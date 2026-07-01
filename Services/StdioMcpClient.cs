using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevSpaceManager.Services;

internal sealed class StdioMcpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MountedMcpDefinition _definition;
    private readonly ConcurrentDictionary<string, PendingMcpRequest> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly Queue<string> _recentErrors = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextId;
    private bool _initialized;
    private bool _disposed;
    private JsonElement? _initializeResult;
    private StdioMcpClientState _state = StdioMcpClientState.Stopped;
    private string _lastError = "";

    public StdioMcpClient(MountedMcpDefinition definition)
    {
        _definition = definition;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifetimeLock)
            {
                return _state is not StdioMcpClientState.Failed and not StdioMcpClientState.Disposed
                    && _process is { HasExited: false };
            }
        }
    }

    public StdioMcpClientSnapshot Snapshot
    {
        get
        {
            lock (_lifetimeLock)
            {
                return new StdioMcpClientSnapshot(
                    _state,
                    _pending.Count,
                    _lastError,
                    _recentErrors.Reverse().ToArray());
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            EnsureProcess();
            if (_initialized) return;

            try
            {
                var response = await RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "DevSpaceManager",
                        ["version"] = "0.1.0"
                    }
                }, TimeSpan.FromMilliseconds(_definition.StartupTimeoutMs), cancellationToken);

                ThrowIfError(response, "初始化失败");
                _initializeResult = ResultOrEmpty(response);

                await NotifyAsync("notifications/initialized", cancellationToken);
                lock (_lifetimeLock)
                {
                    _initialized = true;
                    _state = StdioMcpClientState.Ready;
                    _lastError = "";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailConnection(ex);
                throw;
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public JsonObject InitializeResult
    {
        get
        {
            lock (_lifetimeLock)
            {
                return JsonElementToObject(_initializeResult);
            }
        }
    }

    public async Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await RequireResultAsync(
            RequestAsync("tools/list", new JsonObject(), TimeSpan.FromSeconds(30), cancellationToken),
            "读取工具列表失败");
    }

    public async Task<JsonObject> CallToolAsync(string toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var argsNode = arguments.HasValue
            ? JsonNode.Parse(arguments.Value.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        return await RequireResultAsync(RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = argsNode
        }, TimeSpan.FromMinutes(5), cancellationToken), $"调用工具 {toolName} 失败");
    }

    private async Task<JsonObject> RequireResultAsync(Task<McpRpcResponse> responseTask, string message)
    {
        var response = await responseTask;
        ThrowIfError(response, message);
        return JsonElementToObject(ResultOrEmpty(response));
    }

    private void EnsureProcess()
    {
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false }) return;

            DisposeProcess("MCP 进程正在重新启动。");
            _cts = new CancellationTokenSource();
            _state = StdioMcpClientState.Starting;
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveCommand(_definition.Command),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in _definition.Args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            foreach (var (key, value) in _definition.Env)
            {
                startInfo.Environment[key] = value;
            }

            _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 MCP：{_definition.Name}");
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;
            _stdoutTask = Task.Run(() => ReadStdoutAsync(_process, _cts.Token));
            _stderrTask = Task.Run(() => ReadStderrAsync(_process, _cts.Token));
            _initialized = false;
        }
    }

    private async Task<McpRpcResponse> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString();
        var pending = new PendingMcpRequest(
            key,
            method,
            ReadToolName(method, parameters));
        if (!_pending.TryAdd(key, pending))
        {
            throw new InvalidOperationException($"{_definition.Name} MCP 请求 ID 冲突：{key}");
        }

        try
        {
            await WriteJsonAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            }, cancellationToken);
        }
        catch
        {
            _pending.TryRemove(key, out _);
            throw;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(key, out var request))
            {
                request.Fail(new MountedMcpTimeoutException($"{DescribeRequest(request)} 超时。"));
            }
        });
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(key, out var request))
            {
                request.Cancel(cancellationToken);
            }
        });

        try
        {
            return await pending.Completion.Task;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken cancellationToken) => WriteJsonAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method
    }, cancellationToken);

    private async Task WriteJsonAsync(JsonObject message, CancellationToken cancellationToken)
    {
        StreamWriter writer;
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            writer = _stdin ?? throw new InvalidOperationException($"{_definition.Name} MCP 尚未启动。");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(message.ToJsonString(JsonOptions).AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (!TryReadResponse(line, out var response, out var error))
                {
                    var snippet = line.Length <= 500 ? line : line[..500] + "...";
                    var message = $"{_definition.Name} MCP 输出了非法 JSON：{error}; stdout={snippet}";
                    Log.App($"Mounted MCP {message}");
                    FailConnection(new MountedMcpProtocolException($"{_definition.Name} MCP 输出了非法 JSON，连接已重置。", error));
                    return;
                }

                if (response.Id is null) continue;
                if (_pending.TryRemove(response.Id, out var pending))
                {
                    pending.Succeed(response);
                }
                else
                {
                    Log.App($"Mounted MCP {_definition.Name} returned an unknown response id: {response.Id}");
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                FailConnection(new IOException($"{_definition.Name} MCP 输出流已关闭。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailConnection(ex);
        }
    }

    private static bool TryReadResponse(string line, out McpRpcResponse response, out Exception error)
    {
        response = default;
        error = new InvalidDataException("未知解析错误。");
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = new InvalidDataException("JSON-RPC 消息必须是 object。");
                return false;
            }

            var id = TryReadId(root);
            response = new McpRpcResponse(id, root.Clone());
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static string? TryReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var number) => number.ToString(),
            JsonValueKind.String => id.GetString(),
            _ => id.GetRawText()
        };
    }

    private static string ResolveCommand(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return command;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var path in paths)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(path, command.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? command : command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return command;
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(line)) Log.App($"Mounted MCP {_definition.Name}: {line}");
                if (line is null) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // stderr is diagnostics only; stdout owns the MCP protocol state.
        }
    }

    private void FailConnection(Exception ex)
    {
        PendingMcpRequest[] pending;
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            RememberError(ex.Message);
            _state = StdioMcpClientState.Failed;
            _initialized = false;
            _initializeResult = null;
            pending = _pending.Values.ToArray();
            _pending.Clear();
            try { _cts?.Cancel(); } catch { }
            TryKillProcess();
            DisposeProcessHandles();
        }

        foreach (var request in pending)
        {
            request.Fail(ex);
        }
    }

    private void DisposeProcess(string pendingMessage)
    {
        var ex = new IOException($"{_definition.Name} {pendingMessage}");
        foreach (var request in _pending.Values.ToArray())
        {
            request.Fail(ex);
        }
        _pending.Clear();

        try { _cts?.Cancel(); } catch { }
        TryKillProcess();
        DisposeProcessHandles();
        _initialized = false;
        _initializeResult = null;
        if (!_disposed && _state != StdioMcpClientState.Failed)
        {
            _state = StdioMcpClientState.Stopped;
        }
    }

    private void TryKillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private void DisposeProcessHandles()
    {
        try { _stdin?.Close(); } catch { }
        try { _process?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _stdin = null;
        _process = null;
        _cts = null;
        _stdoutTask = null;
        _stderrTask = null;
    }

    private void RememberError(string message)
    {
        _lastError = message;
        _recentErrors.Enqueue($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");
        while (_recentErrors.Count > 20)
        {
            _recentErrors.Dequeue();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StdioMcpClient));
        }
    }

    private void ThrowIfError(McpRpcResponse response, string message)
    {
        if (response.Root.TryGetProperty("error", out var error))
        {
            throw new MountedMcpRemoteException($"{_definition.Name} MCP {message}：{error.GetRawText()}");
        }
    }

    private static JsonElement ResultOrEmpty(McpRpcResponse response) =>
        response.Root.TryGetProperty("result", out var result)
            ? result.Clone()
            : EmptyObjectElement();

    private static JsonElement EmptyObjectElement()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonObject JsonElementToObject(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new JsonObject();
        }

        return JsonNode.Parse(element.Value.GetRawText()) as JsonObject ?? new JsonObject();
    }

    private static string? ReadToolName(string method, JsonObject parameters)
    {
        if (!string.Equals(method, "tools/call", StringComparison.Ordinal)) return null;
        return parameters["name"]?.GetValue<string>();
    }

    private string DescribeRequest(PendingMcpRequest request)
    {
        var tool = string.IsNullOrWhiteSpace(request.ToolName) ? "" : $" {request.ToolName}";
        return $"{_definition.Name} MCP {request.Method}{tool} 请求 {request.Id}";
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _state = StdioMcpClientState.Disposed;
            DisposeProcess("MCP 客户端已停止。");
        }

        _writeLock.Dispose();
        _initializeLock.Dispose();
    }
}

internal enum StdioMcpClientState
{
    Stopped,
    Starting,
    Ready,
    Failed,
    Disposed
}

internal sealed record StdioMcpClientSnapshot(
    StdioMcpClientState State,
    int PendingCount,
    string LastError,
    IReadOnlyList<string> RecentErrors);

internal readonly record struct McpRpcResponse(string? Id, JsonElement Root);

internal sealed class PendingMcpRequest
{
    public PendingMcpRequest(string id, string method, string? toolName)
    {
        Id = id;
        Method = method;
        ToolName = toolName;
    }

    public string Id { get; }
    public string Method { get; }
    public string? ToolName { get; }
    public TaskCompletionSource<McpRpcResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Succeed(McpRpcResponse response) => Completion.TrySetResult(response);

    public void Fail(Exception ex) => Completion.TrySetException(ex);

    public void Cancel(CancellationToken cancellationToken) => Completion.TrySetCanceled(cancellationToken);
}

internal sealed class MountedMcpRemoteException(string message) : InvalidOperationException(message);

internal sealed class MountedMcpProtocolException(string message, Exception innerException)
    : IOException(message, innerException);

internal sealed class MountedMcpTimeoutException(string message) : TimeoutException(message);
