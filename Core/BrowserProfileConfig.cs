namespace DevSpaceManager.Core;

internal sealed class BrowserProfileConfig
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "默认";
    public string UserDataFolder { get; set; } = "";
    public string ProxyServer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Language { get; set; } = "zh-CN";
    public bool Temporary { get; set; }
}
