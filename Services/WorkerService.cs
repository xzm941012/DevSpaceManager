using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class WorkerService
{
    private readonly ManagerConfigStore _configStore;
    private readonly ManagedProcessService _processes;
    private readonly HealthService _health;
    private readonly UpdateService _updates;
    private bool _devspaceStarted;
    private bool _tunnelStarted;

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
                if (config.AutoStartDevSpace &&
                    !_processes.IsRunning(ProcessRole.DevSpace) &&
                    (config.AutoRestart || !_devspaceStarted))
                {
                    _processes.Start(ProcessRole.DevSpace);
                    _devspaceStarted = true;
                }

                if (config.AutoStartTunnel &&
                    !_processes.IsRunning(ProcessRole.CloudflareTunnel) &&
                    (config.AutoRestart || !_tunnelStarted))
                {
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

                var local = await _health.CheckLocalAsync(cancellationToken);
                var pub = await _health.CheckPublicAsync(cancellationToken);
                config = _configStore.Reload();
                if (config.AutoStartTunnel &&
                    config.AutoRestart &&
                    !config.UseTemporaryCloudflareTunnel &&
                    local.Ok &&
                    !pub.Ok &&
                    !config.TemporaryPublicBaseUrlPending)
                {
                    Log.Worker($"Public health check failed while local DevSpace is healthy. Restarting Cloudflare Tunnel: {pub.Message}");
                    await _processes.RestartAsync(ProcessRole.CloudflareTunnel, cancellationToken);
                    _tunnelStarted = true;
                }
            }
            catch (Exception ex)
            {
                Log.Worker(ex.ToString());
            }

            var delay = Math.Max(10, _configStore.Current.HealthCheckSeconds);
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
    }
}
