using System.Diagnostics;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed record EndpointTestResult(
    string Name,
    string Url,
    bool Ok,
    int StatusCode,
    long ElapsedMs,
    long Bytes,
    string Message);

internal sealed record SpeedTestRound(int Round, EndpointTestResult Local, EndpointTestResult Public);

internal sealed class NetworkTestService
{
    private readonly ManagerConfigStore _configStore;

    public NetworkTestService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<IReadOnlyList<SpeedTestRound>> RunAsync(
        int rounds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SpeedTestRound>();
        for (var i = 1; i <= rounds; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在测速第 {i}/{rounds} 轮...");

            var config = _configStore.Reload();
            var local = await TestEndpointAsync("本地", config.LocalHealthUrl, cancellationToken);
            var pub = await TestEndpointAsync("公网", config.PublicHealthUrl, cancellationToken);
            results.Add(new SpeedTestRound(i, local, pub));
            await Task.Delay(250, cancellationToken);
        }

        return results;
    }

    private static async Task<EndpointTestResult> TestEndpointAsync(string name, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new EndpointTestResult(name, url, false, 0, 0, 0, "地址为空");
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            sw.Stop();
            var message = response.IsSuccessStatusCode ? "OK" : response.ReasonPhrase ?? "HTTP 错误";
            return new EndpointTestResult(name, url, response.IsSuccessStatusCode, (int)response.StatusCode, sw.ElapsedMilliseconds, body.LongLength, message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new EndpointTestResult(name, url, false, 0, sw.ElapsedMilliseconds, 0, ex.Message);
        }
    }
}
