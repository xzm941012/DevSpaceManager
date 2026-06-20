using System.Management;

namespace DevSpaceManager.Services;

internal static class WmiProcessQuery
{
    public static string GetCommandLine(int processId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"select CommandLine from Win32_Process where ProcessId = {processId}");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .Select(item => item["CommandLine"]?.ToString() ?? "")
            .FirstOrDefault() ?? "";
    }
}
