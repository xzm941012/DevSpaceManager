using System.Text.Json;
using DevSpaceManager.Services;

namespace DevSpaceManager.Core;

internal sealed class ManagerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ManagerConfig Current { get; private set; }

    public ManagerConfigStore()
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Current = Load();
    }

    public ManagerConfig Reload()
    {
        Current = Load();
        return Current;
    }

    public void Save(ManagerConfig config)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        File.WriteAllText(AppPaths.ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        Current = config;
    }

    private ManagerConfig Load()
    {
        if (File.Exists(AppPaths.ConfigPath))
        {
            var backedUpInvalidConfig = false;
            try
            {
                var rawJson = File.ReadAllText(AppPaths.ConfigPath);
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    BackupInvalidConfig();
                    backedUpInvalidConfig = true;
                    throw new JsonException("Manager config is empty.");
                }

                var existing = JsonSerializer.Deserialize<ManagerConfig>(rawJson);
                if (existing is not null)
                {
                    var filled = FillDefaults(existing);
                    var filledJson = JsonSerializer.Serialize(filled, JsonOptions);
                    if (!string.Equals(rawJson.Trim(), filledJson.Trim(), StringComparison.Ordinal))
                    {
                        Save(filled);
                    }

                    return filled;
                }
            }
            catch
            {
                if (!backedUpInvalidConfig) BackupInvalidConfig();
                // Fall through to a fresh config; the settings UI can repair bad values.
            }
        }

        var fresh = FillDefaults(new ManagerConfig());
        Save(fresh);
        return fresh;
    }

    private static void BackupInvalidConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigPath)) return;
            var backupPath = $"{AppPaths.ConfigPath}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(AppPaths.ConfigPath, backupPath, overwrite: false);
        }
        catch
        {
            // Best-effort backup only.
        }
    }

    private static ManagerConfig FillDefaults(ManagerConfig config)
    {
        var user = AppPaths.UserProfile;
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ??
                      Path.Combine(user, "AppData", "Local", "nvm");
        var nodeDir = Path.Combine(nvmHome, $"v{config.NodeVersion}");
        var devSpacePath = Path.Combine(nodeDir, "devspace");
        var npmPath = Path.Combine(nodeDir, "npm");
        config.NodeDirectory = Blank(config.NodeDirectory) ? nodeDir : config.NodeDirectory;
        config.DevSpaceCommand = NeedsUnixShim(config.DevSpaceCommand, config.NodeDirectory, "devspace")
            ? devSpacePath
            : config.DevSpaceCommand;
        config.NpmCommand = NeedsUnixShim(config.NpmCommand, config.NodeDirectory, "npm")
            ? npmPath
            : config.NpmCommand;
        config.GitBashPath = Blank(config.GitBashPath)
            ? FindGitBash()
            : config.GitBashPath;
        config.DevSpaceConfigPath = Blank(config.DevSpaceConfigPath)
            ? Path.Combine(user, ".devspace", "config.json")
            : config.DevSpaceConfigPath;
        config.CloudflaredConfigPath = Blank(config.CloudflaredConfigPath)
            ? Path.Combine(user, ".cloudflared", "config.yml")
            : config.CloudflaredConfigPath;
        config.DevSpacePort = config.DevSpacePort is >= 1 and <= 65535
            ? config.DevSpacePort
            : DerivePort(config.LocalHealthUrl, config.DevSpaceConfigPath);
        config.LocalHealthUrl = $"http://127.0.0.1:{config.DevSpacePort}/healthz";
        config.FixedPublicBaseUrl = Blank(config.FixedPublicBaseUrl)
            ? (Blank(config.PublicBaseUrl) ? DerivePublicBaseUrl(config.PublicHealthUrl) : config.PublicBaseUrl.TrimEnd('/'))
            : config.FixedPublicBaseUrl.TrimEnd('/');
        config.PublicBaseUrl = Blank(config.PublicBaseUrl)
            ? config.FixedPublicBaseUrl
            : config.PublicBaseUrl.TrimEnd('/');
        config.PublicHealthUrl = $"{config.PublicBaseUrl}/healthz";
        config.RequestProxyPort = config.RequestProxyPort is >= 1 and <= 65535
            ? config.RequestProxyPort
            : 17676;
        config.LocalDebugPort = config.LocalDebugPort is >= 1024 and <= 65535
            ? config.LocalDebugPort
            : 9223;
        config.UpdateCheckHours = config.UpdateCheckHours is >= 1 and <= 168
            ? config.UpdateCheckHours
            : 24;
        config.DevSpaceAgentDir = Blank(config.DevSpaceAgentDir)
            ? Path.Combine(user, ".codex")
            : config.DevSpaceAgentDir;
        if (config.BrowserProfiles.Count == 0)
        {
            config.BrowserProfiles.Add(new BrowserProfileConfig
            {
                Id = "default",
                Name = "默认",
                UserDataFolder = Path.Combine(AppPaths.BrowserProfilesDirectory, "default"),
                Language = "zh-CN"
            });
        }

        foreach (var profile in config.BrowserProfiles)
        {
            profile.Id = Blank(profile.Id) ? "default" : profile.Id.Trim();
            profile.Name = Blank(profile.Name) ? profile.Id : profile.Name.Trim();
            profile.UserDataFolder = Blank(profile.UserDataFolder)
                ? Path.Combine(AppPaths.BrowserProfilesDirectory, profile.Id)
                : profile.UserDataFolder;
            profile.Language = Blank(profile.Language) ? "zh-CN" : profile.Language.Trim();
        }

        if (Blank(config.ActiveBrowserProfileId) ||
            config.BrowserProfiles.All(profile => !string.Equals(profile.Id, config.ActiveBrowserProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            config.ActiveBrowserProfileId = config.BrowserProfiles[0].Id;
        }

        config.CloudflaredPath = Blank(config.CloudflaredPath)
            ? ExecutableResolver.ResolveCloudflared("cloudflared.exe")
            : ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        config.CloudflareTunnelName = Blank(config.CloudflareTunnelName)
            ? DefaultTunnelName()
            : config.CloudflareTunnelName.Trim();
        config.CloudflaredProtocol = Blank(config.CloudflaredProtocol) ? "auto" : config.CloudflaredProtocol;
        return config;
    }

    private static string DefaultTunnelName()
    {
        var machine = Environment.MachineName.ToLowerInvariant();
        var safeMachine = new string(machine.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safeMachine) ? "devspace-local" : $"devspace-{safeMachine}";
    }

    private static string FindGitBash()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "git-bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "usr", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "git-bash.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "bash.exe";
    }

    private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);

    private static bool NeedsUnixShim(string currentPath, string nodeDirectory, string baseName)
    {
        if (Blank(currentPath)) return true;

        var expectedCmd = Path.Combine(nodeDirectory, $"{baseName}.cmd");
        var expectedPs1 = Path.Combine(nodeDirectory, $"{baseName}.ps1");
        return string.Equals(currentPath, expectedCmd, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(currentPath, expectedPs1, StringComparison.OrdinalIgnoreCase);
    }

    private static string DerivePublicBaseUrl(string healthUrl)
    {
        if (Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }

        return "https://devspace.onemem.cc";
    }

    private static int DerivePort(string localHealthUrl, string devspaceConfigPath)
    {
        if (Uri.TryCreate(localHealthUrl, UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65535)
        {
            return uri.Port;
        }

        if (File.Exists(devspaceConfigPath))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(devspaceConfigPath));
                if (json.RootElement.TryGetProperty("port", out var port) &&
                    port.TryGetInt32(out var value) &&
                    value is >= 1 and <= 65535)
                {
                    return value;
                }
            }
            catch
            {
                // Keep the default below if the user config is temporarily invalid.
            }
        }

        return 7676;
    }
}
