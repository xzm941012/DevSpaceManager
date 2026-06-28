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
            _configStore.Save(config);
            SyncConnectionFiles(config, includeCloudflareIngress: false);
            return changed;
        }
    }

    public ManagerConfig SyncCurrentModeToConfigs()
    {
        lock (_gate)
        {
            var config = _configStore.Reload();
            if (!config.UseTemporaryCloudflareTunnel)
            {
                ApplyCurrentPublicBaseUrl(config, ResolveFixedPublicBaseUrl(config));
                _configStore.Save(config);
                SyncConnectionFiles(config, includeCloudflareIngress: true);
                return config;
            }

            SyncConnectionFiles(config, includeCloudflareIngress: false);
            return config;
        }
    }

    public static int CloudflaredServicePort(ManagerConfig config) =>
        config.RequestProxyEnabled ? config.RequestProxyPort : config.DevSpacePort;

    public static void WriteCloudflaredIngress(string path, string hostname, int port)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Count == 0)
        {
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://localhost:{port}");
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
                lines[i] = $"    service: http://localhost:{port}";
                changed = true;
            }
        }

        if (!changed)
        {
            lines.Clear();
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://localhost:{port}");
            lines.Add("  - service: http_status:404");
        }

        File.WriteAllLines(path, lines);
    }

    public static void WriteDevSpaceConnection(string path, string publicBaseUrl, int port)
    {
        var root = ReadJsonObjectOrEmpty(path);
        root["publicBaseUrl"] = publicBaseUrl.TrimEnd('/');
        root["port"] = port;
        root.TryAdd("host", "127.0.0.1");
        root.TryAdd("allowedRoots", Array.Empty<string>());
        WriteTextFile(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SyncConnectionFiles(ManagerConfig config, bool includeCloudflareIngress)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            WriteDevSpaceConnection(config.DevSpaceConfigPath, config.PublicBaseUrl, config.DevSpacePort);
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
