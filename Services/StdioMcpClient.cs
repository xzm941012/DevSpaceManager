using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevSpaceManager.Services;

internal sealed class StdioMcpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MountedMcpDefinition _definition;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lifetimeLock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextId;
    private bool _initialized;
    private JsonObject _initializeResult = new();

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
                return _process is { HasExited: false };
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        EnsureProcess();
        if (_initialized) return;

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

        if (response["error"] is JsonNode error)
        {
            throw new MountedMcpRemoteException($"{_definition.Name} 初始化失败：{error.ToJsonString(JsonOptions)}");
        }

        _initializeResult = response["result"] as JsonObject ?? new JsonObject();

        await NotifyAsync("notifications/initialized", cancellationToken);
        _initialized = true;
    }

    public JsonObject InitializeResult => _initializeResult.DeepClone().AsObject();

    public async Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await RequireResultAsync(RequestAsync("tools/list", new JsonObject(), TimeSpan.FromSeconds(30), cancellationToken), "读取工具列表失败");
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
        }, TimeSpan.FromMinutes(5), cancellationToken), "调用工具失败");
    }

    private async Task<JsonObject> RequireResultAsync(Task<JsonObject> responseTask, string message)
    {
        var response = await responseTask;
        if (response["error"] is JsonNode error)
        {
            throw new MountedMcpRemoteException($"{_definition.Name} {message}：{error.ToJsonString(JsonOptions)}");
        }

        return response["result"]?.DeepClone() as JsonObject ?? new JsonObject();
    }

    private void EnsureProcess()
    {
        lock (_lifetimeLock)
        {
            if (_process is { HasExited: false }) return;

            DisposeProcess();
            _cts = new CancellationTokenSource();
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

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await WriteJsonAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var registration = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
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

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (Exception ex)
                {
                    var snippet = line.Length <= 500 ? line : line[..500] + "...";
                    Log.App($"Mounted MCP {_definition.Name} wrote invalid JSON: {ex.Message}; stdout={snippet}");
                    FailProtocol(ex);
                    return;
                }

                if (message?["id"] is null) continue;
                var id = message["id"]!.GetValue<long>();
                if (_pending.TryGetValue(id, out var pending))
                {
                    pending.TrySetResult(message);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                FailPending(new IOException($"{_definition.Name} MCP 输出流已关闭。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
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
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) Log.App($"Mounted MCP {_definition.Name}: {line}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // stderr is best-effort diagnostics only.
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var (_, pending) in _pending)
        {
            pending.TrySetException(ex);
        }
    }

    private void FailProtocol(Exception ex)
    {
        lock (_lifetimeLock)
        {
            FailPending(new MountedMcpProtocolException($"{_definition.Name} MCP 输出了非法 JSON，连接已重置。", ex));
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _stdin = null;
            _process?.Dispose();
            _process = null;
            _cts?.Dispose();
            _cts = null;
            _stdoutTask = null;
            _stderrTask = null;
            _initialized = false;
            _initializeResult = new JsonObject();
            _pending.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            DisposeProcess();
        }

        _writeLock.Dispose();
    }

    private void DisposeProcess()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        _stdin = null;
        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
        _stdoutTask = null;
        _stderrTask = null;
        _initialized = false;
        _initializeResult = new JsonObject();
        FailPending(new IOException($"{_definition.Name} MCP 已停止。"));
        _pending.Clear();
    }
}

internal sealed class MountedMcpRemoteException(string message) : InvalidOperationException(message);

internal sealed class MountedMcpProtocolException(string message, Exception innerException)
    : IOException(message, innerException);
