using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevSpaceManager.Core;
using Microsoft.Web.WebView2.Core;

namespace DevSpaceManager.Services;

internal sealed partial class ChatGptTaskCompletionMonitor : IDisposable
{
    private const int MaxNotificationTitleLength = 63;
    private const int MaxNotificationBodyLength = 220;
    private static readonly TimeSpan VisibleCompletionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VisibleCompletionPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InternalToastTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InternalToastPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TitleGenerationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TitleGenerationPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly AppHost _app;
    private readonly Dictionary<string, PendingChatGptTask> _pending = new(StringComparer.Ordinal);
    private CoreWebView2? _webView;
    private CoreWebView2DevToolsProtocolEventReceiver? _requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _finishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _failedReceiver;
    private bool _disposed;

    public ChatGptTaskCompletionMonitor(AppHost app)
    {
        _app = app;
    }

    public async Task AttachAsync(CoreWebView2 webView)
    {
        _webView = webView;
        _requestReceiver = webView.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _responseReceiver = webView.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _finishedReceiver = webView.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _failedReceiver = webView.GetDevToolsProtocolEventReceiver("Network.loadingFailed");

        _requestReceiver.DevToolsProtocolEventReceived += OnRequestWillBeSent;
        _responseReceiver.DevToolsProtocolEventReceived += OnResponseReceived;
        _finishedReceiver.DevToolsProtocolEventReceived += OnLoadingFinished;
        _failedReceiver.DevToolsProtocolEventReceived += OnLoadingFailed;
        await webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
    }

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        try
        {
            if (!NotificationsEnabled())
            {
                return;
            }

            using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
            var root = document.RootElement;
            var request = root.GetProperty("request");
            var url = request.GetProperty("url").GetString() ?? "";
            var method = request.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? ""
                : "";
            if (!IsConversationStreamRequest(url, method))
            {
                return;
            }

            var requestId = root.GetProperty("requestId").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var postData = request.TryGetProperty("postData", out var postDataElement)
                ? postDataElement.GetString() ?? ""
                : "";
            var pending = new PendingChatGptTask(
                requestId,
                ExtractJsonString(postData, "conversation_id"),
                ExtractJsonString(postData, "parent_message_id"),
                DateTimeOffset.Now);
            _pending[requestId] = pending;
            _ = CaptureConversationTitleAsync(pending);
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor request handling failed: {ex.Message}");
        }
    }

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
            var root = document.RootElement;
            var requestId = root.GetProperty("requestId").GetString() ?? "";
            if (!_pending.TryGetValue(requestId, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("response", out var response) &&
                response.TryGetProperty("status", out var status) &&
                status.TryGetInt32(out var statusCode))
            {
                pending.StatusCode = statusCode;
            }
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor response handling failed: {ex.Message}");
        }
    }

    private void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        _ = CompleteAsync(args.ParameterObjectAsJson, failed: false);
    }

    private void OnLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        _ = CompleteAsync(args.ParameterObjectAsJson, failed: true);
    }

    private async Task CompleteAsync(string parameterJson, bool failed)
    {
        PendingChatGptTask? pending = null;
        try
        {
            using var document = JsonDocument.Parse(parameterJson);
            var requestId = document.RootElement.GetProperty("requestId").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(requestId) || !_pending.Remove(requestId, out pending))
            {
                return;
            }

            if (failed || !NotificationsEnabled())
            {
                return;
            }

            var responseBody = await TryGetResponseBodyAsync(requestId);
            if (string.IsNullOrWhiteSpace(pending.ConversationId))
            {
                pending.ConversationId = ExtractJsonStringFromAnywhere(responseBody, "conversation_id");
            }

            await CaptureConversationTitleAsync(pending);
            var answer = Truncate(CollapseWhitespace(ExtractAssistantText(responseBody)), MaxNotificationBodyLength);
            var visibleState = await GetVisibleCompletionStateAsync(pending);
            if (visibleState.IsVisible && visibleState.IsGenerating)
            {
                var visibleAnswer = await WaitForVisibleCompletionAsync(pending);
                if (!string.IsNullOrWhiteSpace(visibleAnswer))
                {
                    answer = Truncate(CollapseWhitespace(visibleAnswer), MaxNotificationBodyLength);
                }
                else
                {
                    var toast = await WaitForInternalCompletionToastAsync(pending);
                    if (!toast.Found)
                    {
                        return;
                    }

                    ApplyInternalToast(pending, toast, ref answer);
                }
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                var visibleAnswer = visibleState.IsVisible && !visibleState.IsGenerating
                    ? visibleState.AssistantText
                    : await WaitForVisibleCompletionAsync(pending);
                if (string.IsNullOrWhiteSpace(visibleAnswer))
                {
                    var toast = await WaitForInternalCompletionToastAsync(pending);
                    if (!toast.Found)
                    {
                        return;
                    }

                    ApplyInternalToast(pending, toast, ref answer);
                }
                else
                {
                    answer = Truncate(CollapseWhitespace(visibleAnswer), MaxNotificationBodyLength);
                }
            }

            await WaitForGeneratedTitleAsync(pending);
            var title = Truncate(string.IsNullOrWhiteSpace(pending.Title)
                ? "ChatGPT 任务完成"
                : pending.Title, MaxNotificationTitleLength);
            _app.RequestNativeNotification(title, answer, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor completion failed: {ex.Message}");
            if (pending is not null)
            {
                _pending.Remove(pending.RequestId);
            }
        }
    }

    private async Task<string> WaitForVisibleCompletionAsync(PendingChatGptTask pending)
    {
        var deadline = DateTimeOffset.Now.Add(VisibleCompletionTimeout);
        var previousText = "";
        var stableCount = 0;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(VisibleCompletionPollInterval);
            if (!NotificationsEnabled())
            {
                return "";
            }

            var state = await GetVisibleCompletionStateAsync(pending);
            if (!state.IsVisible)
            {
                return "";
            }

            if (state.IsGenerating || string.IsNullOrWhiteSpace(state.AssistantText))
            {
                previousText = state.AssistantText;
                stableCount = 0;
                continue;
            }

            if (string.Equals(previousText, state.AssistantText, StringComparison.Ordinal))
            {
                stableCount++;
                if (stableCount >= 1)
                {
                    return state.AssistantText;
                }
            }
            else
            {
                previousText = state.AssistantText;
                stableCount = 1;
            }
        }

        return "";
    }

    private async Task<InternalCompletionToastState> WaitForInternalCompletionToastAsync(PendingChatGptTask pending)
    {
        var deadline = DateTimeOffset.Now.Add(InternalToastTimeout);
        while (DateTimeOffset.Now < deadline)
        {
            if (!NotificationsEnabled())
            {
                return InternalCompletionToastState.NotFound;
            }

            await CaptureConversationTitleAsync(pending);
            var toast = await GetInternalCompletionToastAsync(pending);
            if (toast.Found)
            {
                return toast;
            }

            await Task.Delay(InternalToastPollInterval);
        }

        return InternalCompletionToastState.NotFound;
    }

    private async Task<InternalCompletionToastState> GetInternalCompletionToastAsync(PendingChatGptTask pending)
    {
        try
        {
            if (_webView is null)
            {
                return InternalCompletionToastState.NotFound;
            }

            var conversationId = EscapeJavaScriptString(pending.ConversationId);
            var expectedTitle = EscapeJavaScriptString(IsGenericTitle(pending.Title) ? "" : pending.Title);
            var script = $$"""
                (() => {
                  const expectedConversationId = "{{conversationId}}";
                  const expectedTitle = "{{expectedTitle}}";
                  const normalize = (value) => String(value || "").replace(/\s+/g, " ").trim();
                  const visible = (el) => {
                    if (!el) return false;
                    const style = getComputedStyle(el);
                    return style.visibility !== "hidden" &&
                      style.display !== "none" &&
                      Number(style.opacity || "1") > 0.05 &&
                      !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
                  };
                  const topRight = (rect) => rect.width >= 120 &&
                    rect.width <= Math.min(560, innerWidth * 0.62) &&
                    rect.height >= 32 &&
                    rect.height <= 260 &&
                    rect.top >= 0 &&
                    rect.top <= Math.min(320, innerHeight * 0.42) &&
                    rect.left >= Math.max(innerWidth * 0.42, innerWidth - 760) &&
                    rect.right <= innerWidth + 16;
                  const linkHrefs = (node) => {
                    const links = node.matches?.('a[href*="/c/"]') ? [node] : [];
                    links.push(...node.querySelectorAll?.('a[href*="/c/"]') || []);
                    return [...new Set(links.map((link) => link.href || ""))];
                  };
                  const candidates = [...document.querySelectorAll('[role="status"],[role="alert"],[data-testid*="toast" i],[class*="toast" i],div,section,aside,li,button,a')]
                    .filter(visible)
                    .map((node) => {
                      const rect = node.getBoundingClientRect();
                      const rawText = node.innerText || node.textContent || "";
                      const text = normalize(rawText);
                      const hrefs = linkHrefs(node);
                      const hrefMatch = !!expectedConversationId && hrefs.some((href) => href.includes(`/c/${expectedConversationId}`));
                      const titleMatch = !!expectedTitle && text.includes(expectedTitle);
                      return {
                        text,
                        lines: rawText.split(/\n+/).map(normalize).filter(Boolean),
                        hrefMatch,
                        titleMatch,
                        width: rect.width,
                        height: rect.height,
                        top: rect.top,
                        left: rect.left
                      };
                    })
                    .filter((item) => item.text.length >= 4 && item.text.length <= 600)
                    .filter((item) => item.hrefMatch || item.titleMatch)
                    .filter((item) => topRight({
                      width: item.width,
                      height: item.height,
                      top: item.top,
                      left: item.left,
                      right: item.left + item.width
                    }))
                    .sort((a, b) => {
                      const aScore = (a.hrefMatch ? 0 : 10) + a.text.length / 1000 + a.width / 10000;
                      const bScore = (b.hrefMatch ? 0 : 10) + b.text.length / 1000 + b.width / 10000;
                      return aScore - bScore;
                    });
                  const item = candidates[0];
                  if (!item) {
                    return { found: false, title: "", body: "" };
                  }

                  let title = expectedTitle;
                  if (!title) {
                    title = item.lines.find((line) =>
                      line &&
                      !/^chatgpt$/i.test(line) &&
                      !/^chatgpt\s*(回复|任务)?完成$/i.test(line) &&
                      line.length <= 90) || "";
                  }

                  let body = item.text;
                  for (const token of ["ChatGPT", title]) {
                    if (token) {
                      body = body.replace(token, " ");
                    }
                  }
                  body = normalize(body);
                  if (!body && title) {
                    const index = item.lines.findIndex((line) => line === title);
                    body = normalize(item.lines.slice(index + 1).join(" "));
                  }

                  return {
                    found: true,
                    title: title || expectedTitle,
                    body: body || "后台会话已完成"
                  };
                })()
                """;
            var raw = await _webView.ExecuteScriptAsync(script);
            return InternalCompletionToastState.FromJson(raw);
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor internal toast scan failed: {ex.Message}");
            return InternalCompletionToastState.NotFound;
        }
    }

    private static void ApplyInternalToast(
        PendingChatGptTask pending,
        InternalCompletionToastState toast,
        ref string answer)
    {
        if (!IsGenericTitle(toast.Title))
        {
            pending.Title = toast.Title;
        }

        answer = Truncate(CollapseWhitespace(toast.Body), MaxNotificationBodyLength);
    }

    private async Task WaitForGeneratedTitleAsync(PendingChatGptTask pending)
    {
        if (!IsGenericTitle(pending.Title))
        {
            return;
        }

        var deadline = DateTimeOffset.Now.Add(TitleGenerationTimeout);
        while (DateTimeOffset.Now < deadline)
        {
            await CaptureConversationTitleAsync(pending);
            if (!IsGenericTitle(pending.Title))
            {
                return;
            }

            await Task.Delay(TitleGenerationPollInterval);
        }
    }

    private async Task<VisibleCompletionState> GetVisibleCompletionStateAsync(PendingChatGptTask pending)
    {
        try
        {
            if (_webView is null)
            {
                return VisibleCompletionState.NotVisible;
            }

            var conversationId = EscapeJavaScriptString(pending.ConversationId);
            var script = $$"""
                (() => {
                  const expectedConversationId = "{{conversationId}}";
                  const currentConversationId = location.pathname.match(/\/c\/([^/?#]+)/)?.[1] || "";
                  const isVisible = !expectedConversationId || currentConversationId === expectedConversationId;
                  const visible = (el) => !!el && !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
                  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
                  const hasStop = buttons.some((button) => /stop|停止|cancel|中止/i.test([
                    button.getAttribute('aria-label'),
                    button.innerText,
                    button.getAttribute('data-testid')
                  ].filter(Boolean).join(' ')));
                  const assistants = [...document.querySelectorAll('[data-message-author-role="assistant"], article')]
                    .map((item) => ({
                      role: item.getAttribute('data-message-author-role') || "",
                      text: (item.innerText || "").trim()
                    }))
                    .filter((item) => item.text && (!item.role || item.role === "assistant"));
                  return {
                    isVisible,
                    isGenerating: hasStop,
                    assistantText: isVisible ? (assistants.at(-1)?.text || "") : "",
                    currentConversationId
                  };
                })()
                """;
            var raw = await _webView.ExecuteScriptAsync(script);
            return VisibleCompletionState.FromJson(raw);
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor visible state failed: {ex.Message}");
            return VisibleCompletionState.NotVisible;
        }
    }

    private async Task CaptureConversationTitleAsync(PendingChatGptTask pending)
    {
        try
        {
            if (_webView is null)
            {
                return;
            }

            var conversationId = EscapeJavaScriptString(pending.ConversationId);
            var script = $$"""
                (() => {
                  const conversationId = "{{conversationId}}";
                  const sideTitle = conversationId
                    ? [...document.querySelectorAll('a[href^="/c/"], a[href*="/c/"]')]
                        .find((item) => item.href && item.href.includes(`/c/${conversationId}`))
                        ?.innerText?.trim()
                    : "";
                  const currentConversationId = location.pathname.match(/\/c\/([^/?#]+)/)?.[1] || "";
                  const pageTitle = document.title === "ChatGPT" ? "" : document.title;
                  const currentTitle = !conversationId || currentConversationId === conversationId ? pageTitle : "";
                  return (sideTitle || currentTitle || "ChatGPT 任务完成").slice(0, 120);
                })()
                """;
            var raw = await _webView.ExecuteScriptAsync(script);
            pending.Title = DecodeScriptString(raw);
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor title capture failed: {ex.Message}");
        }
    }

    private async Task<string> TryGetResponseBodyAsync(string requestId)
    {
        try
        {
            if (_webView is null)
            {
                return "";
            }

            var payload = JsonSerializer.Serialize(new { requestId });
            var raw = await _webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", payload);
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("body", out var body))
            {
                return "";
            }

            var text = body.GetString() ?? "";
            if (document.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean())
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }

            return text;
        }
        catch (Exception ex)
        {
            Log.App($"ChatGPT task monitor response body unavailable: {ex.Message}");
            return "";
        }
    }

    private bool NotificationsEnabled()
    {
        var config = _app.ConfigStore.Current;
        return config.CodexStyleEnhancementsEnabled && config.CodexMessageNotificationsEnabled;
    }

    private static bool IsConversationStreamRequest(string url, string method)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, "/backend-api/f/conversation", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractJsonStringFromAnywhere(string text, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var escapedPropertyName = Regex.Escape(propertyName);
        var match = Regex.Match(text, $"\"{escapedPropertyName}\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
        return match.Success ? DecodeJsonString(match.Groups[1].Value) : "";
    }

    private static string ExtractAssistantText(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var line in responseBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = trimmed[5..].Trim();
            if (json.Length == 0 || string.Equals(json, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryCollectAssistantText(json, parts);
        }

        if (parts.Count == 0)
        {
            TryCollectAssistantText(responseBody, parts);
        }

        return string.Join("", parts);
    }

    private static void TryCollectAssistantText(string json, List<string> parts)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            CollectAssistantText(document.RootElement, parts, pathHint: "");
        }
        catch
        {
            CollectRegexFallback(json, parts);
        }
    }

    private static void CollectAssistantText(JsonElement element, List<string> parts, string pathHint)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var nextPath = pathHint;
                if (element.TryGetProperty("p", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
                {
                    nextPath = pathElement.GetString() ?? pathHint;
                }

                if (element.TryGetProperty("message", out var message))
                {
                    CollectAssistantText(message, parts, nextPath);
                }

                if (element.TryGetProperty("content", out var content))
                {
                    CollectAssistantText(content, parts, nextPath);
                }

                if (element.TryGetProperty("parts", out var messageParts) && messageParts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in messageParts.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AddPart(parts, item.GetString());
                        }
                    }
                }

                if (nextPath.Contains("/message/content", StringComparison.OrdinalIgnoreCase) &&
                    element.TryGetProperty("v", out var valueElement) &&
                    valueElement.ValueKind == JsonValueKind.String)
                {
                    AddPart(parts, valueElement.GetString());
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "message" or "content" or "parts" or "v" or "p")
                    {
                        continue;
                    }

                    CollectAssistantText(property.Value, parts, nextPath);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAssistantText(item, parts, pathHint);
                }
                break;
        }
    }

    private static void CollectRegexFallback(string text, List<string> parts)
    {
        foreach (Match match in JsonValueRegex().Matches(text))
        {
            var decoded = DecodeJsonString(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                AddPart(parts, decoded);
            }
        }
    }

    private static void AddPart(List<string> parts, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.Contains("content_type", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("message_id", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        parts.Add(value);
    }

    private static string CollapseWhitespace(string text) =>
        WhitespaceRegex().Replace(text, " ").Trim();

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 1)].TrimEnd() + "…";
    }

    private static bool IsGenericTitle(string value)
    {
        var title = value.Trim();
        return string.IsNullOrWhiteSpace(title) ||
               string.Equals(title, "ChatGPT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(title, "ChatGPT 任务完成", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeScriptString(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch
        {
            return raw.Trim('"');
        }
    }

    private static string EscapeJavaScriptString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string DecodeJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{value}\"") ?? "";
        }
        catch
        {
            return value;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_requestReceiver is not null)
        {
            _requestReceiver.DevToolsProtocolEventReceived -= OnRequestWillBeSent;
        }

        if (_responseReceiver is not null)
        {
            _responseReceiver.DevToolsProtocolEventReceived -= OnResponseReceived;
        }

        if (_finishedReceiver is not null)
        {
            _finishedReceiver.DevToolsProtocolEventReceived -= OnLoadingFinished;
        }

        if (_failedReceiver is not null)
        {
            _failedReceiver.DevToolsProtocolEventReceived -= OnLoadingFailed;
        }

        _pending.Clear();
    }

    [GeneratedRegex("\"v\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.Compiled)]
    private static partial Regex JsonValueRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    private sealed class PendingChatGptTask
    {
        public PendingChatGptTask(string requestId, string conversationId, string parentMessageId, DateTimeOffset startedAt)
        {
            RequestId = requestId;
            ConversationId = conversationId;
            ParentMessageId = parentMessageId;
            StartedAt = startedAt;
        }

        public string RequestId { get; }
        public string ConversationId { get; set; }
        public string ParentMessageId { get; }
        public DateTimeOffset StartedAt { get; }
        public string Title { get; set; } = "ChatGPT 任务完成";
        public int StatusCode { get; set; }
    }

    private sealed record VisibleCompletionState(bool IsVisible, bool IsGenerating, string AssistantText)
    {
        public static VisibleCompletionState NotVisible { get; } = new(false, false, "");

        public static VisibleCompletionState FromJson(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var isVisible = root.TryGetProperty("isVisible", out var visible) && visible.GetBoolean();
                var isGenerating = root.TryGetProperty("isGenerating", out var generating) && generating.GetBoolean();
                var assistantText = root.TryGetProperty("assistantText", out var text)
                    ? text.GetString() ?? ""
                    : "";
                return new VisibleCompletionState(isVisible, isGenerating, assistantText);
            }
            catch
            {
                return NotVisible;
            }
        }
    }

    private sealed record InternalCompletionToastState(bool Found, string Title, string Body)
    {
        public static InternalCompletionToastState NotFound { get; } = new(false, "", "");

        public static InternalCompletionToastState FromJson(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var found = root.TryGetProperty("found", out var foundElement) && foundElement.GetBoolean();
                if (!found)
                {
                    return NotFound;
                }

                var title = root.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString() ?? ""
                    : "";
                var body = root.TryGetProperty("body", out var bodyElement)
                    ? bodyElement.GetString() ?? ""
                    : "";
                return new InternalCompletionToastState(true, title, body);
            }
            catch
            {
                return NotFound;
            }
        }
    }
}
