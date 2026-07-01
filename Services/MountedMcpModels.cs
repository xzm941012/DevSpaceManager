using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevSpaceManager.Services;

internal sealed class MountedMcpDefinition
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "stdio";
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int StartupTimeoutMs { get; init; } = 20_000;
}

internal sealed class MountedMcpServerCache
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public DateTimeOffset? RefreshedAt { get; set; }
    public List<MountedMcpToolCache> Tools { get; set; } = [];
}

internal sealed class MountedMcpToolCache
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonObject InputSchema { get; set; } = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = true
    };
}

internal sealed record MountedMcpSearchResult(
    string Server,
    string Tool,
    string Description,
    JsonObject InputSchema,
    double Score);

internal sealed record MountedMcpCallRequest(string Server, string Tool, JsonElement? Arguments);
