using System.Text.Json.Serialization;

namespace DevSpaceManager.Core;

internal sealed class ManagerConfig
{
    public string NodeVersion { get; set; } = "23.11.1";
    public string NodeDirectory { get; set; } = "";
    public string DevSpaceCommand { get; set; } = "";
    public string NpmCommand { get; set; } = "";
    public string GitBashPath { get; set; } = "";
    public string CloudflaredPath { get; set; } = "";
    public string CloudflareTunnelName { get; set; } = "";
    public string CloudflaredProtocol { get; set; } = "auto";
    public string DevSpaceConfigPath { get; set; } = "";
    public string CloudflaredConfigPath { get; set; } = "";
    public int DevSpacePort { get; set; } = 7676;
    public string LocalHealthUrl { get; set; } = "http://127.0.0.1:7676/healthz";
    public string PublicHealthUrl { get; set; } = "https://devspace.onemem.cc/healthz";
    public string PublicBaseUrl { get; set; } = "https://devspace.onemem.cc";
    public string FixedPublicBaseUrl { get; set; } = "https://devspace.onemem.cc";
    public bool TemporaryPublicBaseUrlPending { get; set; }
    public bool RequestProxyEnabled { get; set; }
    public int RequestProxyPort { get; set; } = 17676;
    public int HealthCheckSeconds { get; set; } = 60;
    public int UpdateCheckHours { get; set; } = 24;
    public string LastNotifiedUpdateVersion { get; set; } = "";
    public bool AutoRestart { get; set; } = true;
    public bool AutoStartDevSpace { get; set; } = true;
    public bool AutoStartTunnel { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public bool UseTemporaryCloudflareTunnel { get; set; }
    public string DevSpaceLogLevel { get; set; } = "warn";
    public string DevSpaceLogFormat { get; set; } = "json";
    public string DevSpaceToolMode { get; set; } = "minimal";
    public string DevSpaceWidgets { get; set; } = "off";
    public bool DevSpaceLogToolCalls { get; set; }
    public bool DevSpaceLogRequests { get; set; }
    public bool DevSpaceSkills { get; set; } = true;
    public string DevSpaceAgentDir { get; set; } = "";
    public string DevSpaceSkillPaths { get; set; } = "";
    public string ActiveBrowserProfileId { get; set; } = "default";
    public List<BrowserProfileConfig> BrowserProfiles { get; set; } = [];
    public bool LocalDebugEnabled { get; set; }
    public int LocalDebugPort { get; set; } = 9223;
    public List<MountedMcpConfig> MountedMcps { get; set; } = [];

    [JsonIgnore]
    public string DevSpaceStdoutLog => Path.Combine(AppPaths.LogDirectory, "devspace.out.log");

    [JsonIgnore]
    public string DevSpaceStderrLog => Path.Combine(AppPaths.LogDirectory, "devspace.err.log");

    [JsonIgnore]
    public string TunnelStdoutLog => Path.Combine(AppPaths.LogDirectory, "cloudflared.out.log");

    [JsonIgnore]
    public string TunnelStderrLog => Path.Combine(AppPaths.LogDirectory, "cloudflared.err.log");

    [JsonIgnore]
    public string McpUrl => $"{PublicBaseUrl.TrimEnd('/')}/mcp";
}

internal sealed class MountedMcpConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}
