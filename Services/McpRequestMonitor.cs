using System.Text;
using System.Text.RegularExpressions;

namespace DevSpaceManager.Services;

internal sealed class McpRequestMonitor
{
    private const int MaxItems = 100;
    private static readonly Regex MethodRegex = new("\"method\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ToolNameRegex = new("\"name\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly object _gate = new();
    private readonly LinkedList<McpRequestEntry> _items = new();

    public event EventHandler<McpRequestEntry>? RequestAdded;
    public event EventHandler<McpRequestEntry>? RequestUpdated;

    public IReadOnlyList<McpRequestEntry> Snapshot()
    {
        lock (_gate)
        {
            return _items.Select(item => item with { }).ToArray();
        }
    }

    public McpRequestEntry Start(string method, string path)
    {
        var isLongLived = IsLongLivedRequest(method, path);
        var entry = new McpRequestEntry(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(method) ? "?" : method,
            path,
            isLongLived ? "监听中" : "进行中",
            null,
            null,
            true,
            null,
            isLongLived,
            isLongLived ? "MCP 长连接已建立，正在等待事件" : null);

        lock (_gate)
        {
            _items.AddFirst(entry);
            while (_items.Count > MaxItems)
            {
                _items.RemoveLast();
            }
        }

        RequestAdded?.Invoke(this, entry);
        return entry;
    }

    public void SetTool(Guid id, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        Update(id, entry => entry with { ToolName = toolName });
    }

    public void Complete(Guid id, int statusCode, long elapsedMs)
    {
        Update(id, entry => entry with
        {
            Status = entry.IsLongLived ? "已断开" : "完成",
            StatusCode = statusCode,
            ElapsedMs = elapsedMs,
            InProgress = false,
            Detail = entry.IsLongLived ? "长连接正常结束" : entry.Detail
        });
    }

    public void Disconnect(Guid id, int? statusCode, long elapsedMs, string detail)
    {
        Update(id, entry => entry with
        {
            Status = entry.IsLongLived ? "已断开" : "中断",
            StatusCode = statusCode,
            ElapsedMs = elapsedMs,
            InProgress = false,
            Detail = detail
        });
    }

    public void Fail(Guid id, int? statusCode, long elapsedMs, string error, string? detail = null)
    {
        Update(id, entry => entry with
        {
            Status = "失败",
            StatusCode = statusCode,
            ElapsedMs = elapsedMs,
            InProgress = false,
            Error = error,
            Detail = detail ?? entry.Detail
        });
    }

    private void Update(Guid id, Func<McpRequestEntry, McpRequestEntry> update)
    {
        McpRequestEntry? updated = null;
        lock (_gate)
        {
            for (var node = _items.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != id) continue;
                updated = update(node.Value);
                node.Value = updated;
                break;
            }
        }

        if (updated is not null)
        {
            RequestUpdated?.Invoke(this, updated);
        }
    }

    public static bool IsLongLivedRequest(string method, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractToolName(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return "";

        var text = Encoding.UTF8.GetString(body);
        var methodMatch = MethodRegex.Match(text);
        if (!methodMatch.Success) return "";

        var method = methodMatch.Groups["value"].Value;
        if (!string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
        {
            return method;
        }

        var toolMatch = ToolNameRegex.Match(text);
        return toolMatch.Success ? toolMatch.Groups["value"].Value : method;
    }
}

internal sealed record McpRequestEntry(
    Guid Id,
    DateTimeOffset StartedAt,
    string Method,
    string Path,
    string Status,
    int? StatusCode,
    long? ElapsedMs,
    bool InProgress,
    string? Error,
    bool IsLongLived,
    string? Detail)
{
    public string ToolName { get; init; } = "";
}
