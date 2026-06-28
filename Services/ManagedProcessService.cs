using System.Diagnostics;
using System.Text.RegularExpressions;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class ManagedProcessService : IDisposable
{
    private static readonly Regex TemporaryTunnelUrlPattern = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ManagerConfigStore _configStore;
    private readonly McpProxyService _proxy;
    private readonly PublicEndpointSyncService _publicEndpoints;
    private readonly HealthService _health;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private Process? _devspace;
    private Process? _tunnel;

    public ManagedProcessService(
        ManagerConfigStore configStore,
        McpProxyService proxy,
        PublicEndpointSyncService publicEndpoints,
        HealthService health)
    {
        _configStore = configStore;
        _proxy = proxy;
        _publicEndpoints = publicEndpoints;
        _health = health;
    }

    public bool IsRunning(ProcessRole role)
    {
        var process = role == ProcessRole.DevSpace ? _devspace : _tunnel;
        return process is { HasExited: false } || FindExisting(role) is not null;
    }

    public void Start(ProcessRole role)
    {
        _processGate.Wait();
        try
        {
            var config = _configStore.Reload();
            if (role == ProcessRole.CloudflareTunnel)
            {
                _proxy.EnsureState();
            }

            if (IsRunning(role)) return;

            if (role == ProcessRole.DevSpace)
            {
                _devspace = StartDevSpace(config);
                return;
            }

            _tunnel = StartTunnel(config);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public void Stop(ProcessRole role)
    {
        _processGate.Wait();
        try
        {
            var process = role == ProcessRole.DevSpace ? _devspace : _tunnel;
            if (process is { HasExited: false })
            {
                KillProcessTree(process);
            }

            foreach (var existing in EnumerateExisting(role))
            {
                KillProcessTree(existing);
            }

            if (role == ProcessRole.CloudflareTunnel)
            {
                _proxy.Stop();
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    public void Restart(ProcessRole role)
    {
        Stop(role);
        Start(role);
    }

    public async Task StartAsync(ProcessRole role, CancellationToken cancellationToken = default)
    {
        if (role == ProcessRole.CloudflareTunnel)
        {
            await StartTunnelWhenDevSpaceReadyAsync(cancellationToken);
            return;
        }

        Start(role);
    }

    public async Task RestartAsync(ProcessRole role, CancellationToken cancellationToken = default)
    {
        if (role == ProcessRole.CloudflareTunnel)
        {
            Stop(ProcessRole.CloudflareTunnel);
            await StartTunnelWhenDevSpaceReadyAsync(cancellationToken);
            return;
        }

        Restart(role);
    }

    public void StartAll()
    {
        StartAllAsync().GetAwaiter().GetResult();
    }

    public void StopAll()
    {
        Stop(ProcessRole.CloudflareTunnel);
        Stop(ProcessRole.DevSpace);
        _proxy.Stop();
    }

    public void RestartAll()
    {
        RestartAllAsync().GetAwaiter().GetResult();
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        if (_configStore.Current.AutoStartDevSpace)
        {
            Start(ProcessRole.DevSpace);
        }

        if (_configStore.Current.AutoStartTunnel)
        {
            await StartTunnelWhenDevSpaceReadyAsync(cancellationToken);
        }
    }

    public async Task RestartAllAsync(CancellationToken cancellationToken = default)
    {
        StopAll();
        await StartAllAsync(cancellationToken);
    }

    private async Task StartTunnelWhenDevSpaceReadyAsync(CancellationToken cancellationToken)
    {
        _proxy.EnsureState();
        Start(ProcessRole.DevSpace);
        var local = await WaitForLocalHealthAsync(cancellationToken);
        if (!local.Ok)
        {
            throw new InvalidOperationException($"DevSpace 未启动，Cloudflare Tunnel 未启动：{local.Message}");
        }

        Start(ProcessRole.CloudflareTunnel);
    }

    private async Task<(bool Ok, string Message)> WaitForLocalHealthAsync(CancellationToken cancellationToken)
    {
        (bool Ok, string Message) last = (false, "尚未检测。");
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _health.CheckLocalAsync(cancellationToken);
            if (last.Ok)
            {
                return last;
            }

            await Task.Delay(attempt == 1 ? 700 : 1200, cancellationToken);
        }

        return last;
    }

    private static Process StartDevSpace(ManagerConfig config)
    {
        EnsureFile(config.GitBashPath, "Git Bash");
        EnsureFile(config.DevSpaceCommand, "DevSpace command");
        var command = $"{CommandProcess.Quote(CommandProcess.ToBashPath(config.DevSpaceCommand))} serve";
        var start = CommandProcess.CreateBash(config.GitBashPath, command);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = Path.GetDirectoryName(config.DevSpaceConfigPath) ?? AppPaths.UserProfile;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["DEVSPACE_LOG_LEVEL"] = config.DevSpaceLogLevel;
        start.Environment["DEVSPACE_LOG_FORMAT"] = config.DevSpaceLogFormat;
        start.Environment["DEVSPACE_TOOL_MODE"] = config.DevSpaceToolMode;
        start.Environment["DEVSPACE_WIDGETS"] = config.DevSpaceWidgets;
        start.Environment["DEVSPACE_SKILLS"] = config.DevSpaceSkills ? "1" : "0";
        start.Environment["DEVSPACE_AGENT_DIR"] = config.DevSpaceAgentDir;
        if (!string.IsNullOrWhiteSpace(config.DevSpaceSkillPaths))
        {
            start.Environment["DEVSPACE_SKILL_PATHS"] = config.DevSpaceSkillPaths;
        }
        CommandProcess.ApplyGitBashPath(start, config.GitBashPath, config.NodeDirectory);

        return StartWithLogs(start, config.DevSpaceStdoutLog, config.DevSpaceStderrLog);
    }

    private Process StartTunnel(ManagerConfig config)
    {
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        EnsureFile(cloudflaredPath, "cloudflared");
        if (config.UseTemporaryCloudflareTunnel)
        {
            _publicEndpoints.MarkTemporaryPublicBaseUrlPending();
            config = _configStore.Reload();
            var temporaryPort = config.RequestProxyEnabled ? config.RequestProxyPort : config.DevSpacePort;
            var temporaryArgs = "tunnel";
            var protocol = string.IsNullOrWhiteSpace(config.CloudflaredProtocol)
                ? "auto"
                : config.CloudflaredProtocol.Trim().ToLowerInvariant();
            if (protocol is "auto" or "http2")
            {
                temporaryArgs += " --protocol http2";
            }
            else if (protocol == "quic")
            {
                temporaryArgs += " --protocol quic";
            }
            temporaryArgs += $" --url http://127.0.0.1:{temporaryPort}";
            AppendLine(config.TunnelStdoutLog, $"临时 tunnel 模式已启用。公网地址会由 cloudflared 输出，通常是 https://*.trycloudflare.com；每次启动都会变化。");
            var temporaryStart = CommandProcess.Create(cloudflaredPath, temporaryArgs);
            temporaryStart.UseShellExecute = false;
            temporaryStart.CreateNoWindow = true;
            temporaryStart.WorkingDirectory = AppPaths.UserProfile;
            temporaryStart.RedirectStandardOutput = true;
            temporaryStart.RedirectStandardError = true;
            return StartWithLogs(
                temporaryStart,
                config.TunnelStdoutLog,
                config.TunnelStderrLog,
                HandleTemporaryTunnelLine,
                HandleTemporaryTunnelLine);
        }

        _publicEndpoints.SyncCurrentModeToConfigs();
        var args = "tunnel";
        if (string.Equals(config.CloudflaredProtocol, "http2", StringComparison.OrdinalIgnoreCase))
        {
            args += " --protocol http2";
        }
        else if (string.Equals(config.CloudflaredProtocol, "quic", StringComparison.OrdinalIgnoreCase))
        {
            args += " --protocol quic";
        }
        args += $" run {QuoteArg(config.CloudflareTunnelName)}";

        var start = CommandProcess.Create(cloudflaredPath, args);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = Path.GetDirectoryName(config.CloudflaredConfigPath) ?? AppPaths.UserProfile;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return StartWithLogs(start, config.TunnelStdoutLog, config.TunnelStderrLog);
    }

    private void HandleTemporaryTunnelLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = TemporaryTunnelUrlPattern.Match(line);
        if (!match.Success) return;

        if (_publicEndpoints.TryApplyTemporaryPublicBaseUrl(match.Value))
        {
            AppendLine(_configStore.Current.TunnelStdoutLog, $"已同步临时公网地址到 DevSpace 配置：{match.Value}");
            _ = Task.Run(() =>
            {
                try
                {
                    Restart(ProcessRole.DevSpace);
                }
                catch (Exception ex)
                {
                    Log.Worker($"Restart DevSpace after temporary tunnel URL sync failed: {ex.Message}");
                }
            });
        }
    }

    private static Process StartWithLogs(
        ProcessStartInfo start,
        string stdoutPath,
        string stderrPath,
        Action<string?>? onOutputLine = null,
        Action<string?>? onErrorLine = null)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            AppendLine(stdoutPath, e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            AppendLine(stderrPath, e.Data);
            onErrorLine?.Invoke(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private IEnumerable<Process> EnumerateExisting(ProcessRole role)
    {
        var config = _configStore.Current;
        var names = role == ProcessRole.DevSpace
            ? new[] { "node", "bash", "sh" }
            : new[] { "cloudflared" };

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (MatchesRole(process, role, config)) yield return process;
            }
        }
    }

    private Process? FindExisting(ProcessRole role) => EnumerateExisting(role).FirstOrDefault();

    private static bool MatchesRole(Process process, ProcessRole role, ManagerConfig config)
    {
        try
        {
            var commandLine = WmiProcessQuery.GetCommandLine(process.Id);
            if (role == ProcessRole.DevSpace)
            {
                return commandLine.Contains("devspace", StringComparison.OrdinalIgnoreCase) ||
                       commandLine.Contains("@waishnav/devspace", StringComparison.OrdinalIgnoreCase);
            }

            if (!commandLine.Contains("cloudflared", StringComparison.OrdinalIgnoreCase) ||
                !commandLine.Contains("tunnel", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (config.UseTemporaryCloudflareTunnel)
            {
                var temporaryPort = PublicEndpointSyncService.CloudflaredServicePort(config);
                return commandLine.Contains($"--url http://localhost:{temporaryPort}", StringComparison.OrdinalIgnoreCase) ||
                       commandLine.Contains($"--url http://127.0.0.1:{temporaryPort}", StringComparison.OrdinalIgnoreCase);
            }

            return commandLine.Contains("run", StringComparison.OrdinalIgnoreCase) &&
                   commandLine.Contains(config.CloudflareTunnelName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void KillProcessTree(Process process)
    {
        if (process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }

    private static void EnsureFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{label} was not found: {path}", path);
        }
    }

    private static void AppendLine(string path, string? line)
    {
        if (line is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string QuoteArg(string value) =>
        value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    public void Dispose()
    {
        _devspace?.Dispose();
        _tunnel?.Dispose();
        _processGate.Dispose();
        _proxy.Stop();
    }
}
