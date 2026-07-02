using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class SshMcpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SshProfileService _profiles;
    private readonly SshSessionManager _sessions;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SshMcpServer(ManagerConfigStore configStore, TextReader? input = null, TextWriter? output = null)
    {
        _profiles = new SshProfileService(configStore);
        _sessions = new SshSessionManager(_profiles);
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string? line;
        while (!cancellationToken.IsCancellationRequested && (line = await _input.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            await HandleLineAsync(line, cancellationToken);
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject? request = null;
        JsonNode? id = null;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("JSON-RPC 消息必须是 object。");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? "";
            var parameters = request["params"] as JsonObject ?? new JsonObject();

            if (string.IsNullOrWhiteSpace(method)) return;

            switch (method)
            {
                case "initialize":
                    await WriteResultAsync(id, InitializeResult(), cancellationToken);
                    break;
                case "notifications/initialized":
                    break;
                case "tools/list":
                    await WriteResultAsync(id, ToolsList(), cancellationToken);
                    break;
                case "tools/call":
                    await WriteResultAsync(id, await CallToolAsync(parameters, cancellationToken), cancellationToken);
                    break;
                default:
                    await WriteErrorAsync(id, -32601, $"不支持的方法：{method}", cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.App($"SSH MCP request failed: {ex.Message}");
            await WriteErrorAsync(id, -32000, ex.Message, cancellationToken);
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "devspace-ssh",
            ["title"] = "DevSpace SSH"
        },
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject()
        },
        ["instructions"] = "Use DevSpace SSH tools to list user-approved SSH profiles, open a persistent session, execute commands in that session, list sessions, and close sessions. Never ask for IP addresses, usernames, passwords, or private keys; use serverId values returned by ssh_list_servers."
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool("ssh_list_servers", "List SSH profiles that the user allowed AI to access. Secrets, host IPs, usernames, and passwords are not returned.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            }, readOnly: true),
            Tool("ssh_open_session", "Open a persistent SSH session for an allowed serverId and return a sessionId for subsequent commands.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["serverId"] = new JsonObject { ["type"] = "string", ["description"] = "Server id returned by ssh_list_servers." }
                },
                ["required"] = new JsonArray("serverId"),
                ["additionalProperties"] = false
            }),
            Tool("ssh_exec", "Execute one command inside an existing SSH session. The session preserves shell state such as cd and exported variables.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sessionId"] = new JsonObject { ["type"] = "string" },
                    ["command"] = new JsonObject { ["type"] = "string" },
                    ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3600 }
                },
                ["required"] = new JsonArray("sessionId", "command"),
                ["additionalProperties"] = false
            }),
            Tool("ssh_list_sessions", "List currently active SSH sessions.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            }, readOnly: true),
            Tool("ssh_close_session", "Close an active SSH session by sessionId.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sessionId"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("sessionId"),
                ["additionalProperties"] = false
            })
        }
    };

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = parameters["name"]?.GetValue<string>() ?? "";
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        object result = name switch
        {
            "ssh_list_servers" => _sessions.ListServers(),
            "ssh_open_session" => await _sessions.OpenSessionAsync(RequiredString(arguments, "serverId"), cancellationToken),
            "ssh_exec" => await _sessions.ExecuteAsync(
                RequiredString(arguments, "sessionId"),
                RequiredString(arguments, "command"),
                OptionalInt(arguments, "timeoutSeconds"),
                cancellationToken),
            "ssh_list_sessions" => _sessions.ListSessions(),
            "ssh_close_session" => _sessions.CloseSession(RequiredString(arguments, "sessionId")),
            _ => throw new InvalidOperationException($"未知工具：{name}")
        };

        return ContentResult(result);
    }

    private static JsonObject Tool(string name, string description, JsonObject inputSchema, bool readOnly = false) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = readOnly,
            ["openWorldHint"] = true
        }
    };

    private static JsonObject ContentResult(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = json
                }
            }
        };
    }

    private static string RequiredString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"缺少字段：{name}");
        return value;
    }

    private static int? OptionalInt(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value) return null;
        return value.TryGetValue<int>(out var number) ? number : null;
    }

    private async Task WriteResultAsync(JsonNode? id, JsonObject result, CancellationToken cancellationToken)
    {
        if (id is null) return;
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result
        }, cancellationToken);
    }

    private async Task WriteErrorAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken)
    {
        if (id is null) return;
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        }, cancellationToken);
    }

    private async Task WriteAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteLineAsync(payload.ToJsonString(JsonOptions).AsMemory(), cancellationToken);
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _writeLock.Dispose();
    }
}
