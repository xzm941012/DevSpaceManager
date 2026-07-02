using System.Text;
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
        var snapshots = await ClientSnapshotsAsync(cancellationToken);

        return new
        {
            servers = definitions.Select(definition =>
            {
                var cache = caches.GetValueOrDefault(definition.Name);
                snapshots.TryGetValue(definition.Name, out var snapshot);
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
                    state = snapshot?.State.ToString() ?? StdioMcpClientState.Stopped.ToString(),
                    pendingCount = snapshot?.PendingCount ?? 0,
                    lastError = snapshot?.LastError ?? "",
                    recentErrors = snapshot?.RecentErrors ?? [],
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
            throw new InvalidOperationException("MCP server not found.");
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
            throw new InvalidOperationException("Only stdio MCP servers are currently supported.");
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

        var text = new StringBuilder();
        text.AppendLine("DevSpaceManager also mounts the following enabled local MCP servers. Use tool_search and call_mounted_tool when a task needs these local MCP capabilities.");
        text.AppendLine();
        text.AppendLine("If an MCP server is marked as having instructions and those instructions have not been loaded in the current context, you can call:");
        text.AppendLine("tool_search(mode=\"server\", query=\"<mcp-name>\")");
        text.AppendLine("to get that MCP server's server instructions.");

        foreach (var server in enabled.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var description = OneLine(server.Description);
            text.AppendLine();
            text.Append("- ");
            text.Append(server.Name);
            if (!string.IsNullOrWhiteSpace(description))
            {
                text.Append(": ");
                text.Append(description);
            }

            text.AppendLine();
            text.Append("  instructions: ");
            text.Append(string.IsNullOrWhiteSpace(server.Instructions) ? "no" : "yes");

            var tools = server.Tools
                .Select(tool => tool.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (tools.Length > 0)
            {
                text.AppendLine();
                text.Append("  tools: ");
                text.Append(string.Join(", ", tools));
            }
            else
            {
                text.AppendLine();
                text.Append("  tools: not refreshed yet");
            }
        }

        return text.ToString().TrimEnd();
    }

    public JsonObject ToolSearchDescriptor() => new()
    {
        ["name"] = "tool_search",
        ["title"] = "Search mounted MCP tools",
        ["description"] = "Search deferred metadata for currently enabled DevSpaceManager-mounted local MCP servers. In tools mode, search for matching tools and their input schemas before calling call_mounted_tool. In server mode, pass an exact MCP server name to get that server's instructions.",
        ["inputSchema"] = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Capability, tool, or exact MCP server name to search for."
                },
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("tools", "server"),
                    ["description"] = "Use \"tools\" to search mounted tool metadata, or \"server\" to return server instructions for an exact MCP server name. Defaults to \"tools\"."
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
        ["outputSchema"] = ToolSearchOutputSchema(),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false
        }
    };

    public JsonObject CallMountedToolDescriptor() => new()
    {
        ["name"] = "call_mounted_tool",
        ["title"] = "Call mounted MCP tool",
        ["description"] = "Call a tool from an enabled DevSpaceManager-mounted local MCP server. You can use tool_search to find the server, tool name, and input schema.",
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
        ["outputSchema"] = MountedToolOutputSchema(),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = false,
            ["destructiveHint"] = false,
            ["idempotentHint"] = false,
            ["openWorldHint"] = true
        }
    };

    public JsonObject SearchForMcpResult(JsonObject arguments)
    {
        var query = arguments["query"]?.GetValue<string>()?.Trim() ?? "";
        var mode = arguments["mode"]?.GetValue<string>()?.Trim() ?? "tools";
        if (string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase))
        {
            return SearchServerInstructionsForMcpResult(query);
        }

        if (!string.Equals(mode, "tools", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("mode must be either \"tools\" or \"server\".");
        }

        var limit = 8;
        if (arguments["limit"] is JsonValue limitValue && limitValue.TryGetValue<int>(out var requestedLimit))
        {
            limit = Math.Clamp(requestedLimit, 1, 20);
        }
        var results = Search(query, limit);
        var payload = new JsonObject
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
        };
        return McpContent(payload);
    }

    private JsonObject SearchServerInstructionsForMcpResult(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new InvalidOperationException("query must be an exact MCP server name when mode is \"server\".");
        }

        var server = EnabledCachedServers()
            .FirstOrDefault(item => string.Equals(item.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            throw new InvalidOperationException("No enabled mounted MCP server matched the query.");
        }

        var payload = new JsonObject
        {
            ["instructions"] = server.Instructions
        };
        return McpContent(payload);
    }

    public async Task<JsonObject> CallMountedToolForMcpResultAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new MountedMcpCallRequest(
            arguments["server"]?.GetValue<string>()?.Trim() ?? "",
            arguments["tool"]?.GetValue<string>()?.Trim() ?? "",
            arguments["arguments"] is JsonNode args ? JsonDocument.Parse(args.ToJsonString()).RootElement.Clone() : null);

        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Tool))
        {
            throw new InvalidOperationException("server and tool are required.");
        }

        EnsureEnabledTool(request.Server, request.Tool);
        return await CallMountedToolAsync(request, cancellationToken);
    }

    private async Task<JsonObject> CallMountedToolAsync(MountedMcpCallRequest request, CancellationToken cancellationToken)
    {
        var definition = FindDefinition(request.Server);
        if (!string.Equals(definition.Type, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only stdio MCP servers are currently supported.");
        }

        var client = await GetClientAsync(definition, cancellationToken);
        return await client.CallToolAsync(request.Tool, request.Arguments, cancellationToken);
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
        if (!IsEnabled(config, server)) throw new InvalidOperationException("This mounted MCP server is not enabled.");

        var cache = LoadCaches().GetValueOrDefault(server);
        if (cache?.Tools.Any(item => string.Equals(item.Name, tool, StringComparison.OrdinalIgnoreCase)) != true)
        {
            throw new InvalidOperationException("This tool is not registered in the local MCP cache. Refresh the tool list first.");
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

    private async Task<Dictionary<string, StdioMcpClientSnapshot>> ClientSnapshotsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _clients.ToDictionary(
                item => item.Key,
                item => item.Value.Snapshot,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.Release();
        }
    }

    private MountedMcpDefinition FindDefinition(string name) =>
        _discovery.Discover().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("MCP server not found.");

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
            "chrome-devtools" => "Control local Chrome DevTools for page inspection, interaction, and debugging.",
            "playwright" => "Use Playwright to control browsers, pages, and browser automation workflows.",
            "node_repl" => "Run JavaScript in a persistent Node.js runtime for scripts, browser bridges, and quick experiments.",
            "pencil" => "Read and write Pencil design files, generate and inspect design nodes, and export designs.",
            _ => $"{definition.Type} MCP: {DisplayCommand(definition)}"
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

    private static JsonObject ToolSearchOutputSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Structured result for tool_search. In tools mode it contains query and matching mounted MCP tools. In server mode it contains instructions for one MCP server.",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Search query used in tools mode."
            },
            ["results"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Matching mounted MCP tools returned in tools mode.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server"] = new JsonObject { ["type"] = "string", ["description"] = "Mounted MCP server name." },
                        ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name on that MCP server." },
                        ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Tool description." },
                        ["score"] = new JsonObject { ["type"] = "number", ["description"] = "Search relevance score." },
                        ["inputSchema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "JSON schema for arguments to pass to call_mounted_tool.",
                            ["additionalProperties"] = true
                        }
                    },
                    ["required"] = new JsonArray("server", "tool", "description", "score", "inputSchema"),
                    ["additionalProperties"] = false
                }
            },
            ["instructions"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Server instructions returned in server mode."
            }
        },
        ["additionalProperties"] = true
    };

    private static JsonObject MountedToolOutputSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Result returned by the mounted MCP server tool. The shape depends on the target MCP tool and may include text/image/resource content, structuredContent, and isError.",
        ["properties"] = new JsonObject
        {
            ["content"] = ContentArraySchema(),
            ["structuredContent"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Optional structured result returned by the mounted MCP tool.",
                ["additionalProperties"] = true
            },
            ["isError"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether the mounted MCP tool reported an error result."
            }
        },
        ["additionalProperties"] = true
    };

    private static JsonObject ContentArraySchema() => new()
    {
        ["type"] = "array",
        ["description"] = "MCP content items returned by the tool.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "MCP content item type such as text, image, or resource."
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Text content. tool_search returns JSON in this field."
                }
            },
            ["additionalProperties"] = true
        }
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
        text = OneLine(text);
        var end = text.IndexOfAny(['.', '。', '\n']);
        if (end > 0) text = text[..(end + 1)];
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "...";
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static JsonObject McpContent(JsonObject payload)
    {
        var textPayload = payload.DeepClone().ToJsonString(JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = textPayload
                }
            },
            ["structuredContent"] = payload.DeepClone()
        };
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _lock.Dispose();
    }
}
