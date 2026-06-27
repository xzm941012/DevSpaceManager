using DevSpaceManager.Core;
using DevSpaceManager.Services;
using System.Runtime.InteropServices;

namespace DevSpaceManager.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppHost _app;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly Control _dispatcher = new();
    private MainWindow? _mainWindow;
    private bool _refreshing;
    private bool _healthy;
    private bool _exiting;
    private DateTimeOffset _lastBackgroundUpdateCheck = DateTimeOffset.MinValue;

    public TrayApplicationContext(AppHost app)
    {
        _app = app;
        _dispatcher.CreateControl();
        _tray = new NotifyIcon
        {
            Icon = NotifyIconFactory.Create(false),
            Text = "DevSpace 管理器",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += async (_, _) =>
        {
            await RefreshStatusAsync();
            await CheckUpdatesInBackgroundAsync();
        };
        _timer.Start();

        _startupTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer.Stop();
            ShowSettings();
            _ = StartBackgroundServicesAsync();
        };
        _startupTimer.Start();
    }

    private async Task StartBackgroundServicesAsync()
    {
        await Task.Delay(1000);
        _ = Task.Run(() => _app.McpProxy.EnsureState());
        _ = RefreshStatusAsync();
        _ = CheckUpdatesInBackgroundAsync();
        _ = InitializeServicesAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("启动全部", null, async (_, _) => await RunProcessActionAsync("启动全部", _app.Processes.StartAll));
        menu.Items.Add("停止全部", null, async (_, _) => await RunProcessActionAsync("停止全部", _app.Processes.StopAll));
        menu.Items.Add("重启全部", null, async (_, _) => await RunProcessActionAsync("重启全部", _app.Processes.RestartAll));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("启动 DevSpace", null, async (_, _) => await RunProcessActionAsync("启动 DevSpace", () => _app.Processes.Start(ProcessRole.DevSpace)));
        menu.Items.Add("重启 DevSpace", null, async (_, _) => await RunProcessActionAsync("重启 DevSpace", () => _app.Processes.Restart(ProcessRole.DevSpace)));
        menu.Items.Add("启动隧道", null, async (_, _) => await RunProcessActionAsync("启动隧道", () => _app.Processes.Start(ProcessRole.CloudflareTunnel)));
        menu.Items.Add("重启隧道", null, async (_, _) => await RunProcessActionAsync("重启隧道", () => _app.Processes.Restart(ProcessRole.CloudflareTunnel)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("检查更新", null, async (_, _) => await CheckUpdatesAsync());
        menu.Items.Add("打开日志", null, (_, _) => OpenFolder(AppPaths.LogDirectory));
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _exiting = true;
            ExitThread();
        });
        return menu;
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devspaceRunning = _app.Processes.IsRunning(ProcessRole.DevSpace);
            var tunnelRunning = _app.Processes.IsRunning(ProcessRole.CloudflareTunnel);
            var local = await _app.Health.CheckLocalAsync();
            var pub = await _app.Health.CheckPublicAsync();
            devspaceRunning = devspaceRunning || local.Ok;
            tunnelRunning = tunnelRunning || pub.Ok;
            var ok = devspaceRunning && tunnelRunning && local.Ok && pub.Ok;
            _tray.Text = ok
                ? "DevSpace 管理器 - 运行正常"
                : $"DevSpace 管理器 - DevSpace:{State(devspaceRunning)} 隧道:{State(tunnelRunning)}";
            if (_healthy != ok)
            {
                _healthy = ok;
                var oldIcon = _tray.Icon;
                _tray.Icon = NotifyIconFactory.Create(ok);
                oldIcon?.Dispose();
            }
        }
        catch
        {
            _healthy = false;
            var oldIcon = _tray.Icon;
            _tray.Icon = NotifyIconFactory.Create(false);
            oldIcon?.Dispose();
            _tray.Text = "DevSpace 管理器 - 状态检查失败";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var update = await _app.Updates.CheckDevSpaceAsync();
            if (!update.HasUpdate)
            {
                MessageBox.Show(update.Notes, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            var result = MessageBox.Show(
                $"{update.Notes}{Environment.NewLine}{Environment.NewLine}现在更新吗？",
                "发现 DevSpace 新版本",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes) return;

            var progress = new Progress<string>(message => Log.Update(message));
            await _app.Updates.UpdateDevSpaceAsync(progress);
            var config = _app.ConfigStore.Reload();
            config.LastNotifiedUpdateVersion = update.LatestVersion;
            _app.ConfigStore.Save(config);
            var restart = MessageBox.Show(
                "更新完成。现在重启 DevSpace 吗？",
                "DevSpace 管理器",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (restart == DialogResult.Yes)
            {
                _app.Processes.Start(ProcessRole.DevSpace);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        try
        {
            var config = _app.ConfigStore.Reload();
            if (!config.CheckUpdates) return;
            if (DateTimeOffset.Now - _lastBackgroundUpdateCheck < TimeSpan.FromHours(config.UpdateCheckHours)) return;

            _lastBackgroundUpdateCheck = DateTimeOffset.Now;
            var update = await _app.Updates.CheckDevSpaceAsync();
            if (!update.HasUpdate) return;
            if (string.Equals(config.LastNotifiedUpdateVersion, update.LatestVersion, StringComparison.OrdinalIgnoreCase)) return;

            config.LastNotifiedUpdateVersion = update.LatestVersion;
            _app.ConfigStore.Save(config);
            _tray.ShowBalloonTip(
                5000,
                "发现 DevSpace 新版本",
                $"{update.CurrentVersion} -> {update.LatestVersion}{Environment.NewLine}可在托盘菜单或设置页执行更新。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Update($"后台检查更新失败：{ex.Message}");
        }
    }

    private async Task RunProcessActionAsync(string actionName, Action action)
    {
        try
        {
            _tray.Text = $"DevSpace 管理器 - 正在{actionName}";
            action();
            await Task.Delay(1200);
            await RefreshStatusAsync();
            _tray.ShowBalloonTip(2500, "DevSpace 管理器", $"{actionName}已执行。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "操作失败", ex.Message, ToolTipIcon.Error);
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            var checks = await _app.Environment.CheckAsync();
            if (checks.All(check => check.Ok))
            {
                _app.Processes.StartAll();
                await RefreshStatusAsync();
                return;
            }

            _tray.ShowBalloonTip(
                5000,
                "需要初始化",
                "缺少部分运行环境或配置，请先完成设置。",
                ToolTipIcon.Warning);
            ShowSettings();
        }
        catch
        {
            // Settings remains available from the tray even if detection fails.
        }
    }

    public void RequestShowSettings()
    {
        if (_dispatcher.IsDisposed) return;
        if (_dispatcher.InvokeRequired)
        {
            _dispatcher.BeginInvoke(ShowSettings);
            return;
        }

        ShowSettings();
    }

    private void ShowSettings()
    {
        try
        {
            Log.App("ShowSettings requested.");
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow(_app);
                _mainWindow.Closing += (_, args) =>
                {
                    if (!_exiting)
                    {
                        args.Cancel = true;
                        _mainWindow.Hide();
                    }
                };
                _mainWindow.Closed += (_, _) => _mainWindow = null;
                Log.App("Main window created.");
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
                Log.App("Main window shown.");
            }

            if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _mainWindow.WindowState = System.Windows.WindowState.Normal;
            }

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
            Log.App($"Main window activated. Visible={_mainWindow.IsVisible}, WindowState={_mainWindow.WindowState}.");
        }
        catch (Exception ex)
        {
            Log.App(ex.ToString());
            MessageBox.Show(ex.Message, "打开设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RunSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string State(bool running) => running ? "已运行" : "未运行";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            if (_mainWindow is not null)
            {
                _mainWindow.Close();
                _mainWindow = null;
            }
            _timer.Dispose();
            _startupTimer.Dispose();
            _dispatcher.Dispose();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _app.Dispose();
        }
        base.Dispose(disposing);
    }

    private static class NativeMethods
    {
        public const int SwRestore = 9;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
