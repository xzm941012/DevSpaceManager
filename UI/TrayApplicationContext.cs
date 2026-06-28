using DevSpaceManager.Core;
using DevSpaceManager.Services;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DevSpaceManager.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppHost _app;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly Control _dispatcher = new();
    private readonly CancellationTokenSource _backgroundWorkerCts = new();
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
        _ = Task.Run(() => _app.Worker.RunAsync(_backgroundWorkerCts.Token), _backgroundWorkerCts.Token);
        _ = RefreshStatusAsync();
        _ = CheckUpdatesInBackgroundAsync();
        _ = InitializeServicesAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = CreateTrayMenu();
        AddTrayMenuItem(menu, "启动全部", async (_, _) => await RunProcessActionAsync("启动全部", token => _app.Processes.StartAllAsync(token)));
        AddTrayMenuItem(menu, "停止全部", async (_, _) => await RunProcessActionAsync("停止全部", _app.Processes.StopAll));
        AddTrayMenuItem(menu, "重启全部", async (_, _) => await RunProcessActionAsync("重启全部", token => _app.Processes.RestartAllAsync(token)));
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "启动 DevSpace", async (_, _) => await RunProcessActionAsync("启动 DevSpace", () => _app.Processes.Start(ProcessRole.DevSpace)));
        AddTrayMenuItem(menu, "重启 DevSpace", async (_, _) => await RunProcessActionAsync("重启 DevSpace", () => _app.Processes.Restart(ProcessRole.DevSpace)));
        AddTrayMenuItem(menu, "启动隧道", async (_, _) => await RunProcessActionAsync("启动隧道", token => _app.Processes.StartAsync(ProcessRole.CloudflareTunnel, token)));
        AddTrayMenuItem(menu, "重启隧道", async (_, _) => await RunProcessActionAsync("重启隧道", token => _app.Processes.RestartAsync(ProcessRole.CloudflareTunnel, token)));
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "检查更新", async (_, _) => await CheckUpdatesAsync());
        AddTrayMenuItem(menu, "打开日志", (_, _) => OpenFolder(AppPaths.LogDirectory));
        AddTrayMenuItem(menu, "设置", (_, _) => ShowSettings());
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "退出", (_, _) =>
        {
            _exiting = true;
            ExitThread();
        }, danger: true);
        return menu;
    }

    private static ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            AccessibleName = "DevSpace 托盘菜单",
            AutoSize = true,
            BackColor = TrayMenuColors.Surface,
            DropShadowEnabled = true,
            ForeColor = TrayMenuColors.Text,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(6),
            Renderer = new TrayMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false
        };
        menu.Opened += (_, _) => ApplyRoundedMenuRegion(menu);
        menu.SizeChanged += (_, _) => ApplyRoundedMenuRegion(menu);
        menu.Closed += (_, _) =>
        {
            var oldRegion = menu.Region;
            menu.Region = null;
            oldRegion?.Dispose();
        };
        return menu;
    }

    private static ToolStripMenuItem AddTrayMenuItem(
        ContextMenuStrip menu,
        string text,
        EventHandler onClick,
        bool danger = false)
    {
        var item = new ToolStripMenuItem(text, null, onClick)
        {
            AutoSize = false,
            BackColor = TrayMenuColors.Surface,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = danger ? TrayMenuColors.DangerText : TrayMenuColors.Text,
            Margin = Padding.Empty,
            Padding = new Padding(12, 0, 12, 0),
            Size = new Size(176, 32),
            TextAlign = ContentAlignment.MiddleLeft
        };
        item.AccessibleName = text;
        menu.Items.Add(item);
        return item;
    }

    private static void AddTraySeparator(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripSeparator
        {
            AutoSize = false,
            Margin = new Padding(6, 4, 6, 4),
            Size = new Size(164, 1)
        });
    }

    private static void ApplyRoundedMenuRegion(ToolStrip menu)
    {
        if (!menu.IsHandleCreated || menu.Width <= 0 || menu.Height <= 0)
        {
            return;
        }

        var regionHandle = NativeMethods.CreateRoundRectRgn(
            0,
            0,
            menu.Width + 1,
            menu.Height + 1,
            14,
            14);
        if (regionHandle == IntPtr.Zero)
        {
            return;
        }

        var oldRegion = menu.Region;
        menu.Region = Region.FromHrgn(regionHandle);
        oldRegion?.Dispose();
        NativeMethods.DeleteObject(regionHandle);
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
        await RunProcessActionAsync(actionName, _ =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private async Task RunProcessActionAsync(string actionName, Func<CancellationToken, Task> action)
    {
        try
        {
            _tray.Text = $"DevSpace 管理器 - 正在{actionName}";
            await action(CancellationToken.None);
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
        catch (Exception ex)
        {
            Log.App($"InitializeServices failed: {ex}");
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
            _backgroundWorkerCts.Cancel();
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
            _backgroundWorkerCts.Dispose();
            _app.Dispose();
        }
        base.Dispose(disposing);
    }

    private static class TrayMenuColors
    {
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color Border = Color.FromArgb(224, 224, 220);
        public static readonly Color Divider = Color.FromArgb(232, 232, 228);
        public static readonly Color Hover = Color.FromArgb(244, 244, 241);
        public static readonly Color Text = Color.FromArgb(31, 31, 28);
        public static readonly Color MutedText = Color.FromArgb(111, 111, 104);
        public static readonly Color DangerText = Color.FromArgb(177, 40, 40);
        public static readonly Color DangerHover = Color.FromArgb(255, 243, 242);
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(TrayMenuColors.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = RoundedRectangle(bounds, 7);
            using var pen = new Pen(TrayMenuColors.Border);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item || (!item.Selected && !item.Pressed))
            {
                return;
            }

            var bounds = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            var hoverColor = item.ForeColor == TrayMenuColors.DangerText
                ? TrayMenuColors.DangerHover
                : TrayMenuColors.Hover;
            using var brush = new SolidBrush(hoverColor);
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(TrayMenuColors.Divider);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var textColor = e.Item.Enabled
                ? e.Item.ForeColor
                : TrayMenuColors.MutedText;
            var textBounds = new Rectangle(14, 0, e.Item.Width - 28, e.Item.Height);
            TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                textBounds,
                textColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private static class NativeMethods
    {
        public const int SwRestore = 9;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(
            int left,
            int top,
            int right,
            int bottom,
            int widthEllipse,
            int heightEllipse);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr objectHandle);
    }
}
