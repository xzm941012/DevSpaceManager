using DevSpaceManager.Services;

namespace DevSpaceManager.Core;

internal sealed class AppHost : IDisposable
{
    public ManagerConfigStore ConfigStore { get; }
    public ManagedProcessService Processes { get; }
    public HealthService Health { get; }
    public NetworkTestService NetworkTests { get; }
    public AuthSecretService AuthSecrets { get; }
    public EnvironmentService Environment { get; }
    public UpdateService Updates { get; }
    public SchedulerService Scheduler { get; }
    public WorkerService Worker { get; }

    private AppHost()
    {
        ConfigStore = new ManagerConfigStore();
        Processes = new ManagedProcessService(ConfigStore);
        Health = new HealthService(ConfigStore);
        NetworkTests = new NetworkTestService(ConfigStore);
        AuthSecrets = new AuthSecretService();
        Environment = new EnvironmentService(ConfigStore);
        Updates = new UpdateService(ConfigStore, Processes);
        Scheduler = new SchedulerService(ConfigStore);
        Worker = new WorkerService(ConfigStore, Processes, Health, Updates);
    }

    public static AppHost Create() => new();

    public void Dispose()
    {
        Processes.Dispose();
    }
}
