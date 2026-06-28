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
            "devspace.start" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Start),
            "devspace.stop" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Stop),
            "devspace.restart" => ProcessAction(ProcessRole.DevSpace, _app.Processes.Restart),
            "tunnel.start" => ProcessAction(ProcessRole.CloudflareTunnel, _app.Processes.Start),
            "tunnel.stop" => ProcessAction(ProcessRole.CloudflareTunnel, _app.Processes.Stop),
            "tunnel.restart" => ProcessAction(ProcessRole.CloudflareTunnel, _app.Processes.Restart),
            "services.startAll" => ProcessAction(_app.Processes.StartAll),
            "services.stopAll" => ProcessAction(_app.Processes.StopAll),
            "services.restartAll" => ProcessAction(_app.Processes.RestartAll),
            "profile.list" => BuildProfiles(),
            "profile.switch" => await SwitchProfileAsync(payload),
            "config.saveBasics" => await SaveBasicsAsync(payload),
            "debug.save" => await SaveDebugAsync(payload),
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

        return new
        {
            checkedAt = DateTimeOffset.Now,
            config = PublicConfig(config),
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
        if (_reloadChatGptView is not null)
        {
            await _reloadChatGptView();
        }

        return BuildProfiles();
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
        return PublicConfig(config);
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
        return PublicConfig(config);
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

    private static object PublicConfig(ManagerConfig config) => new
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
        config.AutoRestart,
        config.LocalDebugEnabled,
        config.LocalDebugPort,
        activeProfileId = config.ActiveBrowserProfileId,
        profiles = config.BrowserProfiles.Select(PublicProfile).ToArray()
    };

    private static object PublicProfile(BrowserProfileConfig profile) => new
    {
        profile.Id,
        profile.Name,
        profile.UserDataFolder,
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
