using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class HealthService
{
    private readonly ManagerConfigStore _configStore;
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public HealthService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<(bool Ok, string Message)> CheckLocalAsync(CancellationToken cancellationToken = default) =>
        await CheckAsync(_configStore.Current.LocalHealthUrl, cancellationToken);

    public async Task<(bool Ok, string Message)> CheckPublicAsync(CancellationToken cancellationToken = default) =>
        _configStore.Current is { UseTemporaryCloudflareTunnel: true, TemporaryPublicBaseUrlPending: true }
            ? (false, "临时公网地址正在刷新。")
            : await CheckAsync(_configStore.Current.PublicHealthUrl, cancellationToken);

    private async Task<(bool Ok, string Message)> CheckAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "地址为空。");

        try
        {
            using var response = await _client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode
                ? (true, body.Trim())
                : (false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, FullExceptionMessage(ex));
        }
    }

    private static string FullExceptionMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.OrdinalIgnoreCase))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages);
    }
}
