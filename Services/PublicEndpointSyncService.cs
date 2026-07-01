using System.Text.Json;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class PublicEndpointSyncService
{
    private readonly ManagerConfigStore _configStore;
    private readonly object _gate = new();

    public PublicEndpointSyncService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("公网地址格式不正确，请输入类似 https://devspace.onemem.cc");
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }

    public string ResolveFixedPublicBaseUrl(ManagerConfig config)
    {
        var raw = string.IsNullOrWhiteSpace(config.FixedPublicBaseUrl)
            ? config.PublicBaseUrl
            : config.FixedPublicBaseUrl;
        return NormalizeBaseUrl(raw);
    }

    public ManagerConfig ActivateTemporaryMode()
    {
        lock (_gate)
        {
            var config = _configStore.Reload();
            config.UseTemporaryCloudflareTunnel = true;
            config.TemporaryPublicBaseUrlPending = true;
            _configStore.Save(config);
            return config;
        }
    }

    public ManagerConfig ActivateFixedMode()
    {
        lock (_gate)
        {
            var config = _configStore.Reload();
            config.UseTemporaryCloudflareTunnel = false;
            config.TemporaryPublicBaseUrlPending = false;
            ApplyCurrentPublicBaseUrl(config, ResolveFixedPublicBaseUrl(config));
            _configStore.Save(config);
            SyncConnectionFiles(config, includeCloudflareIngress: true);
            return config;
        }
    }

    public bool TryApplyTemporaryPublicBaseUrl(string value)
    {
        var normalized = NormalizeBaseUrl(value);
        lock (_gate)
        {
            var config = _configStore.Reload();
            if (!config.UseTemporaryCloudflareTunnel)
            {
                return false;
            }

            var changed = !string.Equals(config.PublicBaseUrl, normalized, StringComparison.OrdinalIgnoreCase);
            ApplyCurrentPublicBaseUrl(config, normalized);
            config.TemporaryPublicBaseUrlPending = false;
            _configStore.Save(config);
            SyncConnectionFiles(config, includeCloudflareIngress: false);
            return changed;
        }
    }

    public void MarkTemporaryPublicBaseUrlPending()
    {
        lock (_gate)
        {
            var config = _configStore.Reload();
            if (!config.UseTemporaryCloudflareTunnel)
            {
                return;
            }

            config.TemporaryPublicBaseUrlPending = true;
            _configStore.Save(config);
        }
    }

    public ManagerConfig SyncCurrentModeToConfigs()
    {
        lock (_gate)
        {
            var config = _configStore.Reload();
            if (!config.UseTemporaryCloudflareTunnel)
            {
                config.TemporaryPublicBaseUrlPending = false;
                ApplyCurrentPublicBaseUrl(config, ResolveFixedPublicBaseUrl(config));
                _configStore.Save(config);
                SyncConnectionFiles(config, includeCloudflareIngress: true);
                return config;
            }

            SyncConnectionFiles(config, includeCloudflareIngress: false);
            return config;
        }
    }

    public ManagerConfig ApplyDevSpaceConfiguration(int port, IEnumerable<string> allowedRoots)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("DevSpace 端口必须在 1 到 65535 之间。");
        }

        var normalizedRoots = NormalizeAllowedRoots(allowedRoots);

        lock (_gate)
        {
            var config = _configStore.Reload();
            config.DevSpacePort = port;
            config.LocalHealthUrl = $"http://127.0.0.1:{port}/healthz";
            _configStore.Save(config);
            SyncConnectionFiles(config, includeCloudflareIngress: !config.UseTemporaryCloudflareTunnel, normalizedRoots);
            return config;
        }
    }

    public ManagerConfig ApplyCloudflareConfiguration(
        bool useTemporaryTunnel,
        string fixedPublicBaseUrl,
        string tunnelName,
        string protocol,
        int proxyPort)
    {
        if (proxyPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("本地代理端口必须在 1 到 65535 之间。");
        }

        var normalizedProtocol = string.IsNullOrWhiteSpace(protocol) ? "auto" : protocol.Trim().ToLowerInvariant();
        if (normalizedProtocol is not ("auto" or "http2" or "quic"))
        {
            throw new InvalidOperationException("Cloudflare 协议只能选择 auto、http2 或 quic。");
        }

        lock (_gate)
        {
            var config = _configStore.Reload();
            config.UseTemporaryCloudflareTunnel = useTemporaryTunnel;
            config.RequestProxyPort = proxyPort;
            config.LocalHealthUrl = $"http://127.0.0.1:{config.DevSpacePort}/healthz";
            config.CloudflaredProtocol = normalizedProtocol;
            config.TemporaryPublicBaseUrlPending = useTemporaryTunnel;

            if (!useTemporaryTunnel)
            {
                if (string.IsNullOrWhiteSpace(tunnelName))
                {
                    throw new InvalidOperationException("固定域名模式需要填写 Cloudflare 隧道名称。");
                }

                config.CloudflareTunnelName = tunnelName.Trim().ToLowerInvariant();
                config.FixedPublicBaseUrl = NormalizeBaseUrl(fixedPublicBaseUrl);
                ApplyCurrentPublicBaseUrl(config, config.FixedPublicBaseUrl);
            }

            _configStore.Save(config);
            SyncConnectionFiles(config, includeCloudflareIngress: !config.UseTemporaryCloudflareTunnel);
            return config;
        }
    }

    public static int CloudflaredServicePort(ManagerConfig config) =>
        config.RequestProxyEnabled ? config.RequestProxyPort : config.DevSpacePort;

    public static void WriteCloudflaredIngress(string path, string hostname, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Count == 0)
        {
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://127.0.0.1:{port}");
            lines.Add("  - service: http_status:404");
            File.WriteAllLines(path, lines);
            return;
        }

        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains("hostname:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"  - hostname: {hostname}";
                changed = true;
                continue;
            }

            if (lines[i].Contains("service: http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains("service: http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"    service: http://127.0.0.1:{port}";
                changed = true;
            }
        }

        if (!changed)
        {
            lines.Clear();
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://127.0.0.1:{port}");
            lines.Add("  - service: http_status:404");
        }

        File.WriteAllLines(path, lines);
    }

    public static IReadOnlyList<string> ReadAllowedRoots(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("allowedRoots", out var roots) || roots.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return roots.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void WriteDevSpaceConnection(string path, string publicBaseUrl, int port, IEnumerable<string>? allowedRoots = null)
    {
        var root = ReadJsonObjectOrEmpty(path);
        root["publicBaseUrl"] = publicBaseUrl.TrimEnd('/');
        root["port"] = port;
        root["host"] = string.IsNullOrWhiteSpace(root.TryGetValue("host", out var host) ? host?.ToString() : null)
            ? "127.0.0.1"
            : host;
        if (allowedRoots is null)
        {
            root.TryAdd("allowedRoots", Array.Empty<string>());
        }
        else
        {
            root["allowedRoots"] = NormalizeAllowedRoots(allowedRoots);
        }
        WriteTextFile(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SyncConnectionFiles(ManagerConfig config, bool includeCloudflareIngress, IReadOnlyList<string>? allowedRoots = null)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            WriteDevSpaceConnection(config.DevSpaceConfigPath, config.PublicBaseUrl, config.DevSpacePort, allowedRoots);
        }

        if (!includeCloudflareIngress) return;

        var hostname = new Uri(ResolveFixedPublicBaseUrl(config)).Host;
        WriteCloudflaredIngress(config.CloudflaredConfigPath, hostname, CloudflaredServicePort(config));
    }

    private static void ApplyCurrentPublicBaseUrl(ManagerConfig config, string publicBaseUrl)
    {
        config.PublicBaseUrl = publicBaseUrl.TrimEnd('/');
        config.PublicHealthUrl = $"{config.PublicBaseUrl}/healthz";
    }

    private static string[] NormalizeAllowedRoots(IEnumerable<string> allowedRoots)
    {
        return allowedRoots
            .Select(root => root?.Trim())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                try
                {
                    return Path.GetFullPath(root!);
                }
                catch
                {
                    return root!;
                }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, object?> ReadJsonObjectOrEmpty(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, object?>();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object?>();

        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json.RootElement.GetRawText()) ??
                   new Dictionary<string, object?>();
        }
        catch
        {
            BackupInvalidFile(path);
            return new Dictionary<string, object?>();
        }
    }

    private static void BackupInvalidFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = $"{path}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // Best-effort backup only; writing a valid config is more important.
        }
    }

    private static void WriteTextFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
