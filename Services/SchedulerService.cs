using System.Diagnostics;
using DevSpaceManager.Core;
using Microsoft.Win32;

namespace DevSpaceManager.Services;

internal sealed class SchedulerService
{
    private const string WorkerTaskName = "DevSpace Manager Worker";
    private const string TrayTaskName = "DevSpace Manager Tray";
    private const string TrayRunValueName = "DevSpaceManager";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ManagerConfigStore _configStore;

    public SchedulerService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public bool IsWorkerRegistered() => TaskExists(WorkerTaskName);

    public bool IsTrayRegistered() => TrayRunValue() is not null || TaskExists(TrayTaskName);

    public void RegisterTrayAtLogon()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find current executable path.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。");
        key.SetValue(TrayRunValueName, $"\"{exe}\" --tray", RegistryValueKind.String);
        UnregisterTrayTaskOnly();
    }

    public void RegisterWorkerAtBoot(string userName)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find current executable path.");
        var taskRun = $"\\\"{exe}\\\" --worker";
        RunInVisibleTerminal(
            "Register DevSpace Manager Worker",
            $"schtasks /Create /TN \"{WorkerTaskName}\" /SC ONSTART /TR \"{taskRun}\" /RU \"{userName}\" /RP * /RL LIMITED /F");
    }

    public void UnregisterWorker() => RunSchtasks($"/Delete /TN \"{WorkerTaskName}\" /F", ignoreErrors: true);

    public void UnregisterTray()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(TrayRunValueName, throwOnMissingValue: false);
        UnregisterTrayTaskOnly();
    }

    private static string? TrayRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(TrayRunValueName) as string;
    }

    private static void UnregisterTrayTaskOnly() =>
        RunSchtasks($"/Delete /TN \"{TrayTaskName}\" /F", ignoreErrors: true);

    private static bool TaskExists(string name)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{name}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        process?.WaitForExit(5000);
        return process?.ExitCode == 0;
    }

    private static void RunSchtasks(string args, bool ignoreErrors = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start schtasks.exe");
        process.WaitForExit();
        if (!ignoreErrors && process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "schtasks failed." : error);
        }
    }

    private static void RunInVisibleTerminal(string title, string command)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k title {title} && echo Windows may ask for this account password. && {command}",
            UseShellExecute = true
        });
    }
}
