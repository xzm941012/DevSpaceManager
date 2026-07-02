using DevSpaceManager.Services;

namespace DevSpaceManager.Core;

internal sealed class AppHost : IDisposable
{
    public ManagerConfigStore ConfigStore { get; }
    public ManagedProcessService Processes { get; }
    public McpRequestMonitor RequestMonitor { get; }
    public MountedMcpService MountedMcps { get; }
    public SshProfileService SshProfiles { get; }
    public McpProxyService McpProxy { get; }
    public PublicEndpointSyncService PublicEndpoints { get; }
    public HealthService Health { get; }
    public NetworkTestService NetworkTests { get; }
    public AuthSecretService AuthSecrets { get; }
    public EnvironmentService Environment { get; }
    public UpdateService Updates { get; }
    public SchedulerService Scheduler { get; }
    public WorkerService Worker { get; }
    public event EventHandler<NativeNotification>? NativeNotificationRequested;

    private AppHost()
    {
        ConfigStore = new ManagerConfigStore();
        RequestMonitor = new McpRequestMonitor();
        MountedMcps = new MountedMcpService(ConfigStore);
        SshProfiles = new SshProfileService(ConfigStore);
        McpProxy = new McpProxyService(ConfigStore, RequestMonitor, MountedMcps);
        PublicEndpoints = new PublicEndpointSyncService(ConfigStore);
        Health = new HealthService(ConfigStore);
        Processes = new ManagedProcessService(ConfigStore, McpProxy, PublicEndpoints, Health);
        NetworkTests = new NetworkTestService(ConfigStore);
        AuthSecrets = new AuthSecretService();
        Environment = new EnvironmentService(ConfigStore);
        Updates = new UpdateService(ConfigStore, Processes);
        Scheduler = new SchedulerService(ConfigStore);
        Worker = new WorkerService(ConfigStore, Processes, Health, Updates);
    }

    public static AppHost Create() => new();

    public void RequestNativeNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        NativeNotificationRequested?.Invoke(this, new NativeNotification(title, message, icon));
    }

    public void Dispose()
    {
        Processes.Dispose();
        McpProxy.Dispose();
        MountedMcps.Dispose();
    }
}

internal sealed record NativeNotification(string Title, string Message, ToolTipIcon Icon);
