using DevSpaceManager.Core;
using DevSpaceManager.Services;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DevSpaceManager.UI;

public sealed partial class SettingsWindow
{
    private readonly AdminBridgeService _bridge;
    private bool _allowClose;

    internal SettingsWindow(AppHost app, Func<Task>? reloadChatGptView)
    {
        _bridge = new AdminBridgeService(app, reloadChatGptView);
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowDwmAttributes();
        Loaded += async (_, _) => await InitializeAdminViewAsync();
        Closing += (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            Hide();
        };
    }

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private async Task InitializeAdminViewAsync()
    {
        if (AdminView.CoreWebView2 is not null) return;
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.AppDataDirectory, "admin-webview"));
        await AdminView.EnsureCoreWebView2Async(env);
        var core = AdminView.CoreWebView2 ?? throw new InvalidOperationException("Admin WebView2 初始化失败。");
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.WebMessageReceived += async (_, args) =>
        {
            var response = await _bridge.HandleAsync(args.WebMessageAsJson);
            core.PostWebMessageAsJson(response);
        };

        core.SetVirtualHostNameToFolderMapping(
            "admin.devspace.local",
            ResolveAdminDistPath(),
            CoreWebView2HostResourceAccessKind.DenyCors);
        AdminView.Source = new Uri("https://admin.devspace.local/index.html");
    }

    private static string ResolveAdminDistPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "AdminApp", "dist"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "AdminApp", "dist")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "DevSpaceManager", "AdminApp", "dist"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }

        throw new FileNotFoundException("未找到 AdminApp 静态资源。", candidates[0]);
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        DragMove();
    }

    private void CloseButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Hide();

    private void ApplyWindowDwmAttributes()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));

        var borderColor = 0x00BABABF; // COLORREF BGR for #BFBFBA
        _ = DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));

        var ncrpEnabled = 2; // DWMNCRP_ENABLED
        _ = DwmSetWindowAttribute(handle, 2, ref ncrpEnabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
