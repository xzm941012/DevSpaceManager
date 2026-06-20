using System.Diagnostics;

namespace DevSpaceManager.Services;

internal static class CommandProcess
{
    public static ProcessStartInfo Create(string fileName, string arguments)
    {
        if (IsCommandScript(fileName))
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"\"{fileName}\" {arguments}\""
            };
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments
        };
    }

    public static ProcessStartInfo CreateBash(string bashPath, string command)
    {
        var start = new ProcessStartInfo
        {
            FileName = bashPath,
            Arguments = $"-lc {Quote(command)}"
        };
        ApplyGitBashPath(start, bashPath);
        return start;
    }

    public static void ApplyGitBashPath(ProcessStartInfo start, string bashPath, string? firstPath = null)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(firstPath))
        {
            paths.Add(firstPath);
        }

        foreach (var path in GetGitBashToolPaths(bashPath))
        {
            paths.Add(path);
        }

        var existing = start.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        paths.AddRange(existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        start.Environment["PATH"] = string.Join(Path.PathSeparator, paths.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsCommandScript(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }

    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static IEnumerable<string> GetGitBashToolPaths(string bashPath)
    {
        var gitRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bashPath) ?? "", ".."));
        var candidates = new[]
        {
            Path.Combine(gitRoot, "usr", "bin"),
            Path.Combine(gitRoot, "mingw64", "bin"),
            Path.Combine(gitRoot, "cmd"),
            Path.GetDirectoryName(bashPath) ?? ""
        };

        return candidates.Where(Directory.Exists);
    }

    public static string ToBashPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            var drive = char.ToLowerInvariant(normalized[0]);
            return $"/{drive}{normalized[2..]}";
        }

        return normalized;
    }
}
