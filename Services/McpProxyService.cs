using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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

    private readonly ManagerConfigStore _configStore;
    private readonly McpRequestMonitor _monitor;
    private readonly HttpClient _client;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _listeningPort;

    public McpProxyService(ManagerConfigStore configStore, McpRequestMonitor monitor)
    {
        _configStore = configStore;
        _monitor = monitor;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseProxy = false,
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
        var entry = _monitor.Start(request.HttpMethod, request.RawUrl ?? request.Url?.PathAndQuery ?? "/");
        var config = _configStore.Current;

        try
        {
            var targetUri = BuildTargetUri(config.DevSpacePort, request.RawUrl ?? request.Url?.PathAndQuery ?? "/");
            using var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);

            if (request.HasEntityBody)
            {
                var observedBody = Array.Empty<byte>();
                if (ShouldObserveRequestBody(request))
                {
                    observedBody = await ReadObservedBytesAsync(request.InputStream, cancellationToken);
                    if (observedBody.Length > 0)
                    {
                        _ = Task.Run(() => _monitor.SetTool(entry.Id, McpRequestMonitor.ExtractToolName(observedBody)));
                    }
                }

                forward.Content = await CreateForwardContentAsync(request, observedBody, cancellationToken);
            }

            CopyRequestHeaders(request, forward);
            using var response = await _client.SendAsync(
                forward,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            context.Response.StatusCode = (int)response.StatusCode;
            if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
            {
                context.Response.StatusDescription = response.ReasonPhrase;
            }
            CopyResponseHeaders(response, context.Response);

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

    private static bool ShouldObserveRequestBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 == 0) return false;
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (!request.InputStream.CanSeek && (request.ContentLength64 < 0 || request.ContentLength64 > MaxObservedBodyBytes)) return false;
        var contentType = request.ContentType ?? "";
        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadObservedBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        var length = MaxObservedBodyBytes;
        var buffer = new byte[length];
        var totalRead = 0;

        while (totalRead < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
            if (source.CanSeek && source.Position >= source.Length) break;
        }

        if (source.CanSeek)
        {
            source.Position = 0;
        }

        if (totalRead == 0) return Array.Empty<byte>();
        if (totalRead == buffer.Length) return buffer;

        var exact = new byte[totalRead];
        Buffer.BlockCopy(buffer, 0, exact, 0, totalRead);
        return exact;
    }

    private static async Task<HttpContent> CreateForwardContentAsync(HttpListenerRequest request, byte[] observedBody, CancellationToken cancellationToken)
    {
        HttpContent content;
        if (request.InputStream.CanSeek)
        {
            request.InputStream.Position = 0;
            content = new StreamContent(request.InputStream);
        }
        else if (observedBody.Length > 0 && request.ContentLength64 > 0 && request.ContentLength64 <= observedBody.Length)
        {
            content = new ByteArrayContent(observedBody, 0, (int)Math.Min(observedBody.Length, request.ContentLength64));
        }
        else if (observedBody.Length == 0)
        {
            content = new StreamContent(request.InputStream);
        }
        else
        {
            using var buffer = new MemoryStream(request.ContentLength64 > 0 && request.ContentLength64 < int.MaxValue
                ? (int)request.ContentLength64
                : 0);
            await request.InputStream.CopyToAsync(buffer, cancellationToken);
            content = new ByteArrayContent(buffer.ToArray());
        }

        CopyContentHeaders(request, content);
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

    private static void CopyContentHeaders(HttpListenerRequest source, HttpContent content)
    {
        foreach (var key in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
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
