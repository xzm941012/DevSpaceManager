using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class MountedMcpService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ManagerConfigStore _configStore;
    private readonly MountedMcpDiscoveryService _discovery = new();
    private readonly Dictionary<string, StdioMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MountedMcpService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<object> ListAsync(CancellationToken cancellationToken)
    {
        var definitions = _discovery.Discover();
        var config = _configStore.Reload();
        if (EnsureConfigRecords(config, definitions)) _configStore.Save(config);
        var caches = LoadCaches();

        await Task.CompletedTask;
        return new
        {
            servers = definitions.Select(definition =>
            {
                var cache = caches.GetValueOrDefault(definition.Name);
                return new
                {
                    definition.Name,
                    definition.Type,
                    command = DisplayCommand(definition),
                    enabled = IsEnabled(config, definition.Name),
                    description = Describe(definition, cache),
                    instructions = cache?.Instructions ?? "",
                    refreshedAt = cache?.RefreshedAt,
                    toolCount = cache?.Tools.Count ?? 0,
                    tools = cache?.Tools.Select(PublicTool).ToArray() ?? []
                };
            }).ToArray()
        };
    }

    public async Task<object> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        var definitions = _discovery.Discover();
        if (definitions.All(item => !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("未找到 MCP。");
        }

        var config = _configStore.Reload();
        if (EnsureConfigRecords(config, definitions)) _configStore.Save(config);
        var record = config.MountedMcps.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        record.Enabled = enabled;
        _configStore.Save(config);
        return await ListAsync(cancellationToken);
    }

    public async Task<object> RefreshAsync(string name, CancellationToken cancellationToken)
    {
        var definition = FindDefinition(name);
        if (!string.Equals(definition.Type, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前仅支持 stdio 类型 MCP。");
        }

        var client = await GetClientAsync(definition, cancellationToken);
        var toolsResult = await client.ListToolsAsync(cancellationToken);
        var initialize = client.InitializeResult;
        var cache = new MountedMcpServerCache
        {
            Name = definition.Name,
            Description = ReadServerDescription(definition, initialize),
            Instructions = initialize["instructions"]?.GetValue<string>() ?? "",
            RefreshedAt = DateTimeOffset.Now,
            Tools = ReadTools(toolsResult)
        };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var caches = LoadCaches();
            caches[definition.Name] = cache;
            SaveCaches(caches);
        }
        finally
        {
            _lock.Release();
        }

        return await ListAsync(cancellationToken);
    }

    public string BuildProxyInstructions()
    {
        var enabled = EnabledCachedServers().ToArray();
        if (enabled.Length == 0) return "";

        var names = string.Join(", ", enabled.Select(item => item.Name));
        return $"DevSpaceManager 还挂载了以下本机 MCP：{names}。需要使用这些本机 MCP 能力时，先调用 tool_search 搜索可用工具，再用 call_mounted_tool 按返回的 server、tool 和参数 schema 调用。";
    }

    public JsonObject ToolSearchDescriptor() => new()
    {
        ["name"] = "tool_search",
        ["title"] = "Search mounted MCP tools",
        ["description"] = "Search deferred metadata for currently enabled DevSpaceManager-mounted local MCP tools. Use this before calling a mounted local MCP tool.",
        ["inputSchema"] = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Capability or tool to search for."
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 20,
                    ["description"] = "Maximum number of results."
                }
            },
            ["required"] = new JsonArray("query"),
            ["additionalProperties"] = false
        },
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false
        },
        ["securitySchemes"] = DevSpaceSecuritySchemes(),
        ["_meta"] = DevSpaceToolMeta()
    };

    public JsonObject CallMountedToolDescriptor() => new()
    {
        ["name"] = "call_mounted_tool",
        ["title"] = "Call mounted MCP tool",
        ["description"] = "Call a tool from an enabled DevSpaceManager-mounted local MCP server. First use tool_search to find the server, tool name, and input schema.",
        ["inputSchema"] = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["server"] = new JsonObject { ["type"] = "string", ["description"] = "Mounted MCP server name." },
                ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name on the mounted MCP server." },
                ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments matching the returned tool input schema." }
            },
            ["required"] = new JsonArray("server", "tool"),
            ["additionalProperties"] = false
        },
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = false,
            ["destructiveHint"] = false,
            ["idempotentHint"] = false,
            ["openWorldHint"] = true
        },
        ["securitySchemes"] = DevSpaceSecuritySchemes(),
        ["_meta"] = DevSpaceToolMeta()
    };

    public JsonObject SearchForMcpResult(JsonObject arguments)
    {
        var query = arguments["query"]?.GetValue<string>()?.Trim() ?? "";
        var limit = 8;
        if (arguments["limit"] is JsonValue limitValue && limitValue.TryGetValue<int>(out var requestedLimit))
        {
            limit = Math.Clamp(requestedLimit, 1, 20);
        }
        var results = Search(query, limit);
        return McpContent(new JsonObject
        {
            ["query"] = query,
            ["results"] = new JsonArray(results.Select(result => new JsonObject
            {
                ["server"] = result.Server,
                ["tool"] = result.Tool,
                ["description"] = result.Description,
                ["score"] = result.Score,
                ["inputSchema"] = result.InputSchema.DeepClone()
            }).ToArray<JsonNode?>())
        });
    }

    public async Task<JsonObject> CallMountedToolForMcpResultAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new MountedMcpCallRequest(
            arguments["server"]?.GetValue<string>()?.Trim() ?? "",
            arguments["tool"]?.GetValue<string>()?.Trim() ?? "",
            arguments["arguments"] is JsonNode args ? JsonDocument.Parse(args.ToJsonString()).RootElement.Clone() : null);

        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Tool))
        {
            throw new InvalidOperationException("server 和 tool 不能为空。");
        }

        EnsureEnabledTool(request.Server, request.Tool);
        var result = await CallWithRestartAsync(request, cancellationToken);
        return result;
    }

    private async Task<JsonObject> CallWithRestartAsync(MountedMcpCallRequest request, CancellationToken cancellationToken)
    {
        var definition = FindDefinition(request.Server);
        if (!string.Equals(definition.Type, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前仅支持 stdio 类型 MCP。");
        }

        var client = await GetClientAsync(definition, cancellationToken);
        try
        {
            return await client.CallToolAsync(request.Tool, request.Arguments, cancellationToken);
        }
        catch (MountedMcpRemoteException)
        {
            throw;
        }
        catch (MountedMcpProtocolException)
        {
            throw;
        }
        catch (Exception) when (client.IsRunning == false)
        {
            await RestartClientAsync(definition.Name, cancellationToken);
            client = await GetClientAsync(definition, cancellationToken);
            return await client.CallToolAsync(request.Tool, request.Arguments, cancellationToken);
        }
    }

    private IReadOnlyList<MountedMcpSearchResult> Search(string query, int limit)
    {
        var tokens = Tokenize(query).ToArray();
        return EnabledCachedServers()
            .SelectMany(server => server.Tools.Select(tool =>
            {
                var haystack = $"{server.Name} {server.Description} {server.Instructions} {tool.Name} {tool.Description}".ToLowerInvariant();
                var exact = query.Length > 0 && haystack.Contains(query.ToLowerInvariant(), StringComparison.Ordinal) ? 4 : 0;
                var tokenScore = tokens.Sum(token => haystack.Contains(token, StringComparison.Ordinal) ? 2 : 0);
                var nameScore = tokens.Sum(token => tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ? 3 : 0);
                return new MountedMcpSearchResult(server.Name, tool.Name, tool.Description, tool.InputSchema.DeepClone().AsObject(), exact + tokenScore + nameScore);
            }))
            .Where(item => string.IsNullOrWhiteSpace(query) || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Tool, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private IEnumerable<MountedMcpServerCache> EnabledCachedServers()
    {
        var config = _configStore.Reload();
        var enabled = config.MountedMcps.Where(item => item.Enabled).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return LoadCaches().Values.Where(item => enabled.Contains(item.Name));
    }

    private void EnsureEnabledTool(string server, string tool)
    {
        var config = _configStore.Reload();
        if (!IsEnabled(config, server)) throw new InvalidOperationException("该 MCP 未启用。");

        var cache = LoadCaches().GetValueOrDefault(server);
        if (cache?.Tools.Any(item => string.Equals(item.Name, tool, StringComparison.OrdinalIgnoreCase)) != true)
        {
            throw new InvalidOperationException("该工具未在本地缓存中登记，请先刷新工具列表。");
        }
    }

    private async Task<StdioMcpClient> GetClientAsync(MountedMcpDefinition definition, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_clients.TryGetValue(definition.Name, out var existing) && existing.IsRunning) return existing;
            existing?.Dispose();
            var client = new StdioMcpClient(definition);
            _clients[definition.Name] = client;
            return client;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RestartClientAsync(string name, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_clients.Remove(name, out var existing)) existing.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    private MountedMcpDefinition FindDefinition(string name) =>
        _discovery.Discover().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("未找到 MCP。");

    private static bool EnsureConfigRecords(ManagerConfig config, IReadOnlyList<MountedMcpDefinition> definitions)
    {
        var changed = false;
        foreach (var definition in definitions)
        {
            if (config.MountedMcps.Any(item => string.Equals(item.Name, definition.Name, StringComparison.OrdinalIgnoreCase))) continue;
            config.MountedMcps.Add(new MountedMcpConfig { Name = definition.Name, Enabled = false });
            changed = true;
        }

        return changed;
    }

    private static bool IsEnabled(ManagerConfig config, string name) =>
        config.MountedMcps.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Enabled == true;

    private static string Describe(MountedMcpDefinition definition, MountedMcpServerCache? cache)
    {
        if (!string.IsNullOrWhiteSpace(cache?.Description)) return cache.Description;
        return definition.Name switch
        {
            "chrome-devtools" => "控制本机 Chrome DevTools，用于页面检查、交互和调试。",
            "playwright" => "通过 Playwright 操作浏览器、页面和自动化测试流程。",
            "node_repl" => "在持久 Node.js 运行时中执行 JavaScript，适合脚本、浏览器桥接和快速实验。",
            "pencil" => "读写 Pencil 设计文件，生成、检查和导出设计节点。",
            _ => $"{definition.Type} MCP：{DisplayCommand(definition)}"
        };
    }

    private static string ReadServerDescription(MountedMcpDefinition definition, JsonObject initialize)
    {
        var serverInfo = initialize["serverInfo"] as JsonObject;
        var title = serverInfo?["title"]?.GetValue<string>() ?? serverInfo?["name"]?.GetValue<string>() ?? definition.Name;
        var instructions = initialize["instructions"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(instructions)) return FirstSentence(instructions, 160);
        return title;
    }

    private static List<MountedMcpToolCache> ReadTools(JsonObject toolsResult)
    {
        if (toolsResult["tools"] is not JsonArray tools) return [];
        return tools.OfType<JsonObject>()
            .Select(tool => new MountedMcpToolCache
            {
                Name = tool["name"]?.GetValue<string>() ?? "",
                Description = tool["description"]?.GetValue<string>() ?? "",
                InputSchema = (tool["inputSchema"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true
                }
            })
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<string, MountedMcpServerCache> LoadCaches()
    {
        if (!File.Exists(AppPaths.MountedMcpCachePath)) return new Dictionary<string, MountedMcpServerCache>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cache = JsonSerializer.Deserialize<List<MountedMcpServerCache>>(File.ReadAllText(AppPaths.MountedMcpCachePath), JsonOptions) ?? [];
            return cache.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, MountedMcpServerCache>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveCaches(Dictionary<string, MountedMcpServerCache> caches)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var values = caches.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        File.WriteAllText(AppPaths.MountedMcpCachePath, JsonSerializer.Serialize(values, JsonOptions));
    }

    private static object PublicTool(MountedMcpToolCache tool) => new
    {
        tool.Name,
        tool.Description,
        tool.InputSchema
    };

    private static string DisplayCommand(MountedMcpDefinition definition)
    {
        var command = definition.Args.Count == 0
            ? definition.Command
            : $"{definition.Command} {string.Join(" ", definition.Args)}";
        return command.Length <= 140 ? command : command[..137] + "...";
    }

    private static IEnumerable<string> Tokenize(string query) =>
        query.Split([' ', '\t', '\r', '\n', '.', ',', '/', '\\', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .Where(item => item.Length > 1);

    private static string FirstSentence(string text, int maxLength)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        var end = text.IndexOfAny(['.', '。', '\n']);
        if (end > 0) text = text[..(end + 1)];
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "...";
    }

    private static JsonObject McpContent(JsonObject payload) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = payload.ToJsonString(JsonOptions)
            }
        }
    };

    private static JsonArray DevSpaceSecuritySchemes() => new()
    {
        new JsonObject
        {
            ["type"] = "oauth2",
            ["scopes"] = new JsonArray("devspace")
        }
    };

    private static JsonObject DevSpaceToolMeta() => new()
    {
        ["securitySchemes"] = DevSpaceSecuritySchemes()
    };

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _lock.Dispose();
    }
}
