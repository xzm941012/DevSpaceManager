using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class WorkerService
{
    private const int PublicHealthFailuresBeforeTunnelRestart = 3;
    private static readonly TimeSpan HealthFailureConfirmDelay = TimeSpan.FromMilliseconds(1200);

    private readonly ManagerConfigStore _configStore;
    private readonly ManagedProcessService _processes;
    private readonly HealthService _health;
    private readonly UpdateService _updates;
    private bool _devspaceStarted;
    private bool _tunnelStarted;
    private int _publicHealthFailures;

    public WorkerService(
        ManagerConfigStore configStore,
        ManagedProcessService processes,
        HealthService health,
        UpdateService updates)
    {
        _configStore = configStore;
        _processes = processes;
        _health = health;
        _updates = updates;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Log.Worker("Worker started.");
        var lastUpdateCheck = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var config = _configStore.Reload();
                var startedDevSpaceThisCycle = false;
                if (config.AutoStartDevSpace &&
                    !_processes.IsRunning(ProcessRole.DevSpace) &&
                    (config.AutoRestart || !_devspaceStarted))
                {
                    Log.Worker("DevSpace process is not running. Starting DevSpace.");
                    _processes.Start(ProcessRole.DevSpace);
                    _devspaceStarted = true;
                    startedDevSpaceThisCycle = true;
                }

                if (config.AutoStartTunnel &&
                    !_processes.IsRunning(ProcessRole.CloudflareTunnel) &&
                    (config.AutoRestart || !_tunnelStarted))
                {
                    Log.Worker("Cloudflare Tunnel process is not running. Starting Cloudflare Tunnel.");
                    await _processes.StartAsync(ProcessRole.CloudflareTunnel, cancellationToken);
                    _tunnelStarted = true;
                }

                if (config.CheckUpdates &&
                    DateTimeOffset.Now - lastUpdateCheck > TimeSpan.FromHours(config.UpdateCheckHours))
                {
                    lastUpdateCheck = DateTimeOffset.Now;
                    var update = await _updates.CheckDevSpaceAsync(cancellationToken);
                    if (update.HasUpdate)
                    {
                        Log.Update(update.Notes);
                    }
                }

                var local = startedDevSpaceThisCycle
                    ? await WaitForLocalHealthAsync(cancellationToken)
                    : await _health.CheckLocalAsync(cancellationToken);
                var pub = await _health.CheckPublicAsync(cancellationToken);
                config = _configStore.Reload();
                if (config.AutoStartDevSpace &&
                    config.AutoRestart &&
                    !local.Ok)
                {
                    await Task.Delay(HealthFailureConfirmDelay, cancellationToken);
                    local = await _health.CheckLocalAsync(cancellationToken);
                    if (local.Ok)
                    {
                        Log.Worker("Local DevSpace health check recovered before restart.");
                        ResetPublicHealthFailures();
                        pub = await _health.CheckPublicAsync(cancellationToken);
                    }
                }

                if (config.AutoStartDevSpace &&
                    config.AutoRestart &&
                    !local.Ok)
                {
                    ResetPublicHealthFailures();
                    Log.Worker($"Local DevSpace health check failed. Restarting DevSpace: {local.Message}");
                    _processes.Restart(ProcessRole.DevSpace);
                    _devspaceStarted = true;
                    local = await WaitForLocalHealthAsync(cancellationToken);
                    if (local.Ok)
                    {
                        pub = await _health.CheckPublicAsync(cancellationToken);
                    }
                }

                if (local.Ok && pub.Ok)
                {
                    ResetPublicHealthFailures();
                }
                else if (!local.Ok)
                {
                    ResetPublicHealthFailures();
                }

                if (config.AutoStartTunnel &&
                    config.AutoRestart &&
                    !config.UseTemporaryCloudflareTunnel &&
                    local.Ok &&
                    !pub.Ok &&
                    !config.TemporaryPublicBaseUrlPending)
                {
                    _publicHealthFailures++;
                    if (_publicHealthFailures < PublicHealthFailuresBeforeTunnelRestart)
                    {
                        Log.Worker($"Public health check failed while local DevSpace is healthy ({_publicHealthFailures}/{PublicHealthFailuresBeforeTunnelRestart}). Waiting before tunnel restart: {pub.Message}");
                    }
                    else
                    {
                        Log.Worker($"Public health check failed while local DevSpace is healthy ({_publicHealthFailures}/{PublicHealthFailuresBeforeTunnelRestart}). Restarting Cloudflare Tunnel: {pub.Message}");
                        await _processes.RestartAsync(ProcessRole.CloudflareTunnel, cancellationToken);
                        _tunnelStarted = true;
                        ResetPublicHealthFailures();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Worker(ex.ToString());
            }

            var current = _configStore.Current;
            var configuredDelay = Math.Max(10, current.HealthCheckSeconds);
            var delay = current.AutoRestart ? Math.Min(10, configuredDelay) : configuredDelay;
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
    }

    private async Task<(bool Ok, string Message)> WaitForLocalHealthAsync(CancellationToken cancellationToken)
    {
        (bool Ok, string Message) last = (false, "尚未检测。");
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(attempt == 1 ? 700 : 1200, cancellationToken);
            last = await _health.CheckLocalAsync(cancellationToken);
            if (last.Ok)
            {
                return last;
            }
        }

        return last;
    }

    private void ResetPublicHealthFailures()
    {
        _publicHealthFailures = 0;
    }
}
