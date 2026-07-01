using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class McpProxyService : IDisposable
{
    private const int MaxObservedBodyBytes = 16 * 1024;

    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Connection",
        "Content-Length",
        "Content-Type",
        "Content-Encoding",
        "Content-Language",
        "Expect",
        "Proxy-Connection",
        "Transfer-Encoding",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Port",
        "X-Forwarded-Proto",
        "X-Real-IP",
        "Cf-Connecting-IP"
    };

    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Connection",
        "Transfer-Encoding",
        "Trailer",
        "Upgrade"
    };

    private static readonly HashSet<string> LoggedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/mcp",
        "/register",
        "/authorize",
        "/token",
        "/revoke",
        "/.well-known/oauth-protected-resource/mcp",
        "/.well-known/oauth-authorization-server"
    };

    private readonly ManagerConfigStore _configStore;
    private readonly McpRequestMonitor _monitor;
    private readonly MountedMcpService _mountedMcps;
    private readonly HttpClient _client;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _listeningPort;

    public McpProxyService(ManagerConfigStore configStore, McpRequestMonitor monitor, MountedMcpService mountedMcps)
    {
        _configStore = configStore;
        _monitor = monitor;
        _mountedMcps = mountedMcps;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = int.MaxValue
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public bool IsRunning => _listener?.IsListening == true;

    public void EnsureState()
    {
        var config = _configStore.Reload();
        if (!config.RequestProxyEnabled)
        {
            Stop();
            return;
        }

        if (IsRunning && _listeningPort == config.RequestProxyPort) return;

        Stop();
        Start(config.RequestProxyPort);
    }

    public void Start(int port)
    {
        if (IsRunning && _listeningPort == port) return;

        Stop();
        _listeningPort = port;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        var listener = _listener;
        _cts = null;
        _listener = null;
        _acceptLoop = null;
        _listeningPort = 0;

        try { cts?.Cancel(); } catch { }
        try { listener?.Close(); } catch { }
        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync();
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested) await Task.Delay(100, cancellationToken).ContinueWith(_ => { });
                continue;
            }

            _ = Task.Run(() => ProxyAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ProxyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var rawUrl = request.RawUrl ?? request.Url?.PathAndQuery ?? "/";
        var entry = _monitor.Start(request.HttpMethod, rawUrl);
        var config = _configStore.Current;

        try
        {
            byte[] observedBody = [];

            if (request.HasEntityBody)
            {
                if (ShouldObserveRequestBody(request, rawUrl))
                {
                    observedBody = await ReadRequestBodyAsync(request, cancellationToken);
                    _ = Task.Run(() => _monitor.SetTool(entry.Id, McpRequestMonitor.ExtractToolName(observedBody)));
                }
            }

            var targetUri = BuildTargetUri(config.DevSpacePort, rawUrl);
            var canRetryForward = !request.HasEntityBody || observedBody.Length > 0;
            var (forward, response) = await SendForwardAsync(
                () => CreateForwardRequest(request, targetUri, observedBody),
                canRetryForward,
                cancellationToken);

            using (forward)
            using (response)
            {

                if (observedBody.Length > 0 &&
                    response.IsSuccessStatusCode &&
                    await TryHandleMountedMcpCallAsync(context, observedBody, entry.Id, stopwatch, cancellationToken))
                {
                    LogProxyRequest(request, rawUrl, 200, stopwatch.ElapsedMilliseconds);
                    return;
                }

                context.Response.StatusCode = (int)response.StatusCode;
                if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
                {
                    context.Response.StatusDescription = response.ReasonPhrase;
                }
                CopyResponseHeaders(response, context.Response);

                if (await TryWriteInjectedResponseAsync(context, response, request, rawUrl, forward.Content, cancellationToken))
                {
                    stopwatch.Stop();
                    _monitor.Complete(entry.Id, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
                    LogProxyRequest(request, rawUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
                    return;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await responseStream.CopyToAsync(context.Response.OutputStream, cancellationToken);
                stopwatch.Stop();
                if (entry.IsLongLived)
                {
                    _monitor.Disconnect(entry.Id, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, DescribeLongLivedDisconnect(response.StatusCode));
                }
                else
                {
                    _monitor.Complete(entry.Id, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
                }
                LogProxyRequest(request, rawUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _monitor.Disconnect(entry.Id, null, stopwatch.ElapsedMilliseconds, "代理正在停止，连接已关闭");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (entry.IsLongLived && IsClientDisconnect(ex))
            {
                _monitor.Disconnect(entry.Id, null, stopwatch.ElapsedMilliseconds, "客户端主动断开连接");
            }
            else
            {
                _monitor.Fail(entry.Id, 502, stopwatch.ElapsedMilliseconds, ex.Message, DescribeFailure(ex, entry.IsLongLived));
            }
            LogProxyRequest(request, rawUrl, 502, stopwatch.ElapsedMilliseconds, ex.Message);

            if (!context.Response.OutputStream.CanWrite) return;

            try
            {
                context.Response.StatusCode = 502;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync("DevSpace proxy failed.");
            }
            catch
            {
                // The client may have disconnected; the monitor entry already captured the failure.
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static Uri BuildTargetUri(int port, string rawUrl)
    {
        var pathAndQuery = string.IsNullOrWhiteSpace(rawUrl) ? "/" : rawUrl;
        if (!pathAndQuery.StartsWith('/')) pathAndQuery = "/" + pathAndQuery;
        return new Uri($"http://127.0.0.1:{port}{pathAndQuery}");
    }

    private static bool ShouldObserveRequestBody(HttpListenerRequest request, string rawUrl)
    {
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (!rawUrl.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)) return false;
        if (request.ContentLength64 is <= 0 or > MaxObservedBodyBytes) return false;
        var contentType = request.ContentType ?? "";
        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(HttpRequestMessage Request, HttpResponseMessage Response)> SendForwardAsync(
        Func<HttpRequestMessage> createRequest,
        bool canRetry,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var forward = createRequest();
            try
            {
                var response = await _client.SendAsync(
                    forward,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                return (forward, response);
            }
            catch (Exception ex) when (canRetry && attempt < maxAttempts && IsRetryableUpstreamFailure(ex))
            {
                forward.Dispose();
                await Task.Delay(RetryDelay(attempt), cancellationToken);
            }
            catch
            {
                forward.Dispose();
                throw;
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt switch
        {
            1 => 250,
            2 => 500,
            3 => 1000,
            _ => 1500
        });

    private static bool IsRetryableUpstreamFailure(Exception ex) =>
        ex is HttpRequestException or IOException;

    private static HttpRequestMessage CreateForwardRequest(HttpListenerRequest request, Uri targetUri, byte[] observedBody)
    {
        var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);
        if (request.HasEntityBody)
        {
            forward.Content = observedBody.Length > 0
                ? CreateBufferedForwardContent(request, observedBody)
                : CreateStreamForwardContent(request);
        }

        CopyRequestHeaders(request, forward);
        return forward;
    }

    private async Task<bool> TryHandleMountedMcpCallAsync(
        HttpListenerContext context,
        byte[] body,
        Guid entryId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!TryParseJsonRpc(body, out var rpc)) return false;
        if (!string.Equals(rpc.Method, "tools/call", StringComparison.Ordinal)) return false;
        if (rpc.Params?["name"]?.GetValue<string>() is not { } name) return false;
        if (name is not "tool_search" and not "call_mounted_tool") return false;

        JsonObject result;
        try
        {
            var arguments = rpc.Params?["arguments"] as JsonObject ?? new JsonObject();
            result = name == "tool_search"
                ? _mountedMcps.SearchForMcpResult(arguments)
                : await _mountedMcps.CallMountedToolForMcpResultAsync(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, JsonRpcError(rpc.Id, -32000, ex.Message), cancellationToken);
            stopwatch.Stop();
            _monitor.Fail(entryId, 200, stopwatch.ElapsedMilliseconds, ex.Message, "挂载 MCP 工具调用失败");
            return true;
        }

        await WriteJsonAsync(context.Response, JsonRpcResult(rpc.Id, result), cancellationToken);
        stopwatch.Stop();
        _monitor.Complete(entryId, 200, stopwatch.ElapsedMilliseconds);
        return true;
    }

    private async Task<bool> TryWriteInjectedResponseAsync(
        HttpListenerContext context,
        HttpResponseMessage response,
        HttpListenerRequest request,
        string rawUrl,
        HttpContent? requestContent,
        CancellationToken cancellationToken)
    {
        if (!ShouldInjectResponse(request, rawUrl, requestContent)) return false;

        var requestBytes = requestContent is ByteArrayContent
            ? await requestContent.ReadAsByteArrayAsync(cancellationToken)
            : [];
        if (!TryParseJsonRpc(requestBytes, out var requestRpc)) return false;
        if (requestRpc.Method is not ("initialize" or "tools/list")) return false;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText)) return false;

        var isEventStream = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        if (!TryReadJsonResponse(responseText, isEventStream, out var responseJson)) return false;

        if (responseJson is null) return false;
        if (requestRpc.Method == "tools/list")
        {
            InjectTools(responseJson);
        }
        else
        {
            InjectInstructions(responseJson);
        }

        context.Response.StatusCode = (int)response.StatusCode;
        if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
        {
            context.Response.StatusDescription = response.ReasonPhrase;
        }

        CopyResponseHeaders(response, context.Response);
        context.Response.Headers.Remove("Content-Length");
        if (isEventStream)
        {
            await WriteSseJsonAsync(context.Response, responseJson, cancellationToken);
        }
        else
        {
            await WriteJsonAsync(context.Response, responseJson, cancellationToken);
        }

        return true;
    }

    private static bool TryReadJsonResponse(string responseText, bool isEventStream, out JsonObject? responseJson)
    {
        responseJson = null;
        var jsonText = isEventStream ? ExtractSingleSseData(responseText) : responseText;
        if (string.IsNullOrWhiteSpace(jsonText)) return false;

        try
        {
            responseJson = JsonNode.Parse(jsonText) as JsonObject;
            return responseJson is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractSingleSseData(string responseText)
    {
        var data = new StringBuilder();
        using var reader = new StringReader(responseText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (data.Length > 0) data.Append('\n');
            data.Append(line[5..].TrimStart());
        }

        return data.Length == 0 ? null : data.ToString();
    }

    private static bool ShouldInjectResponse(HttpListenerRequest request, string rawUrl, HttpContent? requestContent)
    {
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (!rawUrl.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)) return false;
        if (requestContent is not ByteArrayContent) return false;
        return true;
    }

    private void InjectTools(JsonObject responseJson)
    {
        if (responseJson["result"] is not JsonObject result) return;
        if (result["tools"] is not JsonArray tools)
        {
            tools = [];
            result["tools"] = tools;
        }

        AddToolIfMissing(tools, _mountedMcps.ToolSearchDescriptor());
        AddToolIfMissing(tools, _mountedMcps.CallMountedToolDescriptor());
    }

    private void InjectInstructions(JsonObject responseJson)
    {
        var addition = _mountedMcps.BuildProxyInstructions();
        if (string.IsNullOrWhiteSpace(addition)) return;
        if (responseJson["result"] is not JsonObject result) return;

        var current = result["instructions"]?.GetValue<string>() ?? "";
        if (current.Contains("tool_search", StringComparison.OrdinalIgnoreCase)) return;
        result["instructions"] = string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()}\n\n{addition}";
    }

    private static void AddToolIfMissing(JsonArray tools, JsonObject descriptor)
    {
        var name = descriptor["name"]?.GetValue<string>() ?? "";
        if (tools.OfType<JsonObject>().Any(tool => string.Equals(tool["name"]?.GetValue<string>(), name, StringComparison.Ordinal))) return;
        tools.Add(descriptor);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteSseJsonAsync(HttpListenerResponse response, JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {payload.ToJsonString()}\n\n");
        response.ContentType = "text/event-stream";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static JsonObject JsonRpcResult(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result.DeepClone()
    };

    private static JsonObject JsonRpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static bool TryParseJsonRpc(byte[] body, out JsonRpcEnvelope rpc)
    {
        rpc = default;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json) return false;
            rpc = new JsonRpcEnvelope(
                json["id"]?.DeepClone(),
                json["method"]?.GetValue<string>() ?? "",
                json["params"] as JsonObject);
            return !string.IsNullOrWhiteSpace(rpc.Method);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var capacity = request.ContentLength64 is > 0 and <= int.MaxValue
            ? (int)request.ContentLength64
            : 0;
        using var buffer = new MemoryStream(capacity);
        await request.InputStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static HttpContent CreateBufferedForwardContent(HttpListenerRequest request, byte[] body)
    {
        var content = new ByteArrayContent(body);
        CopyContentHeaders(request, content);
        return content;
    }

    private static HttpContent CreateStreamForwardContent(HttpListenerRequest request)
    {
        var content = new StreamContent(request.InputStream);
        CopyContentHeaders(request, content);
        if (request.ContentLength64 >= 0)
        {
            content.Headers.ContentLength = request.ContentLength64;
        }

        return content;
    }

    private static void CopyRequestHeaders(HttpListenerRequest source, HttpRequestMessage target)
    {
        foreach (var key in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || SkippedRequestHeaders.Contains(key)) continue;
            var values = source.Headers.GetValues(key);
            if (values is null) continue;
            if (!target.Headers.TryAddWithoutValidation(key, values))
            {
                target.Content?.Headers.TryAddWithoutValidation(key, values);
            }
        }
    }

    private static string DescribeLongLivedDisconnect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 200 && code < 400
            ? "长连接正常结束"
            : $"长连接结束，状态码 {code}";
    }

    private static string DescribeFailure(Exception ex, bool isLongLived)
    {
        if (ex is HttpRequestException httpEx)
        {
            return isLongLived
                ? $"转发过程中断开：{httpEx.Message}"
                : $"转发失败：{httpEx.Message}";
        }

        if (ex is IOException ioEx)
        {
            return isLongLived
                ? $"连接被中止：{ioEx.Message}"
                : $"I/O 失败：{ioEx.Message}";
        }

        return isLongLived
            ? $"连接异常结束：{ex.Message}"
            : ex.Message;
    }

    private static bool IsClientDisconnect(Exception ex)
    {
        if (ex is HttpListenerException listenerEx)
        {
            return listenerEx.ErrorCode is 64 or 995 or 1229;
        }

        return ex is IOException;
    }

    private static void LogProxyRequest(HttpListenerRequest request, string rawUrl, int statusCode, long elapsedMs, string? error = null)
    {
        if (!ShouldLogProxyRequest(rawUrl, statusCode, error)) return;

        var path = request.Url?.AbsolutePath ?? rawUrl.Split('?', 2)[0];
        var userAgent = request.UserAgent ?? "";
        if (userAgent.Length > 120) userAgent = userAgent[..120];
        var suffix = string.IsNullOrWhiteSpace(error) ? "" : $" error={error}";
        Log.App($"Proxy {request.HttpMethod} {path} -> {statusCode} in {elapsedMs}ms ua={userAgent}{suffix}");
    }

    private static bool ShouldLogProxyRequest(string rawUrl, int statusCode, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error) || statusCode >= 400) return true;
        var path = rawUrl.Split('?', 2)[0];
        return LoggedPaths.Contains(path);
    }

    private static void CopyContentHeaders(HttpListenerRequest source, HttpContent content)
    {
        if (!string.IsNullOrWhiteSpace(source.ContentType))
        {
            content.Headers.TryAddWithoutValidation("Content-Type", source.ContentType);
        }

        foreach (var key in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Content-Language", StringComparison.OrdinalIgnoreCase))
            {
                var values = source.Headers.GetValues(key);
                if (values is not null)
                {
                    content.Headers.TryAddWithoutValidation(key, values);
                }
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpListenerResponse target)
    {
        foreach (var header in source.Headers)
        {
            if (SkippedResponseHeaders.Contains(header.Key)) continue;
            target.Headers[header.Key] = string.Join(",", header.Value);
        }

        foreach (var header in source.Content.Headers)
        {
            if (SkippedResponseHeaders.Contains(header.Key)) continue;
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(header.Value.FirstOrDefault(), out var length))
                {
                    target.ContentLength64 = length;
                }
                continue;
            }

            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                target.ContentType = header.Value.FirstOrDefault();
                continue;
            }

            target.Headers[header.Key] = string.Join(",", header.Value);
        }
    }

    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }
}

internal readonly record struct JsonRpcEnvelope(JsonNode? Id, string Method, JsonObject? Params);
