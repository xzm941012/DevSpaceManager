using System.Text.Json;
using System.Text.Json.Serialization;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class AdminBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppHost _app;
    private readonly Func<Task>? _reloadChatGptView;

    public AdminBridgeService(AppHost app, Func<Task>? reloadChatGptView = null)
    {
        _app = app;
        _reloadChatGptView = reloadChatGptView;
    }

    public async Task<string> HandleAsync(string rawJson, CancellationToken cancellationToken = default)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(rawJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return Serialize(BridgeResponse.Failed(null, $"请求格式不正确：{ex.Message}"));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return Serialize(BridgeResponse.Failed(request?.Id, "缺少命令。"));
        }

        try
        {
            var data = await DispatchAsync(request.Command, request.Payload, cancellationToken);
            return Serialize(BridgeResponse.Success(request.Id, data));
        }
        catch (Exception ex)
        {
            Log.App($"Bridge command failed: {request.Command} - {ex}");
            return Serialize(BridgeResponse.Failed(request.Id, ex.Message));
        }
    }

    private async Task<object?> DispatchAsync(string command, JsonElement payload, CancellationToken cancellationToken)
    {
        return command switch
        {
            "app.snapshot" => await BuildSnapshotAsync(cancellationToken),
            "app.reloadChatGpt" => await ReloadChatGptViewAsync(),
            "app.exit" => ExitApplication(),
            "devspace.start" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Start),
            "devspace.stop" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Stop),
            "devspace.restart" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Restart),
            "tunnel.start" => await ProcessActionAsync(token => _app.Processes.StartAsync(ProcessRole.CloudflareTunnel, token), cancellationToken),
            "tunnel.stop" => ProcessAction(ProcessRole.CloudflareTunnel, _app.Processes.Stop),
            "tunnel.restart" => await ProcessActionAsync(token => _app.Processes.RestartAsync(ProcessRole.CloudflareTunnel, token), cancellationToken),
            "services.startAll" => await ProcessActionAsync(_app.Processes.StartAllAsync, cancellationToken),
            "services.stopAll" => ProcessAction(_app.Processes.StopAll),
            "services.restartAll" => await ProcessActionAsync(_app.Processes.RestartAllAsync, cancellationToken),
            "profile.list" => BuildProfiles(),
            "profile.switch" => await SwitchProfileAsync(payload),
            "profile.save" => await SaveProfileAsync(payload),
            "profile.delete" => await DeleteProfileAsync(payload),
            "config.saveBasics" => await SaveBasicsAsync(payload),
            "startup.save" => await SaveStartupAsync(payload),
            "debug.save" => await SaveDebugAsync(payload),
            "codex.save" => await SaveCodexEnhancementsAsync(payload),
            "mcp.list" => await _app.MountedMcps.ListAsync(cancellationToken),
            "mcp.setEnabled" => await SetMountedMcpEnabledAsync(payload, cancellationToken),
            "mcp.refresh" => await RefreshMountedMcpAsync(payload, cancellationToken),
            "ssh.list" => _app.SshProfiles.ListPublic(),
            "ssh.save" => _app.SshProfiles.Save(ReadSshDraft(payload)),
            "ssh.delete" => _app.SshProfiles.Delete(RequiredString(payload, "id")),
            "ssh.setAiEnabled" => _app.SshProfiles.SetAiEnabled(RequiredString(payload, "id"), RequiredBool(payload, "enabled")),
            "ssh.test" => await _app.SshProfiles.TestAsync(ReadSshDraft(payload), cancellationToken),
            "ssh.installMcp" => _app.SshProfiles.InstallMcp(),
            "log.tail" => TailLog(payload),
            "environment.check" => await _app.Environment.CheckAsync(),
            "environment.openInstaller" => OpenInstaller(),
            _ => throw new InvalidOperationException($"不支持的命令：{command}")
        };
    }

    private async Task<object> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var config = _app.ConfigStore.Reload();
        var devspaceRunning = _app.Processes.IsRunning(ProcessRole.DevSpace);
        var tunnelRunning = _app.Processes.IsRunning(ProcessRole.CloudflareTunnel);
        var local = await _app.Health.CheckLocalAsync(cancellationToken);
        var pub = await _app.Health.CheckPublicAsync(cancellationToken);

        var publicConfig = PublicConfig(config, _app.Scheduler.IsTrayRegistered());

        return new
        {
            checkedAt = DateTimeOffset.Now,
            config = publicConfig,
            ownerPassword = _app.AuthSecrets.ReadOwnerPassword(),
            services = new
            {
                devspace = new { running = devspaceRunning || local.Ok, healthOk = local.Ok, message = local.Message },
                tunnel = new { running = tunnelRunning || pub.Ok, healthOk = pub.Ok, message = pub.Message }
            },
            logs = new
            {
                devspaceOut = config.DevSpaceStdoutLog,
                devspaceErr = config.DevSpaceStderrLog,
                tunnelOut = config.TunnelStdoutLog,
                tunnelErr = config.TunnelStderrLog
            }
        };
    }

    private object BuildProfiles()
    {
        var config = _app.ConfigStore.Reload();
        return new
        {
            activeProfileId = config.ActiveBrowserProfileId,
            profiles = config.BrowserProfiles.Select(PublicProfile).ToArray()
        };
    }

    private async Task<object> SwitchProfileAsync(JsonElement payload)
    {
        var id = RequiredString(payload, "id");
        var config = _app.ConfigStore.Reload();
        if (config.BrowserProfiles.All(profile => !string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("未找到浏览器配置。");
        }

        config.ActiveBrowserProfileId = id;
        _app.ConfigStore.Save(config);
        await Task.CompletedTask;

        return BuildProfiles();
    }

    private async Task<object> SaveProfileAsync(JsonElement payload)
    {
        var id = payload.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() ?? "" : "";
        var name = RequiredString(payload, "name");
        var proxyServer = payload.TryGetProperty("proxyServer", out var proxyElement) ? NormalizeProxyServer(proxyElement.GetString() ?? "") : "";
        var language = payload.TryGetProperty("language", out var languageElement) ? NormalizeLanguage(languageElement.GetString() ?? "") : "zh-CN";
        var userDataFolder = payload.TryGetProperty("userDataFolder", out var folderElement) ? folderElement.GetString()?.Trim() ?? "" : "";

        var config = _app.ConfigStore.Reload();
        var isNew = string.IsNullOrWhiteSpace(id);
        BrowserProfileConfig profile;
        if (isNew)
        {
            id = CreateProfileId(name, config.BrowserProfiles.Select(item => item.Id));
            profile = new BrowserProfileConfig
            {
                Id = id,
                Name = name,
                UserDataFolder = string.IsNullOrWhiteSpace(userDataFolder)
                    ? Path.Combine(AppPaths.BrowserProfilesDirectory, id)
                    : userDataFolder,
                Language = language,
                ProxyServer = proxyServer
            };
            config.BrowserProfiles.Add(profile);
        }
        else
        {
            profile = config.BrowserProfiles.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("未找到浏览器 Profile。");
            profile.Name = name;
            profile.Language = language;
            profile.ProxyServer = proxyServer;
            if (!string.IsNullOrWhiteSpace(userDataFolder))
            {
                profile.UserDataFolder = userDataFolder;
            }
        }

        Directory.CreateDirectory(profile.UserDataFolder);
        _app.ConfigStore.Save(config);
        await Task.CompletedTask;

        return BuildProfiles();
    }

    private async Task<object> DeleteProfileAsync(JsonElement payload)
    {
        var id = RequiredString(payload, "id");
        var config = _app.ConfigStore.Reload();
        if (config.BrowserProfiles.Count <= 1)
        {
            throw new InvalidOperationException("至少需要保留一个浏览器 Profile。");
        }

        var removed = config.BrowserProfiles.RemoveAll(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new InvalidOperationException("未找到浏览器 Profile。");
        }

        var activeChanged = string.Equals(config.ActiveBrowserProfileId, id, StringComparison.OrdinalIgnoreCase);
        if (activeChanged)
        {
            config.ActiveBrowserProfileId = config.BrowserProfiles[0].Id;
        }

        _app.ConfigStore.Save(config);
        if (activeChanged && _reloadChatGptView is not null)
        {
            await _reloadChatGptView();
        }

        return BuildProfiles();
    }

    private async Task<object> ReloadChatGptViewAsync()
    {
        if (_reloadChatGptView is not null)
        {
            await _reloadChatGptView();
        }

        return new { ok = true };
    }

    private static object ExitApplication()
    {
        System.Windows.Forms.Application.Exit();
        return new { ok = true };
    }

    private async Task<object> SaveBasicsAsync(JsonElement payload)
    {
        var config = _app.ConfigStore.Reload();
        if (payload.TryGetProperty("publicBaseUrl", out var publicBaseUrl))
        {
            config.FixedPublicBaseUrl = _app.PublicEndpoints.NormalizeBaseUrl(publicBaseUrl.GetString() ?? "");
            if (!config.UseTemporaryCloudflareTunnel)
            {
                config.PublicBaseUrl = config.FixedPublicBaseUrl;
                config.PublicHealthUrl = $"{config.PublicBaseUrl}/healthz";
            }
        }

        if (payload.TryGetProperty("tunnelName", out var tunnelName))
        {
            config.CloudflareTunnelName = NormalizeTunnelName(tunnelName.GetString() ?? "");
        }

        if (payload.TryGetProperty("devSpacePort", out var portElement) && portElement.TryGetInt32(out var port))
        {
            if (port is < 1 or > 65535) throw new InvalidOperationException("端口必须在 1-65535 之间。");
            config.DevSpacePort = port;
            config.LocalHealthUrl = $"http://127.0.0.1:{port}/healthz";
        }

        if (payload.TryGetProperty("autoStartDevSpace", out var autoDevSpace))
        {
            config.AutoStartDevSpace = autoDevSpace.GetBoolean();
        }

        if (payload.TryGetProperty("autoStartTunnel", out var autoTunnel))
        {
            config.AutoStartTunnel = autoTunnel.GetBoolean();
        }

        if (payload.TryGetProperty("requestProxyEnabled", out var requestProxyEnabled))
        {
            config.RequestProxyEnabled = requestProxyEnabled.GetBoolean();
        }

        if (payload.TryGetProperty("requestProxyPort", out var requestProxyPort) && requestProxyPort.TryGetInt32(out var proxyPort))
        {
            if (proxyPort is < 1 or > 65535) throw new InvalidOperationException("监控代理端口必须在 1-65535 之间。");
            config.RequestProxyPort = proxyPort;
        }

        _app.ConfigStore.Save(config);
        config = _app.PublicEndpoints.SyncCurrentModeToConfigs();
        _app.McpProxy.EnsureState();
        await Task.CompletedTask;
        return PublicConfig(config, _app.Scheduler.IsTrayRegistered());
    }

    private async Task<object> SaveStartupAsync(JsonElement payload)
    {
        var config = _app.ConfigStore.Reload();
        if (payload.TryGetProperty("startWithWindows", out var startWithWindows))
        {
            config.StartWithWindows = startWithWindows.GetBoolean();
        }

        if (payload.TryGetProperty("startMinimizedToTray", out var startMinimizedToTray))
        {
            config.StartMinimizedToTray = startMinimizedToTray.GetBoolean();
        }

        if (config.StartWithWindows)
        {
            _app.Scheduler.RegisterTrayAtLogon();
        }
        else
        {
            _app.Scheduler.UnregisterTray();
        }

        _app.ConfigStore.Save(config);
        await Task.CompletedTask;
        return PublicConfig(config, _app.Scheduler.IsTrayRegistered());
    }

    private async Task<object> SaveDebugAsync(JsonElement payload)
    {
        var config = _app.ConfigStore.Reload();
        if (payload.TryGetProperty("enabled", out var enabled))
        {
            config.LocalDebugEnabled = enabled.GetBoolean();
        }

        if (payload.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var port))
        {
            if (port is < 1024 or > 65535)
            {
                throw new InvalidOperationException("调试端口必须在 1024-65535 之间。");
            }

            config.LocalDebugPort = port;
        }

        _app.ConfigStore.Save(config);
        await Task.CompletedTask;
        return PublicConfig(config, _app.Scheduler.IsTrayRegistered());
    }

    private async Task<object> SaveCodexEnhancementsAsync(JsonElement payload)
    {
        var config = _app.ConfigStore.Reload();
        if (payload.TryGetProperty("codexStyleEnhancementsEnabled", out var enabled))
        {
            config.CodexStyleEnhancementsEnabled = enabled.GetBoolean();
        }

        if (payload.TryGetProperty("codexMessageNotificationsEnabled", out var notificationsEnabled))
        {
            config.CodexMessageNotificationsEnabled = notificationsEnabled.GetBoolean();
        }

        _app.ConfigStore.Save(config);
        await Task.CompletedTask;
        return PublicConfig(config, _app.Scheduler.IsTrayRegistered());
    }

    private async Task<object> SetMountedMcpEnabledAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var name = RequiredString(payload, "name");
        if (!payload.TryGetProperty("enabled", out var enabledElement))
        {
            throw new InvalidOperationException("缺少字段：enabled");
        }

        return await _app.MountedMcps.SetEnabledAsync(name, enabledElement.GetBoolean(), cancellationToken);
    }

    private async Task<object> RefreshMountedMcpAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var name = RequiredString(payload, "name");
        return await _app.MountedMcps.RefreshAsync(name, cancellationToken);
    }

    private object TailLog(JsonElement payload)
    {
        var name = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "devspaceOut";
        var lines = payload.TryGetProperty("lines", out var lineElement) && lineElement.TryGetInt32(out var value)
            ? Math.Clamp(value, 20, 400)
            : 160;
        var config = _app.ConfigStore.Reload();
        var path = name switch
        {
            "devspaceOut" => config.DevSpaceStdoutLog,
            "devspaceErr" => config.DevSpaceStderrLog,
            "tunnelOut" => config.TunnelStdoutLog,
            "tunnelErr" => config.TunnelStderrLog,
            "app" => AppPaths.AppLogPath,
            "updates" => AppPaths.UpdateLogPath,
            _ => throw new InvalidOperationException("未知日志类型。")
        };

        return new
        {
            name,
            path,
            text = File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(lines)) : ""
        };
    }

    private object OpenInstaller()
    {
        _app.Environment.OpenInstallTerminal();
        return new { opened = true };
    }

    private static object ProcessAction(ProcessRole role, Action<ProcessRole> action)
    {
        action(role);
        return new { ok = true };
    }

    private static object ProcessAction(Action action)
    {
        action();
        return new { ok = true };
    }

    private static async Task<object> ProcessActionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await action(cancellationToken);
        return new { ok = true };
    }

    private static object PublicConfig(ManagerConfig config, bool? trayRegistered = null) => new
    {
        config.PublicBaseUrl,
        configuredPublicBaseUrl = config.FixedPublicBaseUrl,
        config.McpUrl,
        config.LocalHealthUrl,
        config.PublicHealthUrl,
        tunnelName = config.CloudflareTunnelName,
        config.CloudflaredProtocol,
        config.DevSpacePort,
        config.RequestProxyEnabled,
        config.RequestProxyPort,
        config.AutoStartDevSpace,
        config.AutoStartTunnel,
        startWithWindows = trayRegistered ?? config.StartWithWindows,
        config.StartMinimizedToTray,
        config.AutoRestart,
        config.LocalDebugEnabled,
        config.LocalDebugPort,
        config.CodexStyleEnhancementsEnabled,
        config.CodexMessageNotificationsEnabled,
        activeProfileId = config.ActiveBrowserProfileId,
        profiles = config.BrowserProfiles.Select(PublicProfile).ToArray()
    };

    private static object PublicProfile(BrowserProfileConfig profile) => new
    {
        profile.Id,
        profile.Name,
        profile.UserDataFolder,
        profile.ProxyServer,
        proxyConfigured = !string.IsNullOrWhiteSpace(profile.ProxyServer),
        profile.Language,
        profile.Temporary
    };

    private static string RequiredString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"缺少字段：{propertyName}");
        }

        var text = value.GetString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"字段不能为空：{propertyName}");
        }

        return text;
    }

    private static bool RequiredBool(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"缺少字段：{propertyName}");
        }

        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException($"字段必须是布尔值：{propertyName}");
        }

        return value.GetBoolean();
    }

    private static SshProfileDraft ReadSshDraft(JsonElement payload)
    {
        string? id = payload.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var passwordProvided = payload.TryGetProperty("password", out var passwordElement);
        return new SshProfileDraft(
            id,
            RequiredString(payload, "name"),
            RequiredString(payload, "host"),
            ReadInt(payload, "port", 22),
            RequiredString(payload, "username"),
            passwordProvided ? passwordElement.GetString() ?? "" : null,
            payload.TryGetProperty("aiEnabled", out var aiEnabled) ? aiEnabled.GetBoolean() : true,
            payload.TryGetProperty("securityMode", out var securityMode) ? securityMode.GetString() ?? "restricted" : "restricted",
            payload.TryGetProperty("policyTemplate", out var policyTemplate) ? policyTemplate.GetString() ?? "inspection" : "inspection",
            ReadStringArray(payload, "allowPatterns"),
            ReadStringArray(payload, "denyPatterns"),
            ReadInt(payload, "idleTimeoutMinutes", 30),
            ReadInt(payload, "commandTimeoutSeconds", 60),
            ReadInt(payload, "maxOutputBytes", 128 * 1024),
            payload.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "");
    }

    private static int ReadInt(JsonElement payload, string propertyName, int fallback)
    {
        return payload.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string NormalizeLanguage(string value)
    {
        var language = value.Trim();
        if (string.IsNullOrWhiteSpace(language)) return "zh-CN";
        if (language.Length > 32)
        {
            throw new InvalidOperationException("语言标识太长。");
        }

        return language;
    }

    private static string NormalizeProxyServer(string value)
    {
        var proxy = value.Trim();
        if (string.IsNullOrWhiteSpace(proxy)) return "";

        var supported = new[] { "http://", "https://", "socks4://", "socks5://" };
        if (!supported.Any(prefix => proxy.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (Uri.TryCreate($"http://{proxy}", UriKind.Absolute, out var hostPort) && !string.IsNullOrWhiteSpace(hostPort.Host) && hostPort.Port > 0)
            {
                return proxy;
            }

            throw new InvalidOperationException("浏览器代理需要填写 HTTP、HTTPS 或 SOCKS 代理地址，例如 socks5://127.0.0.1:7890。Trojan 节点需要先由本地代理客户端转换。");
        }

        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
        {
            throw new InvalidOperationException("代理地址格式不正确，例如 socks5://127.0.0.1:7890。");
        }

        return proxy;
    }

    private static string CreateProfileId(string name, IEnumerable<string> existingIds)
    {
        var baseId = new string(name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "profile";
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

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("公网地址格式不正确，请输入类似 https://devspace.example.com");
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string NormalizeTunnelName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("隧道名称不能为空。");
        }

        if (normalized.Length > 64)
        {
            throw new InvalidOperationException("隧道名称太长，请控制在 64 个字符以内。");
        }

        if (normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-')))
        {
            throw new InvalidOperationException("隧道名称只能包含小写字母、数字和短横线。");
        }

        if (normalized.StartsWith('-') || normalized.EndsWith('-') || normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("隧道名称不要以短横线开头或结尾，也不要连续使用短横线。");
        }

        return normalized;
    }

    private static string Serialize(BridgeResponse response) => JsonSerializer.Serialize(response, JsonOptions);
}

internal sealed record BridgeRequest(string? Id, string Command, JsonElement Payload);

internal sealed record BridgeResponse(string? Id, bool Ok, object? Data, string? Error)
{
    public static BridgeResponse Success(string? id, object? data) => new(id, true, data, null);

    public static BridgeResponse Failed(string? id, string error) => new(id, false, null, error);
}
