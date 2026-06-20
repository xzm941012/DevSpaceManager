using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal static class Log
{
    public static void Worker(string message) => Write(AppPaths.WorkerLogPath, message);

    public static void Update(string message) => Write(AppPaths.UpdateLogPath, message);

    public static void App(string message) => Write(AppPaths.AppLogPath, message);

    private static void Write(string path, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
