using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DevSpaceManager.Services;

internal sealed class SshSessionManager : IDisposable
{
    private readonly SshProfileService _profiles;
    private readonly ConcurrentDictionary<string, ManagedSshSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _cleanupTimer;
    private bool _disposed;

    public SshSessionManager(SshProfileService profiles)
    {
        _profiles = profiles;
        _cleanupTimer = new System.Threading.Timer(_ => CleanupIdleSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public object ListServers() => new
    {
        servers = _profiles.EnabledProfiles().Select(profile => new
        {
            serverId = profile.Id,
            profile.Name,
            profile.Description,
            securityMode = SshPolicy.NormalizeMode(profile.SecurityMode),
            profile.PolicyTemplate,
            idleTimeoutSeconds = profile.IdleTimeoutMinutes * 60,
            commandTimeoutSeconds = profile.CommandTimeoutSeconds
        }).ToArray()
    };

    public async Task<object> OpenSessionAsync(string serverId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var profile = SshProfileService.ToConnectionProfile(_profiles.GetEnabledProfile(serverId));
        var session = await ManagedSshSession.OpenAsync(profile, cancellationToken);
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("SSH 会话 ID 冲突。");
        }

        Log.App($"SSH session opened id={session.Id} server={profile.Id} mode={profile.SecurityMode}");
        return new
        {
            sessionId = session.Id,
            serverId = profile.Id,
            profile.Name,
            status = "open",
            createdAt = session.CreatedAt,
            idleTimeoutSeconds = profile.IdleTimeoutMinutes * 60
        };
    }

    public async Task<object> ExecuteAsync(string sessionId, string command, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("命令不能为空。");
        if (!_sessions.TryGetValue(sessionId, out var session)) throw new InvalidOperationException("SSH 会话不存在或已过期。");

        var policy = SshPolicy.Evaluate(session.Profile, command);
        Audit(session.Profile.Id, session.Id, command, policy);
        if (!policy.IsAllowed)
        {
            throw new InvalidOperationException($"命令被策略拒绝：{policy.Reason}");
        }

        var seconds = Math.Clamp(timeoutSeconds ?? session.Profile.CommandTimeoutSeconds, 1, 3600);
        return await session.ExecuteAsync(command, TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    public object ListSessions()
    {
        CleanupIdleSessions();
        return new
        {
            sessions = _sessions.Values
                .OrderBy(item => item.CreatedAt)
                .Select(item => new
                {
                    sessionId = item.Id,
                    serverId = item.Profile.Id,
                    item.Profile.Name,
                    createdAt = item.CreatedAt,
                    lastActivityAt = item.LastActivityAt,
                    idleSeconds = (int)(DateTimeOffset.Now - item.LastActivityAt).TotalSeconds,
                    commandCount = item.CommandCount
                })
                .ToArray()
        };
    }

    public object CloseSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) throw new InvalidOperationException("SSH 会话不存在或已关闭。");
        session.Dispose();
        Log.App($"SSH session closed id={sessionId} server={session.Profile.Id}");
        return new { sessionId, closed = true };
    }

    public static async Task<SshConnectionTestResult> TestConnectionAsync(DevSpaceManager.Core.SshProfileConfig profile, CancellationToken cancellationToken)
    {
        return await TestConnectionAsync(SshProfileService.ToConnectionProfile(profile), cancellationToken);
    }

    private static async Task<SshConnectionTestResult> TestConnectionAsync(SshConnectionProfile profile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = CreateClient(profile);
        using var registration = cancellationToken.Register(() =>
        {
            try { client.Disconnect(); } catch { }
        });
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.CommandTimeoutSeconds, 5, 60));

        await Task.Run(() =>
        {
            client.Connect();
            using var command = client.CreateCommand("echo devspace-ssh-ok");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            var output = command.Execute();
            if (!output.Contains("devspace-ssh-ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("连接成功，但测试命令返回异常。");
            }
        }, cancellationToken);

        stopwatch.Stop();
        return new SshConnectionTestResult(true, "连接成功。", stopwatch.ElapsedMilliseconds);
    }

    private void CleanupIdleSessions()
    {
        var now = DateTimeOffset.Now;
        foreach (var (id, session) in _sessions.ToArray())
        {
            var timeout = TimeSpan.FromMinutes(Math.Clamp(session.Profile.IdleTimeoutMinutes, 1, 1440));
            if (now - session.LastActivityAt <= timeout) continue;
            if (_sessions.TryRemove(id, out var removed))
            {
                removed.Dispose();
                Log.App($"SSH session idle timeout id={id} server={removed.Profile.Id}");
            }
        }
    }

    private static SshClient CreateClient(SshConnectionProfile profile)
    {
        var auth = new PasswordAuthenticationMethod(profile.Username, profile.Password);
        var connection = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.CommandTimeoutSeconds, 5, 60))
        };
        return new SshClient(connection);
    }

    private static void Audit(string serverId, string sessionId, string command, PolicyResult policy)
    {
        var snippet = command.ReplaceLineEndings(" ");
        if (snippet.Length > 240) snippet = snippet[..240] + "...";
        Log.App($"SSH policy server={serverId} session={sessionId} allowed={policy.IsAllowed} reason={policy.Reason} command=\"{snippet.Replace("\"", "\\\"")}\"");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SshSessionManager));
    }

    public void Dispose()
    {
        _disposed = true;
        _cleanupTimer.Dispose();
        foreach (var (_, session) in _sessions)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }

    private sealed class ManagedSshSession : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SshClient _client;
        private readonly ShellStream _shell;
        private bool _disposed;

        private ManagedSshSession(SshConnectionProfile profile, SshClient client, ShellStream shell)
        {
            Profile = profile;
            _client = client;
            _shell = shell;
            Id = $"ssh_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..25];
            CreatedAt = DateTimeOffset.Now;
            LastActivityAt = CreatedAt;
        }

        public string Id { get; }
        public SshConnectionProfile Profile { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset LastActivityAt { get; private set; }
        public int CommandCount { get; private set; }

        public static async Task<ManagedSshSession> OpenAsync(SshConnectionProfile profile, CancellationToken cancellationToken)
        {
            var client = CreateClient(profile);
            try
            {
                await Task.Run(client.Connect, cancellationToken);
                var shell = client.CreateShellStream("devspace-ssh", 120, 40, 1200, 800, 64 * 1024);
                var session = new ManagedSshSession(profile, client, shell);
                await session.InitializeShellAsync(cancellationToken);
                return session;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task<object> ExecuteAsync(string commandText, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_disposed || !_client.IsConnected) throw new InvalidOperationException("SSH 会话已关闭。");
                var stopwatch = Stopwatch.StartNew();
                var marker = $"__DEVSPACE_SSH_DONE_{Guid.NewGuid():N}__";
                var output = await ExecuteShellCommandAsync(commandText, marker, timeout, cancellationToken);

                stopwatch.Stop();
                LastActivityAt = DateTimeOffset.Now;
                CommandCount++;

                var truncated = false;
                var stdout = Truncate(output.Stdout, Profile.MaxOutputBytes, ref truncated);
                var stderr = "";

                return new
                {
                    sessionId = Id,
                    serverId = Profile.Id,
                    stdout,
                    stderr,
                    exitCode = output.ExitCode,
                    timedOut = output.TimedOut,
                    truncated,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    commandCount = CommandCount
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task InitializeShellAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(200, cancellationToken);
            DrainShell();
            var marker = $"__DEVSPACE_SSH_READY_{Guid.NewGuid():N}__";
            _shell.WriteLine($"printf '{marker}\\n'");
            var ready = await ReadUntilAsync(marker, TimeSpan.FromSeconds(10), cancellationToken);
            if (!ready.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SSH shell 初始化超时。");
            }

            DrainShell();
        }

        private async Task<ShellCommandResult> ExecuteShellCommandAsync(
            string commandText,
            string marker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DrainShell();
            _shell.WriteLine(commandText);
            _shell.WriteLine($"__devspace_rc=$?; printf '\\n{marker}:%s\\n' \"$__devspace_rc\"");

            var raw = await ReadUntilAsync(marker, timeout, cancellationToken);
            var markerIndex = raw.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return new ShellCommandResult(CleanShellOutput(raw, commandText), -1, true);
            }

            var before = raw[..markerIndex];
            var after = raw[(markerIndex + marker.Length)..];
            var exitCode = ParseExitCode(after);
            return new ShellCommandResult(CleanShellOutput(before, commandText), exitCode, false);
        }

        private async Task<string> ReadUntilAsync(string marker, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.Now + timeout;
            var builder = new StringBuilder();
            while (DateTimeOffset.Now < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (_shell.DataAvailable)
                {
                    builder.Append(_shell.Read());
                    if (builder.ToString().Contains(marker, StringComparison.Ordinal))
                    {
                        return builder.ToString();
                    }
                }

                await Task.Delay(50, cancellationToken);
            }

            return builder.ToString();
        }

        private void DrainShell()
        {
            while (_shell.DataAvailable)
            {
                _ = _shell.Read();
            }
        }

        private static int ParseExitCode(string text)
        {
            var digits = new string(text.TakeWhile(ch => ch == ':' || char.IsWhiteSpace(ch) || char.IsDigit(ch))
                .Where(char.IsDigit)
                .ToArray());
            return int.TryParse(digits, out var code) ? code : -1;
        }

        private static string CleanShellOutput(string raw, string commandText)
        {
            var lines = raw.Replace("\r", "")
                .Split('\n')
                .Where(line => !string.Equals(line.Trim(), commandText.Trim(), StringComparison.Ordinal))
                .Where(line => !line.TrimStart().StartsWith("__devspace_rc=", StringComparison.Ordinal))
                .ToArray();
            return string.Join(Environment.NewLine, lines).Trim();
        }

        private static string Truncate(string value, int maxBytes, ref bool truncated)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length <= maxBytes) return value;
            truncated = true;
            return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "\n[output truncated]";
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                _shell.Dispose();
                if (_client.IsConnected) _client.Disconnect();
            }
            catch
            {
            }
            _client.Dispose();
            _lock.Dispose();
        }
    }
}

internal sealed record SshConnectionTestResult(bool Ok, string Message, long ElapsedMs);

internal sealed record ShellCommandResult(string Stdout, int ExitCode, bool TimedOut);

internal sealed record SshConnectionProfile(
    string Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string Password,
    string SecurityMode,
    string PolicyTemplate,
    List<string> AllowPatterns,
    List<string> DenyPatterns,
    int IdleTimeoutMinutes,
    int CommandTimeoutSeconds,
    int MaxOutputBytes) : ISshPolicySubject;
