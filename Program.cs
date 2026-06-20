using DevSpaceManager.Core;
using DevSpaceManager.Services;
using DevSpaceManager.UI;

namespace DevSpaceManager;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--worker", StringComparison.OrdinalIgnoreCase)))
        {
            using var workerApp = AppHost.Create();
            await workerApp.Worker.RunAsync();
            return 0;
        }

        using var singleInstance = SingleInstanceCoordinator.Create();
        if (!singleInstance.IsFirstInstance)
        {
            await SingleInstanceCoordinator.SignalExistingAsync();
            return 0;
        }

        using var app = AppHost.Create();
        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(app);
        singleInstance.Start(context.RequestShowSettings);
        Application.Run(context);
        return 0;
    }
}
