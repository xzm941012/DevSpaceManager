using System.Diagnostics;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class ManagedProcessService : IDisposable
{
    private readonly ManagerConfigStore _configStore;
    private Process? _devspace;
    private Process? _tunnel;

    public ManagedProcessService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public bool IsRunning(ProcessRole role)
    {
        var process = role == ProcessRole.DevSpace ? _devspace : _tunnel;
        return process is { HasExited: false } || FindExisting(role) is not null;
    }

    public void Start(ProcessRole role)
    {
        if (IsRunning(role)) return;

        var config = _configStore.Reload();
        if (role == ProcessRole.DevSpace)
        {
            _devspace = StartDevSpace(config);
            return;
        }

        _tunnel = StartTunnel(config);
    }

    public void Stop(ProcessRole role)
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
    }

    public void Restart(ProcessRole role)
    {
        Stop(role);
        Start(role);
    }

    public void StartAll()
    {
        if (_configStore.Current.AutoStartDevSpace) Start(ProcessRole.DevSpace);
        if (_configStore.Current.AutoStartTunnel) Start(ProcessRole.CloudflareTunnel);
    }

    public void StopAll()
    {
        Stop(ProcessRole.CloudflareTunnel);
        Stop(ProcessRole.DevSpace);
    }

    public void RestartAll()
    {
        StopAll();
        StartAll();
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

    private static Process StartTunnel(ManagerConfig config)
    {
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        EnsureFile(cloudflaredPath, "cloudflared");
        var args = $"tunnel run {QuoteArg(config.CloudflareTunnelName)}";
        if (string.Equals(config.CloudflaredProtocol, "http2", StringComparison.OrdinalIgnoreCase))
        {
            args += " --protocol http2";
        }
        else if (string.Equals(config.CloudflaredProtocol, "quic", StringComparison.OrdinalIgnoreCase))
        {
            args += " --protocol quic";
        }

        var start = CommandProcess.Create(cloudflaredPath, args);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = Path.GetDirectoryName(config.CloudflaredConfigPath) ?? AppPaths.UserProfile;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return StartWithLogs(start, config.TunnelStdoutLog, config.TunnelStderrLog);
    }

    private static Process StartWithLogs(ProcessStartInfo start, string stdoutPath, string stderrPath)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLine(stdoutPath, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(stderrPath, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static IEnumerable<Process> EnumerateExisting(ProcessRole role)
    {
        var names = role == ProcessRole.DevSpace
            ? new[] { "node", "bash", "sh" }
            : new[] { "cloudflared" };

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (MatchesRole(process, role)) yield return process;
            }
        }
    }

    private static Process? FindExisting(ProcessRole role) => EnumerateExisting(role).FirstOrDefault();

    private static bool MatchesRole(Process process, ProcessRole role)
    {
        try
        {
            var commandLine = WmiProcessQuery.GetCommandLine(process.Id);
            if (role == ProcessRole.DevSpace)
            {
                return commandLine.Contains("devspace", StringComparison.OrdinalIgnoreCase) ||
                       commandLine.Contains("@waishnav/devspace", StringComparison.OrdinalIgnoreCase);
            }

            return commandLine.Contains("cloudflared", StringComparison.OrdinalIgnoreCase) &&
                   commandLine.Contains("tunnel", StringComparison.OrdinalIgnoreCase) &&
                   commandLine.Contains("devspace", StringComparison.OrdinalIgnoreCase);
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
    }
}
