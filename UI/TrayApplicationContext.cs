using DevSpaceManager.Core;
using DevSpaceManager.Services;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DevSpaceManager.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int TrayMenuWidth = 206;
    private const int TraySubmenuWidth = 254;
    private const int TrayMenuItemHeight = 32;

    private readonly AppHost _app;
    private readonly bool _startMinimized;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly Control _dispatcher = new();
    private readonly CancellationTokenSource _backgroundWorkerCts = new();
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private EnvironmentSetupWindow? _environmentSetupWindow;
    private UpdateWindow? _updateWindow;
    private ToolStripMenuItem? _devSpaceStatusItem;
    private ToolStripMenuItem? _tunnelStatusItem;
    private bool _refreshing;
    private bool _healthy;
    private bool _exiting;
    private bool _disposed;
    private bool _closingForLightweightMode;
    private bool _hasAvailableUpdate;
    private string _availableUpdateMessage = "";
    private DateTimeOffset _lastBackgroundUpdateCheck = DateTimeOffset.MinValue;

    public TrayApplicationContext(AppHost app, bool startMinimized)
    {
        _app = app;
        _startMinimized = startMinimized;
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
                ShowMainWindow();
            }
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();
        _app.NativeNotificationRequested += OnNativeNotificationRequested;

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
            if (!_startMinimized)
            {
                ShowMainWindow();
            }
            _ = StartBackgroundServicesAsync();
        };
        _startupTimer.Start();
    }

    private void OnNativeNotificationRequested(object? sender, NativeNotification notification)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!ShouldShowNativeNotification())
            {
                return;
            }

            _tray.ShowBalloonTip(5000, notification.Title, notification.Message, notification.Icon);
        });
    }

    private bool ShouldShowNativeNotification()
    {
        if (_mainWindow is null)
        {
            return false;
        }

        return !_mainWindow.IsVisible ||
               _mainWindow.WindowState == System.Windows.WindowState.Minimized;
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
        AddTrayMenuItem(menu, "打开 ChatGPT", (_, _) => ShowMainWindow());
        AddTrayMenuItem(menu, "轻量模式", (_, _) => EnterLightweightMode());
        AddTraySeparator(menu);
        AddServiceSubmenu(menu, "DevSpace", ProcessRole.DevSpace);
        AddServiceSubmenu(menu, "Cloudflare Tunnel", ProcessRole.CloudflareTunnel);
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "启动全部", async (_, _) => await RunProcessActionAsync("启动全部", token => _app.Processes.StartAllAsync(token)));
        AddTrayMenuItem(menu, "停止全部", async (_, _) => await RunProcessActionAsync("停止全部", _app.Processes.StopAll));
        AddTrayMenuItem(menu, "重启全部", async (_, _) => await RunProcessActionAsync("重启全部", token => _app.Processes.RestartAllAsync(token)));
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "检查更新", (_, _) => ShowUpdateWindow());
        AddTrayMenuItem(menu, "打开日志", (_, _) => OpenFolder(AppPaths.LogDirectory));
        AddTrayMenuItem(menu, "设置", (_, _) => ShowSettingsWindow());
        AddTrayMenuItem(menu, "环境诊断", (_, _) => ShowEnvironmentSetupWindow(orderedMode: false));
        AddTraySeparator(menu);
        AddTrayMenuItem(menu, "退出", (_, _) =>
        {
            _exiting = true;
            ExitThread();
        }, danger: true);
        return menu;
    }

    private void AddServiceSubmenu(ContextMenuStrip menu, string text, ProcessRole role)
    {
        var submenu = AddTrayMenuItem(menu, text, (_, _) => { });
        ConfigureTrayDropDown(submenu, TraySubmenuWidth);
        var status = AddTraySubmenuItem(submenu, "当前状态：检测中", (_, _) => { });
        status.Enabled = false;
        AddTraySubmenuSeparator(submenu);
        AddTraySubmenuItem(submenu, "启动", async (_, _) => await RunProcessActionAsync($"启动{text}", token => StartRoleAsync(role, token)));
        AddTraySubmenuItem(submenu, "停止", async (_, _) => await RunProcessActionAsync($"停止{text}", () => _app.Processes.Stop(role)));
        AddTraySubmenuItem(submenu, "重启", async (_, _) => await RunProcessActionAsync($"重启{text}", token => RestartRoleAsync(role, token)));

        if (role == ProcessRole.DevSpace)
        {
            _devSpaceStatusItem = status;
        }
        else
        {
            _tunnelStatusItem = status;
        }
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
        menu.MinimumSize = new Size(TrayMenuWidth + menu.Padding.Horizontal, 0);
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

    private static void ConfigureTrayDropDown(ToolStripMenuItem item, int itemWidth)
    {
        var dropDown = new ContextMenuStrip
        {
            AutoSize = true,
            BackColor = TrayMenuColors.Surface,
            DropShadowEnabled = true,
            Font = item.Owner?.Font ?? new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TrayMenuColors.Text,
            Padding = new Padding(6),
            Renderer = new TrayMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false
        };
        dropDown.MinimumSize = new Size(itemWidth + dropDown.Padding.Horizontal, 0);
        dropDown.Opened += (_, _) => ApplyRoundedMenuRegion(dropDown);
        dropDown.SizeChanged += (_, _) => ApplyRoundedMenuRegion(dropDown);
        dropDown.Closed += (_, _) =>
        {
            var oldRegion = dropDown.Region;
            dropDown.Region = null;
            oldRegion?.Dispose();
        };
        item.DropDown = dropDown;
        item.DropDownOpening += (_, _) =>
        {
            AlignDropDownToOwnerRightEdge(item);
        };
    }

    private static void AlignDropDownToOwnerRightEdge(ToolStripMenuItem item)
    {
        if (item.Owner is not ToolStrip owner || !owner.Visible)
        {
            return;
        }

        var location = owner.PointToScreen(new Point(owner.Width - 1, item.Bounds.Top));
        item.DropDown.Location = location;
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
            Size = new Size(TrayMenuWidth, TrayMenuItemHeight),
            TextAlign = ContentAlignment.MiddleLeft
        };
        item.AccessibleName = text;
        menu.Items.Add(item);
        return item;
    }

    private static ToolStripMenuItem AddTraySubmenuItem(
        ToolStripMenuItem parent,
        string text,
        EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick)
        {
            AutoSize = false,
            BackColor = TrayMenuColors.Surface,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = TrayMenuColors.Text,
            Margin = Padding.Empty,
            Padding = new Padding(12, 0, 12, 0),
            Size = new Size(TraySubmenuWidth, TrayMenuItemHeight),
            TextAlign = ContentAlignment.MiddleLeft
        };
        parent.DropDownItems.Add(item);
        return item;
    }

    private static void AddTraySeparator(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripSeparator
        {
            AutoSize = false,
            Margin = new Padding(6, 4, 6, 4),
            Size = new Size(TrayMenuWidth - 12, 1)
        });
    }

    private static void AddTraySubmenuSeparator(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Add(new ToolStripSeparator
        {
            AutoSize = false,
            Margin = new Padding(6, 4, 6, 4),
            Size = new Size(TraySubmenuWidth - 12, 1)
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
            UpdateServiceStatusMenu(devspaceRunning, tunnelRunning);
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
            UpdateServiceStatusMenu(false, false);
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

    private void UpdateServiceStatusMenu(bool devspaceRunning, bool tunnelRunning)
    {
        if (_devSpaceStatusItem is not null)
        {
            _devSpaceStatusItem.Text = $"当前状态：{State(devspaceRunning)}";
        }

        if (_tunnelStatusItem is not null)
        {
            _tunnelStatusItem.Text = $"当前状态：{State(tunnelRunning)}";
        }
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        try
        {
            var config = _app.ConfigStore.Reload();
            if (!config.CheckUpdates)
            {
                ApplyAvailableUpdateStatus(new AvailableUpdateStatus(false, "", ""));
                return;
            }
            if (DateTimeOffset.Now - _lastBackgroundUpdateCheck < TimeSpan.FromHours(config.UpdateCheckHours)) return;

            _lastBackgroundUpdateCheck = DateTimeOffset.Now;
            var status = await CheckUpdateStatusAsync();
            ApplyAvailableUpdateStatus(status);
            if (!status.HasUpdate) return;
            if (string.Equals(config.LastNotifiedUpdateVersion, status.NotificationVersion, StringComparison.OrdinalIgnoreCase)) return;

            config.LastNotifiedUpdateVersion = status.NotificationVersion;
            _app.ConfigStore.Save(config);
            _tray.ShowBalloonTip(
                5000,
                "发现可用更新",
                $"{status.BalloonMessage}{Environment.NewLine}可在托盘菜单或主窗口执行更新。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Update($"后台检查更新失败：{ex.Message}");
        }
    }

    private async Task<AvailableUpdateStatus> CheckUpdateStatusAsync()
    {
        var devSpaceTask = _app.Updates.CheckDevSpaceAsync();
        var cloudflaredTask = _app.Updates.CheckCloudflaredAsync();
        await Task.WhenAll(devSpaceTask, cloudflaredTask);

        var devSpace = await devSpaceTask;
        var cloudflared = await cloudflaredTask;
        var available = new List<string>();
        if (devSpace.HasUpdate)
        {
            available.Add($"DevSpace {devSpace.CurrentVersion} -> {devSpace.LatestVersion}");
        }

        if (cloudflared.HasUpdate)
        {
            available.Add($"cloudflared {cloudflared.CurrentVersion} -> {cloudflared.LatestVersion}");
        }

        var message = available.Count == 0
            ? ""
            : string.Join("；", available);
        var notificationVersion = string.Join("|", available);
        return new AvailableUpdateStatus(available.Count > 0, message, notificationVersion);
    }

    private void ApplyAvailableUpdateStatus(AvailableUpdateStatus status)
    {
        _hasAvailableUpdate = status.HasUpdate;
        _availableUpdateMessage = status.ToolTipMessage;
        _mainWindow?.SetUpdateAvailable(status.HasUpdate, status.ToolTipMessage);
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
            RefreshMainWindowEnvironmentSoon();
            _tray.ShowBalloonTip(2500, "DevSpace 管理器", $"{actionName}已执行。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "操作失败", ex.Message, ToolTipIcon.Error);
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshMainWindowEnvironmentSoon()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _ = _mainWindow.RefreshEnvironmentDiagnosticAsync(allowStartupGrace: true);
        _mainWindow.QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(4), allowStartupGrace: true);
        _mainWindow.QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(10));
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
            ShowEnvironmentSetupWindow(orderedMode: false);
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
            _dispatcher.BeginInvoke(ShowMainWindow);
            return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        try
        {
            Log.App("ShowMainWindow requested.");
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow(_app, ShowUpdateWindow);
                _mainWindow.Closing += (_, args) =>
                {
                    if (!_exiting && !_closingForLightweightMode)
                    {
                        args.Cancel = true;
                        _mainWindow.Hide();
                    }
                };
                _mainWindow.Closed += (_, _) => _mainWindow = null;
                _mainWindow.SetUpdateAvailable(_hasAvailableUpdate, _availableUpdateMessage);
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
            MessageBox.Show(ex.Message, "打开 ChatGPT 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSettingsWindow()
    {
        try
        {
            if (_settingsWindow is null || !_settingsWindow.IsLoaded)
            {
                Func<Task>? reloadChatGptView = _mainWindow is null
                    ? null
                    : () => _mainWindow.ReloadChatGptViewAsync();
                _settingsWindow = new SettingsWindow(_app, reloadChatGptView)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.App(ex.ToString());
            MessageBox.Show(ex.Message, "打开设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowEnvironmentSetupWindow(bool orderedMode)
    {
        try
        {
            if (_environmentSetupWindow is null || !_environmentSetupWindow.IsLoaded)
            {
                _environmentSetupWindow = new EnvironmentSetupWindow(_app, orderedMode)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                };
                _environmentSetupWindow.Closed += (_, _) => _environmentSetupWindow = null;
            }

            _environmentSetupWindow.Show();
            _environmentSetupWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.App(ex.ToString());
            MessageBox.Show(ex.Message, "打开环境诊断失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowUpdateWindow()
    {
        try
        {
            if (_updateWindow is null || !_updateWindow.IsLoaded)
            {
                _updateWindow = new UpdateWindow(_app)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                };
                _updateWindow.UpdateStatusChanged += (_, status) =>
                {
                    ApplyAvailableUpdateStatus(new AvailableUpdateStatus(
                        status.HasUpdate,
                        status.ToolTipMessage,
                        status.NotificationVersion));
                };
                _updateWindow.Closed += (_, _) => _updateWindow = null;
            }

            _updateWindow.Show();
            _updateWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.App(ex.ToString());
            MessageBox.Show(ex.Message, "打开检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EnterLightweightMode()
    {
        try
        {
            _settingsWindow?.ForceClose();
            _settingsWindow = null;
            _environmentSetupWindow?.Close();
            _environmentSetupWindow = null;
            _updateWindow?.Close();
            _updateWindow = null;

            if (_mainWindow is not null)
            {
                _closingForLightweightMode = true;
                try
                {
                    _mainWindow.Close();
                    _mainWindow = null;
                }
                finally
                {
                    _closingForLightweightMode = false;
                }
            }

            _tray.ShowBalloonTip(2500, "DevSpace 管理器", "已进入轻量模式，后端服务继续运行。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.App(ex.ToString());
            MessageBox.Show(ex.Message, "进入轻量模式失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private Task StartRoleAsync(ProcessRole role, CancellationToken cancellationToken) =>
        role == ProcessRole.CloudflareTunnel
            ? _app.Processes.StartAsync(role, cancellationToken)
            : Task.Run(() => _app.Processes.Start(role), cancellationToken);

    private Task RestartRoleAsync(ProcessRole role, CancellationToken cancellationToken) =>
        role == ProcessRole.CloudflareTunnel
            ? _app.Processes.RestartAsync(role, cancellationToken)
            : Task.Run(() => _app.Processes.Restart(role), cancellationToken);

    private sealed record AvailableUpdateStatus(
        bool HasUpdate,
        string ToolTipMessage,
        string NotificationVersion)
    {
        public string BalloonMessage => string.IsNullOrWhiteSpace(ToolTipMessage)
            ? "发现可更新组件"
            : ToolTipMessage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _exiting = true;
            _app.NativeNotificationRequested -= OnNativeNotificationRequested;
            try
            {
                _backgroundWorkerCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_mainWindow is not null)
            {
                _mainWindow.Close();
                _mainWindow = null;
            }
            _settingsWindow?.ForceClose();
            _settingsWindow = null;
            _environmentSetupWindow?.Close();
            _environmentSetupWindow = null;
            _updateWindow?.Close();
            _updateWindow = null;
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
            var rightInset = e.Item is ToolStripMenuItem { HasDropDownItems: true }
                ? 30
                : 14;
            var textBounds = new Rectangle(14, 0, e.Item.Width - 14 - rightInset, e.Item.Height);
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

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item || !item.HasDropDownItems)
            {
                return;
            }

            var centerY = e.Item.Height / 2;
            var right = e.Item.Width - 13;
            using var pen = new Pen(TrayMenuColors.MutedText, 1.4F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(
                pen,
                new[]
                {
                    new Point(right - 4, centerY - 5),
                    new Point(right, centerY),
                    new Point(right - 4, centerY + 5)
                });
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
