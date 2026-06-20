using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal static class ExecutableResolver
{
    public static string ResolveCloudflared(string configuredPath) =>
        Resolve(configuredPath, "cloudflared.exe", FindCloudflaredCandidates);

    public static bool CloudflaredExists(string configuredPath) =>
        File.Exists(ResolveCloudflared(configuredPath));

    private static string Resolve(
        string configuredPath,
        string executableName,
        Func<IEnumerable<string>> extraCandidates)
    {
        foreach (var candidate in EnumerateCandidates(configuredPath, executableName, extraCandidates))
        {
            if (File.Exists(candidate)) return candidate;
        }

        return configuredPath;
    }

    private static IEnumerable<string> EnumerateCandidates(
        string configuredPath,
        string executableName,
        Func<IEnumerable<string>> extraCandidates)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        foreach (var path in FindOnPath(executableName))
        {
            yield return path;
        }

        foreach (var path in extraCandidates())
        {
            yield return path;
        }
    }

    private static IEnumerable<string> FindOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), executableName);
            }
            catch
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static IEnumerable<string> FindCloudflaredCandidates()
    {
        var user = AppPaths.UserProfile;
        yield return Path.Combine(user, ".devspace-manager", "bin", "cloudflared.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "cloudflared.exe");

        var wingetRoot = Path.Combine(user, "AppData", "Local", "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(wingetRoot)) yield break;

        foreach (var match in Directory.EnumerateFiles(wingetRoot, "cloudflared.exe", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            yield return match;
        }
    }
}
