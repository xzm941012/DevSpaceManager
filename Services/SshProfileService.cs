using System.Text;
using System.Text.Json;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class SshProfileService
{
    private const string SshMcpName = "devspace-ssh";

    private readonly ManagerConfigStore _configStore;

    public SshProfileService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public object ListPublic()
    {
        var config = _configStore.Reload();
        return new
        {
            installed = IsMcpInstalled(),
            codexConfigPath = AppPaths.CodexConfigPath,
            templates = SshPolicy.Templates.Select(template => new
            {
                template.Id,
                template.Name,
                template.Description
            }).ToArray(),
            profiles = config.SshProfiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(PublicProfile)
                .ToArray()
        };
    }

    public object Save(SshProfileDraft draft)
    {
        ValidateDraft(draft);
        var config = _configStore.Reload();
        var now = DateTimeOffset.Now;
        SshProfileConfig profile;

        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            var id = CreateProfileId(draft.Name, config.SshProfiles.Select(item => item.Id));
            profile = new SshProfileConfig
            {
                Id = id,
                CreatedAt = now,
                AiEnabled = true
            };
            config.SshProfiles.Add(profile);
        }
        else
        {
            profile = config.SshProfiles.FirstOrDefault(item => string.Equals(item.Id, draft.Id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("未找到 SSH 配置。");
        }

        profile.Name = draft.Name.Trim();
        profile.EncryptedHost = SshSecretProtector.Protect(draft.Host.Trim());
        profile.EncryptedPort = SshSecretProtector.Protect(draft.Port.ToString());
        profile.EncryptedUsername = SshSecretProtector.Protect(draft.Username.Trim());
        if (draft.Password is not null)
        {
            profile.EncryptedPassword = SshSecretProtector.Protect(draft.Password);
        }
        profile.AiEnabled = draft.AiEnabled;
        profile.SecurityMode = SshPolicy.NormalizeMode(draft.SecurityMode);
        profile.PolicyTemplate = NormalizeTemplate(draft.PolicyTemplate);
        profile.AllowPatterns = NormalizePatterns(draft.AllowPatterns);
        profile.DenyPatterns = NormalizePatterns(draft.DenyPatterns);
        profile.Description = (draft.Description ?? "").Trim();
        profile.IdleTimeoutMinutes = Math.Clamp(draft.IdleTimeoutMinutes, 1, 1440);
        profile.CommandTimeoutSeconds = Math.Clamp(draft.CommandTimeoutSeconds, 1, 3600);
        profile.MaxOutputBytes = Math.Clamp(draft.MaxOutputBytes, 4096, 4 * 1024 * 1024);
        profile.UpdatedAt = now;

        _configStore.Save(config);
        return ListPublic();
    }

    public object SetAiEnabled(string id, bool enabled)
    {
        var config = _configStore.Reload();
        var profile = FindMutable(config, id);
        profile.AiEnabled = enabled;
        profile.UpdatedAt = DateTimeOffset.Now;
        _configStore.Save(config);
        return ListPublic();
    }

    public object Delete(string id)
    {
        var config = _configStore.Reload();
        var removed = config.SshProfiles.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) throw new InvalidOperationException("未找到 SSH 配置。");
        _configStore.Save(config);
        return ListPublic();
    }

    public async Task<object> TestAsync(SshProfileDraft draft, CancellationToken cancellationToken)
    {
        ValidateDraft(draft);
        var temp = new SshProfileConfig
        {
            Id = string.IsNullOrWhiteSpace(draft.Id) ? "test" : draft.Id,
            Name = draft.Name,
            EncryptedHost = SshSecretProtector.Protect(draft.Host),
            EncryptedPort = SshSecretProtector.Protect(draft.Port.ToString()),
            EncryptedUsername = SshSecretProtector.Protect(draft.Username),
            EncryptedPassword = SshSecretProtector.Protect(draft.Password ?? FindExistingPassword(draft.Id)),
            CommandTimeoutSeconds = Math.Clamp(draft.CommandTimeoutSeconds, 1, 3600),
            MaxOutputBytes = Math.Clamp(draft.MaxOutputBytes, 4096, 4 * 1024 * 1024)
        };

        if (string.IsNullOrWhiteSpace(GetPassword(temp)))
        {
            throw new InvalidOperationException("请填写密码后再测试连接。");
        }

        var result = await SshSessionManager.TestConnectionAsync(temp, cancellationToken);
        return new
        {
            ok = result.Ok,
            result.Message,
            result.ElapsedMs
        };
    }

    public object InstallMcp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.CodexConfigPath)!);
        var currentExe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法定位当前程序路径。");
        var block = BuildMcpTomlBlock(ResolveMcpAssemblyPath(currentExe));

        var existing = File.Exists(AppPaths.CodexConfigPath)
            ? File.ReadAllText(AppPaths.CodexConfigPath)
            : "";
        var next = UpsertMcpBlock(existing, block);
        File.WriteAllText(AppPaths.CodexConfigPath, next, new UTF8Encoding(false));
        return ListPublic();
    }

    public bool IsMcpInstalled()
    {
        if (!File.Exists(AppPaths.CodexConfigPath)) return false;
        foreach (var rawLine in File.ReadLines(AppPaths.CodexConfigPath))
        {
            var line = rawLine.Trim();
            if (line.Equals($"[mcp_servers.{SshMcpName}]", StringComparison.OrdinalIgnoreCase) ||
                line.Equals($"[mcp_servers.\"{SshMcpName}\"]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<SshProfileConfig> EnabledProfiles() =>
        _configStore.Reload().SshProfiles
            .Where(profile => profile.AiEnabled)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public SshProfileConfig GetEnabledProfile(string id)
    {
        var profile = _configStore.Reload().SshProfiles.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("未找到 SSH 配置。");
        if (!profile.AiEnabled) throw new InvalidOperationException("该 SSH 配置未允许 AI 访问。");
        return profile;
    }

    public static SshConnectionProfile ToConnectionProfile(SshProfileConfig profile) => new(
        profile.Id,
        profile.Name,
        GetHost(profile),
        GetPort(profile),
        GetUsername(profile),
        GetPassword(profile),
        profile.SecurityMode,
        profile.PolicyTemplate,
        profile.AllowPatterns,
        profile.DenyPatterns,
        profile.IdleTimeoutMinutes,
        profile.CommandTimeoutSeconds,
        profile.MaxOutputBytes);

    private static object PublicProfile(SshProfileConfig profile) => new
    {
        profile.Id,
        profile.Name,
        host = SafeUnprotect(profile.EncryptedHost),
        port = SafeGetPort(profile),
        username = SafeUnprotect(profile.EncryptedUsername),
        passwordConfigured = !string.IsNullOrWhiteSpace(profile.EncryptedPassword),
        profile.AiEnabled,
        securityMode = SshPolicy.NormalizeMode(profile.SecurityMode),
        profile.PolicyTemplate,
        profile.AllowPatterns,
        profile.DenyPatterns,
        profile.Description,
        profile.IdleTimeoutMinutes,
        profile.CommandTimeoutSeconds,
        profile.MaxOutputBytes,
        profile.CreatedAt,
        profile.UpdatedAt
    };

    private string FindExistingPassword(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        return _configStore.Reload().SshProfiles
            .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            is { } profile
            ? GetPassword(profile)
            : "";
    }

    private static string GetHost(SshProfileConfig profile) => SshSecretProtector.Unprotect(profile.EncryptedHost).Trim();

    private static int GetPort(SshProfileConfig profile)
    {
        var text = SshSecretProtector.Unprotect(profile.EncryptedPort).Trim();
        if (int.TryParse(text, out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        throw new InvalidOperationException("SSH 端口配置无效，请在设置中重新保存。");
    }

    private static string GetUsername(SshProfileConfig profile) => SshSecretProtector.Unprotect(profile.EncryptedUsername).Trim();

    private static string GetPassword(SshProfileConfig profile) => SshSecretProtector.Unprotect(profile.EncryptedPassword);

    private static string SafeUnprotect(string protectedValue)
    {
        try
        {
            return SshSecretProtector.Unprotect(protectedValue);
        }
        catch
        {
            return "";
        }
    }

    private static int SafeGetPort(SshProfileConfig profile)
    {
        try
        {
            return GetPort(profile);
        }
        catch
        {
            return 22;
        }
    }

    private static void ValidateDraft(SshProfileDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidOperationException("名称不能为空。");
        if (draft.Name.Trim().Length > 80) throw new InvalidOperationException("名称太长。");
        if (string.IsNullOrWhiteSpace(draft.Host)) throw new InvalidOperationException("主机不能为空。");
        if (draft.Host.Trim().Length > 255) throw new InvalidOperationException("主机太长。");
        if (draft.Port is < 1 or > 65535) throw new InvalidOperationException("端口必须在 1-65535 之间。");
        if (string.IsNullOrWhiteSpace(draft.Username)) throw new InvalidOperationException("用户名不能为空。");
        if (draft.Username.Trim().Length > 128) throw new InvalidOperationException("用户名太长。");
        if (draft.Password is { Length: > 4096 }) throw new InvalidOperationException("密码太长。");
    }

    private static SshProfileConfig FindMutable(ManagerConfig config, string id) =>
        config.SshProfiles.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("未找到 SSH 配置。");

    private static List<string> NormalizePatterns(IEnumerable<string>? patterns) =>
        (patterns ?? [])
        .Select(item => item.Trim())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .Take(80)
        .ToList();

    private static string NormalizeTemplate(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return SshPolicy.Templates.Any(item => item.Id == normalized) ? normalized : "inspection";
    }

    private static string CreateProfileId(string name, IEnumerable<string> existingIds)
    {
        var baseId = new string(name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "ssh";
        if (baseId.Length > 32) baseId = baseId[..32].Trim('-');

        var used = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        var index = 2;
        while (used.Contains(id))
        {
            id = $"{baseId}-{index++}";
        }

        return id;
    }

    private static string ResolveMcpAssemblyPath(string currentExe)
    {
        var dll = Path.ChangeExtension(currentExe, ".dll");
        return File.Exists(dll) ? dll : currentExe;
    }

    private static string BuildMcpTomlBlock(string assemblyPath) =>
        $"""

        [mcp_servers."{SshMcpName}"]
        command = "dotnet"
        args = ["{EscapeTomlString(assemblyPath)}", "--ssh-mcp"]
        startup_timeout_ms = 20000
        """;

    private static string UpsertMcpBlock(string existing, string block)
    {
        var lines = existing.ReplaceLineEndings("\n").Split('\n').ToList();
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Equals($"[mcp_servers.{SshMcpName}]", StringComparison.OrdinalIgnoreCase) ||
                line.Equals($"[mcp_servers.\"{SshMcpName}\"]", StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }

        if (start >= 0)
        {
            var end = lines.Count;
            for (var i = start + 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    end = i;
                    break;
                }
            }

            lines.RemoveRange(start, end - start);
            lines.InsertRange(start, block.Trim().Split('\n'));
            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        var prefix = string.IsNullOrWhiteSpace(existing) ? "" : existing.TrimEnd() + Environment.NewLine;
        return prefix + block.Trim() + Environment.NewLine;
    }

    private static string EscapeTomlString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal sealed record SshProfileDraft(
    string? Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string? Password,
    bool AiEnabled,
    string SecurityMode,
    string PolicyTemplate,
    IReadOnlyList<string>? AllowPatterns,
    IReadOnlyList<string>? DenyPatterns,
    int IdleTimeoutMinutes,
    int CommandTimeoutSeconds,
    int MaxOutputBytes,
    string? Description);
