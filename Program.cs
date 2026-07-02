using DevSpaceManager.Core;
using DevSpaceManager.Services;
using DevSpaceManager.UI;
using System.Windows;

namespace DevSpaceManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--ssh-mcp", StringComparison.OrdinalIgnoreCase)))
        {
            var configStore = new ManagerConfigStore();
            using var server = new SshMcpServer(configStore);
            server.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        if (args.Any(arg => string.Equals(arg, "--worker", StringComparison.OrdinalIgnoreCase)))
        {
            using var workerApp = AppHost.Create();
            workerApp.Worker.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        using var singleInstance = SingleInstanceCoordinator.Create();
        if (!singleInstance.IsFirstInstance)
        {
            SingleInstanceCoordinator.SignalExistingAsync().GetAwaiter().GetResult();
            return 0;
        }

        using var app = AppHost.Create();
        _ = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        var startMinimized = args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)) ||
                             app.ConfigStore.Current.StartMinimizedToTray;
        using var context = new TrayApplicationContext(app, startMinimized);
        singleInstance.Start(context.RequestShowSettings);
        System.Windows.Forms.Application.Run(context);
        System.Windows.Application.Current.Shutdown();
        return 0;
    }
}
