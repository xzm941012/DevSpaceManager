using System.Diagnostics;
using System.Text.Json;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class UpdateService
{
    private readonly ManagerConfigStore _configStore;
    private readonly ManagedProcessService _processes;
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public UpdateService(ManagerConfigStore configStore, ManagedProcessService processes)
    {
        _configStore = configStore;
        _processes = processes;
    }

    public async Task<UpdateInfo> CheckDevSpaceAsync(CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestNpmVersionAsync(cancellationToken);
        var current = await GetLocalDevSpaceVersionAsync(cancellationToken);
        var hasUpdate = TryVersion(latest, out var latestVersion) &&
                        TryVersion(current, out var currentVersion) &&
                        latestVersion > currentVersion;
        var notes = hasUpdate
            ? $"发现新版本：{current} -> {latest}。更新说明可到 GitHub 查看。"
            : $"DevSpace 已是最新版本。当前版本：{current}。";
        return new UpdateInfo(current, latest, hasUpdate, notes);
    }

    public async Task UpdateDevSpaceAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Reload();
        if (!File.Exists(config.GitBashPath)) throw new FileNotFoundException("未找到 Git Bash。", config.GitBashPath);
        if (!File.Exists(config.NpmCommand)) throw new FileNotFoundException("未找到 npm 命令。", config.NpmCommand);

        progress?.Report("正在停止 DevSpace...");
        _processes.Stop(ProcessRole.DevSpace);

        progress?.Report("正在安装最新版 @waishnav/devspace...");
        var command = $"{CommandProcess.Quote(CommandProcess.ToBashPath(config.NpmCommand))} install -g @waishnav/devspace";
        await RunStreamingBashAsync(config.GitBashPath, command, AppPaths.UpdateLogPath, cancellationToken);

        progress?.Report("更新完成，现在可以重启 DevSpace。");
        Log.Update("DevSpace 更新完成。");
    }

    private async Task<string> GetLatestNpmVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("https://registry.npmjs.org/@waishnav%2Fdevspace/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("version").GetString() ?? "";
    }

    private async Task<string> GetLocalDevSpaceVersionAsync(CancellationToken cancellationToken)
    {
        var config = _configStore.Reload();
        if (!File.Exists(config.GitBashPath)) return "unknown";
        if (!File.Exists(config.NpmCommand)) return "unknown";
        var command = $"{CommandProcess.Quote(CommandProcess.ToBashPath(config.NpmCommand))} list -g @waishnav/devspace --depth=0";
        var output = await RunCaptureBashAsync(config.GitBashPath, command, cancellationToken);
        var marker = "@waishnav/devspace@";
        var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "unknown";
        var versionStart = index + marker.Length;
        var version = output[versionStart..].Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return version ?? "unknown";
    }

    private static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var clean = value.Trim().TrimStart('v');
        if (!Version.TryParse(clean, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private static async Task<string> RunCaptureBashAsync(string bashPath, string command, CancellationToken cancellationToken)
    {
        var start = CommandProcess.CreateBash(bashPath, command);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {bashPath}");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "检查本地 DevSpace 版本失败。" : error);
        }
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static async Task RunStreamingBashAsync(string bashPath, string command, string logPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var start = CommandProcess.CreateBash(bashPath, command);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {bashPath}");
        process.OutputDataReceived += (_, e) => Append(logPath, e.Data);
        process.ErrorDataReceived += (_, e) => Append(logPath, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"更新失败，npm 退出码：{process.ExitCode}。");
    }

    private static void Append(string path, string? line)
    {
        if (line is null) return;
        File.AppendAllText(path, line + Environment.NewLine);
    }
}

internal sealed record UpdateInfo(string CurrentVersion, string LatestVersion, bool HasUpdate, string Notes);
