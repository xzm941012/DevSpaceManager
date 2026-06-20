namespace DevSpaceManager.Core;

internal static class AppPaths
{
    public static string UserProfile { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string AppDataDirectory { get; } =
        Path.Combine(UserProfile, ".devspace-manager");

    public static string ConfigPath { get; } =
        Path.Combine(AppDataDirectory, "config.json");

    public static string LogDirectory { get; } =
        Path.Combine(AppDataDirectory, "logs");

    public static string WorkerLogPath { get; } =
        Path.Combine(LogDirectory, "worker.log");

    public static string UpdateLogPath { get; } =
        Path.Combine(LogDirectory, "updates.log");

    public static string AppLogPath { get; } =
        Path.Combine(LogDirectory, "app.log");

    public static string DevSpaceAuthPath { get; } =
        Path.Combine(UserProfile, ".devspace", "auth.json");

    public static string CloudflaredDirectory { get; } =
        Path.Combine(UserProfile, ".cloudflared");

    public static string CloudflaredCertPath { get; } =
        Path.Combine(CloudflaredDirectory, "cert.pem");
}
