using System.Diagnostics;
using System.Text.Json;
using DevSpaceManager.Core;
using DevSpaceManager.Services;

namespace DevSpaceManager.UI;

internal sealed class SettingsForm : Form
{
    private readonly AppHost _app;
    private ComboBox _nodeVersion = null!;
    private TextBox _publicBaseUrl = null!;
    private TextBox _tunnelName = null!;
    private NumericUpDown _devSpacePort = null!;
    private TextBox _mcpUrl = null!;
    private TextBox _healthUrl = null!;
    private TextBox _ownerPassword = null!;
    private Button _toggleOwnerPasswordButton = null!;
    private ComboBox _cloudflaredProtocol = null!;
    private CheckedListBox _allowedRoots = null!;
    private CheckBox _autoStartDevSpace = null!;
    private CheckBox _autoStartTunnel = null!;
    private CheckBox _autoRestart = null!;
    private CheckBox _checkUpdates = null!;
    private ComboBox _updateHours = null!;
    private TextBox _overview = null!;
    private Label _status = null!;
    private TextBox _advancedText = null!;
    private TextBox _devspaceConfig = null!;
    private TextBox _cloudflaredConfig = null!;
    private Button _checkUpdatesButton = null!;
    private Button _updateAndRestartButton = null!;
    private Button _quickCheckUpdatesButton = null!;
    private Button _refreshStatusButton = null!;
    private Button _syncDomainButton = null!;
    private Button _runSpeedTestButton = null!;
    private Label _startupStatus = null!;
    private TextBox _speedTestOutput = null!;
    private bool _ownerPasswordVisible;

    public SettingsForm(AppHost app)
    {
        _app = app;
        Text = "DevSpace 管理器";
        Icon = NotifyIconFactory.Create(false);
        StartPosition = FormStartPosition.CenterScreen;
        Width = 920;
        Height = 760;
        MinimumSize = new Size(860, 680);
        BuildUi();
        LoadData();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildCommonPage());
        tabs.TabPages.Add(BuildProjectRootsPage());
        tabs.TabPages.Add(BuildSpeedTestPage());
        tabs.TabPages.Add(BuildStartupPage());
        tabs.TabPages.Add(BuildUpdatePage());
        tabs.TabPages.Add(BuildAdvancedPage());
        root.Controls.Add(tabs, 0, 0);

        _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        root.Controls.Add(_status, 0, 1);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(Button("关闭", (_, _) => Close()));
        footer.Controls.Add(Button("保存", (_, _) => SaveAll()));
        footer.Controls.Add(Button("初始化 / 安装", (_, _) => OpenInitialization()));
        _refreshStatusButton = Button("检查状态", async (_, _) => await RefreshOverviewAsync(showProgress: true));
        footer.Controls.Add(_refreshStatusButton);
        root.Controls.Add(footer, 0, 2);

        var quick = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        quick.Controls.Add(Button("启动全部", async (_, _) => await RunProcessActionAsync("启动全部", _app.Processes.StartAll)));
        quick.Controls.Add(Button("停止全部", async (_, _) => await RunProcessActionAsync("停止全部", _app.Processes.StopAll)));
        quick.Controls.Add(Button("重启全部", async (_, _) => await RunProcessActionAsync("重启全部", _app.Processes.RestartAll)));
        _syncDomainButton = Button("同步域名并重启", async (_, _) => await SyncDomainAsync());
        quick.Controls.Add(_syncDomainButton);
        _quickCheckUpdatesButton = Button("检查更新", async (_, _) => await CheckUpdatesAsync());
        quick.Controls.Add(_quickCheckUpdatesButton);
        root.Controls.Add(quick, 0, 3);

        Controls.Add(root);
    }

    private TabPage BuildCommonPage()
    {
        var page = new TabPage("基础");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 308));

        _overview = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Regular),
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = SystemColors.Window
        };
        layout.Controls.Add(_overview, 0, 0);

        var basics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        basics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        basics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        basics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
        for (var i = 0; i < 8; i++) basics.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        basics.Controls.Add(Label("公网域名"), 0, 0);
        _publicBaseUrl = new TextBox { Dock = DockStyle.Fill };
        _publicBaseUrl.TextChanged += (_, _) => UpdateDerivedUrls();
        basics.Controls.Add(_publicBaseUrl, 1, 0);
        basics.Controls.Add(new Label
        {
            Text = "只填域名",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 2, 0);
        basics.Controls.Add(Label("隧道名称"), 0, 1);
        _tunnelName = new TextBox { Dock = DockStyle.Fill };
        basics.Controls.Add(_tunnelName, 1, 1);
        basics.Controls.Add(Label("每台电脑不同"), 2, 1);
        basics.Controls.Add(Label("本地端口"), 0, 2);
        _devSpacePort = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Minimum = 1,
            Maximum = 65535,
            Value = 7676,
            Width = 140
        };
        _devSpacePort.ValueChanged += (_, _) => UpdateDerivedUrls();
        basics.Controls.Add(_devSpacePort, 1, 2);
        basics.Controls.Add(Label("默认：7676"), 2, 2);
        basics.Controls.Add(Label("MCP 地址"), 0, 3);
        _mcpUrl = ReadOnlyBox();
        basics.Controls.Add(_mcpUrl, 1, 3);
        basics.Controls.Add(Button("复制", (_, _) => CopyText(_mcpUrl.Text)), 2, 3);
        basics.Controls.Add(Label("健康检查地址"), 0, 4);
        _healthUrl = ReadOnlyBox();
        basics.Controls.Add(_healthUrl, 1, 4);
        basics.Controls.Add(Button("复制", (_, _) => CopyText(_healthUrl.Text)), 2, 4);
        basics.Controls.Add(Label("当前密钥"), 0, 5);
        _ownerPassword = ReadOnlyBox();
        _ownerPassword.UseSystemPasswordChar = true;
        basics.Controls.Add(_ownerPassword, 1, 5);
        var secretButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _toggleOwnerPasswordButton = Button("显示", (_, _) => ToggleOwnerPassword());
        secretButtons.Controls.Add(_toggleOwnerPasswordButton);
        secretButtons.Controls.Add(Button("复制", (_, _) => CopyText(_ownerPassword.Text)));
        basics.Controls.Add(secretButtons, 2, 5);
        basics.Controls.Add(Label("隧道模式"), 0, 6);
        _cloudflaredProtocol = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cloudflaredProtocol.Items.AddRange(new object[] { "auto", "http2", "quic" });
        basics.Controls.Add(_cloudflaredProtocol, 1, 6);
        basics.Controls.Add(Label("推荐：http2"), 2, 6);
        basics.Controls.Add(Label("Node 版本"), 0, 7);
        _nodeVersion = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        basics.Controls.Add(_nodeVersion, 1, 7);
        layout.Controls.Add(basics, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildProjectRootsPage()
    {
        var page = new TabPage("项目目录");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "这些目录就是允许 ChatGPT 打开的项目范围。建议按项目目录添加，不要直接放整个磁盘。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var rootsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootsPanel.Controls.Add(Label("允许访问的项目目录"), 0, 0);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        _allowedRoots = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        split.Controls.Add(_allowedRoots, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8, 0, 0, 0)
        };
        buttons.Controls.Add(SideButton("添加目录", (_, _) => AddRoot()));
        buttons.Controls.Add(SideButton("删除所选", (_, _) => RemoveSelectedRoot()));
        buttons.Controls.Add(SideButton("全部启用", (_, _) => SetAllRoots(true)));
        split.Controls.Add(buttons, 1, 0);
        rootsPanel.Controls.Add(split, 0, 1);
        layout.Controls.Add(rootsPanel, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSpeedTestPage()
    {
        var page = new TabPage("测速");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _runSpeedTestButton = Button("开始测试 5 轮", async (_, _) => await RunSpeedTestAsync());
        buttons.Controls.Add(_runSpeedTestButton);
        buttons.Controls.Add(Button("复制结果", (_, _) => CopyText(_speedTestOutput.Text)));
        layout.Controls.Add(buttons, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "测速会分别请求本地 healthz 和公网 healthz。公网比本地慢很多，瓶颈通常在 Cloudflare Tunnel、DNS、代理或网络链路；两边都慢，则优先看 DevSpace 本身或电脑负载。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        _speedTestOutput = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 9.5f)
        };
        layout.Controls.Add(_speedTestOutput, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildStartupPage()
    {
        var page = new TabPage("启动");
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _autoStartDevSpace = new CheckBox { Text = "启动管理器时自动启动 DevSpace", Dock = DockStyle.Fill };
        _autoStartTunnel = new CheckBox { Text = "启动管理器时自动启动 Cloudflare Tunnel", Dock = DockStyle.Fill };
        _autoRestart = new CheckBox { Text = "服务异常退出时自动重启", Dock = DockStyle.Fill };
        panel.Controls.Add(_autoStartDevSpace, 0, 0);
        panel.Controls.Add(_autoStartTunnel, 0, 1);
        panel.Controls.Add(_autoRestart, 0, 2);
        panel.Controls.Add(new Label
        {
            Text = "登录后托盘启动不需要密码；开机后台运行可以在无人登录时启动，但需要 Windows 计划任务保存一次账户密码。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 3);

        var trayButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        trayButtons.Controls.Add(Button("启用登录后托盘启动", (_, _) => EnableTrayStartup()));
        trayButtons.Controls.Add(Button("取消登录后托盘启动", (_, _) => DisableTrayStartup()));
        panel.Controls.Add(trayButtons, 0, 4);

        var workerButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        workerButtons.Controls.Add(Button("启用开机后台运行", (_, _) => EnableWorkerStartup()));
        workerButtons.Controls.Add(Button("移除开机后台运行", (_, _) => DisableWorkerStartup()));
        panel.Controls.Add(workerButtons, 0, 5);

        _startupStatus = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 0, 8, 0)
        };
        panel.Controls.Add(_startupStatus, 0, 6);

        panel.Controls.Add(new Label
        {
            Text = "建议：普通使用先开“登录后托盘启动”；如果希望电脑重启后即使没人登录也能远程连接，再启用“开机后台运行”。",
            Dock = DockStyle.Fill
        }, 0, 7);

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildUpdatePage()
    {
        var page = new TabPage("更新");
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(Label("自动检查更新"), 0, 0);
        _checkUpdates = new CheckBox { Dock = DockStyle.Left };
        panel.Controls.Add(_checkUpdates, 1, 0);

        panel.Controls.Add(Label("检查频率"), 0, 1);
        _updateHours = new ComboBox { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _updateHours.Items.AddRange(new object[] { "6 小时", "12 小时", "24 小时", "48 小时" });
        panel.Controls.Add(_updateHours, 1, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _checkUpdatesButton = Button("立即检查更新", async (_, _) => await CheckUpdatesAsync());
        _updateAndRestartButton = Button("更新后重启 DevSpace", async (_, _) => await UpdateAndRestartAsync());
        buttons.Controls.Add(_checkUpdatesButton);
        buttons.Controls.Add(_updateAndRestartButton);
        panel.Controls.Add(buttons, 1, 2);

        panel.Controls.Add(new Label
        {
            Text = "这里只管理 DevSpace 版本更新。cloudflared 的升级频率通常更低，可以手动处理。",
            Dock = DockStyle.Fill
        }, 1, 3);

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildAdvancedPage()
    {
        var page = new TabPage("高级");
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildRawConfigPage("DevSpace config.json", out _devspaceConfig));
        tabs.TabPages.Add(BuildRawConfigPage("cloudflared config.yml", out _cloudflaredConfig));
        tabs.TabPages.Add(BuildManagerConfigPage());
        page.Controls.Add(tabs);
        return page;
    }

    private TabPage BuildRawConfigPage(string title, out TextBox editor)
    {
        var page = new TabPage(title);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("重新载入", (_, _) => LoadRawConfigs()));
        buttons.Controls.Add(Button("保存", (_, _) => SaveRawConfigs()));
        layout.Controls.Add(buttons, 0, 0);
        editor = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9)
        };
        layout.Controls.Add(editor, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildManagerConfigPage()
    {
        var page = new TabPage("管理器配置");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("打开日志目录", (_, _) => OpenPath(AppPaths.LogDirectory)));
        buttons.Controls.Add(Button("打开配置目录", (_, _) => OpenPath(AppPaths.AppDataDirectory)));
        layout.Controls.Add(buttons, 0, 0);
        _advancedText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9)
        };
        layout.Controls.Add(_advancedText, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private void LoadData()
    {
        var config = _app.ConfigStore.Reload();
        LoadNodeVersions(config.NodeVersion);
        _publicBaseUrl.Text = config.PublicBaseUrl;
        _tunnelName.Text = config.CloudflareTunnelName;
        _devSpacePort.Value = Math.Clamp(config.DevSpacePort, 1, 65535);
        LoadOwnerPassword();
        UpdateDerivedUrls();
        _cloudflaredProtocol.SelectedItem = string.IsNullOrWhiteSpace(config.CloudflaredProtocol) ? "auto" : config.CloudflaredProtocol;
        _autoStartDevSpace.Checked = config.AutoStartDevSpace;
        _autoStartTunnel.Checked = config.AutoStartTunnel;
        _autoRestart.Checked = config.AutoRestart;
        _checkUpdates.Checked = config.CheckUpdates;
        _updateHours.SelectedItem = $"{config.UpdateCheckHours} 小时";
        LoadAllowedRoots(config.DevSpaceConfigPath);
        LoadRawConfigs();
        LoadAdvancedSummary(config);
        RefreshStartupStatus();
        _ = RefreshOverviewAsync();
    }

    private void EnableTrayStartup()
    {
        try
        {
            _app.Scheduler.RegisterTrayAtLogon();
            RefreshStartupStatus("已启用：登录 Windows 后会自动启动托盘管理器（无需密码）。");
        }
        catch (Exception ex)
        {
            RefreshStartupStatus("启用登录后托盘启动失败。");
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DisableTrayStartup()
    {
        try
        {
            _app.Scheduler.UnregisterTray();
            RefreshStartupStatus("已取消：登录后不会自动启动托盘管理器。");
        }
        catch (Exception ex)
        {
            RefreshStartupStatus("取消登录后托盘启动失败。");
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EnableWorkerStartup()
    {
        try
        {
            var userName = $@"{Environment.UserDomainName}\{Environment.UserName}";
            _app.Scheduler.RegisterWorkerAtBoot(userName);
            RefreshStartupStatus("已打开计划任务注册窗口：请在弹出的黑色窗口里按提示输入 Windows 密码。");
        }
        catch (Exception ex)
        {
            RefreshStartupStatus("打开开机后台运行注册窗口失败。");
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DisableWorkerStartup()
    {
        try
        {
            _app.Scheduler.UnregisterWorker();
            RefreshStartupStatus("已移除：开机后台运行任务已删除。");
        }
        catch (Exception ex)
        {
            RefreshStartupStatus("移除开机后台运行失败。");
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshStartupStatus(string? actionResult = null)
    {
        if (_startupStatus is null) return;
        var tray = _app.Scheduler.IsTrayRegistered() ? "已启用" : "未启用";
        var worker = _app.Scheduler.IsWorkerRegistered() ? "已启用" : "未启用";
        var prefix = string.IsNullOrWhiteSpace(actionResult) ? "" : $"{actionResult}{Environment.NewLine}";
        _startupStatus.Text =
            $"{prefix}登录后托盘启动：{tray}{Environment.NewLine}" +
            $"开机后台运行：{worker}";
        if (!string.IsNullOrWhiteSpace(actionResult))
        {
            _status.Text = actionResult;
        }
    }

    private void LoadNodeVersions(string selected)
    {
        _nodeVersion.Items.Clear();
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ??
                      Path.Combine(AppPaths.UserProfile, "AppData", "Local", "nvm");
        if (Directory.Exists(nvmHome))
        {
            foreach (var dir in Directory.GetDirectories(nvmHome, "v*"))
            {
                _nodeVersion.Items.Add(Path.GetFileName(dir).TrimStart('v'));
            }
        }

        if (_nodeVersion.Items.Count == 0)
        {
            _nodeVersion.Items.Add(selected);
        }

        _nodeVersion.SelectedItem = _nodeVersion.Items.Cast<object>().FirstOrDefault(item => string.Equals(item?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
                                    ?? _nodeVersion.Items[0];
    }

    private void LoadAllowedRoots(string devspaceConfigPath)
    {
        _allowedRoots.Items.Clear();
        foreach (var root in ReadAllowedRoots(devspaceConfigPath))
        {
            _allowedRoots.Items.Add(root, true);
        }
    }

    private async Task RefreshOverviewAsync(bool showProgress = false)
    {
        if (showProgress)
        {
            SetStatusChecking(true);
        }

        var config = _app.ConfigStore.Current;
        try
        {
            var local = await _app.Health.CheckLocalAsync();
            var pub = await _app.Health.CheckPublicAsync();
            var devspaceRunning = local.Ok || _app.Processes.IsRunning(ProcessRole.DevSpace);
            var tunnelRunning = pub.Ok || _app.Processes.IsRunning(ProcessRole.CloudflareTunnel);
            var checkedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _overview.Text =
                $"最后检查：{checkedAt}{Environment.NewLine}" +
                $"DevSpace：{State(devspaceRunning)}{Environment.NewLine}" +
                $"Cloudflare Tunnel：{State(tunnelRunning)}{Environment.NewLine}" +
                $"当前公网地址：{config.PublicBaseUrl}{Environment.NewLine}" +
                $"隧道名称：{config.CloudflareTunnelName}{Environment.NewLine}" +
                $"MCP 地址：{config.McpUrl}{Environment.NewLine}" +
                $"本地连接：{State(local.Ok)}  {local.Message}{Environment.NewLine}" +
                $"公网连接：{State(pub.Ok)}  {pub.Message}{Environment.NewLine}" +
                $"当前 Node 版本：{config.NodeVersion}";
            UpdateDerivedUrls();

            if (showProgress)
            {
                _status.Text = $"状态检查完成：{checkedAt}";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "状态检查失败。";
            MessageBox.Show(ex.Message, "状态检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (showProgress)
            {
                SetStatusChecking(false);
            }
        }
    }

    private async Task CheckUpdatesAsync()
    {
        await RunBusyAsync("正在检查 DevSpace 更新...", async () =>
        {
            var update = await _app.Updates.CheckDevSpaceAsync();
            MessageBox.Show(update.Notes, "DevSpace 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void OpenInitialization()
    {
        SaveAll();
        using var form = new InitializationForm(_app);
        form.ShowDialog(this);
    }

    private async Task RunSpeedTestAsync()
    {
        await RunBusyAsync("正在测试连接速度...", async () =>
        {
            if (_runSpeedTestButton is not null)
            {
                _runSpeedTestButton.Enabled = false;
                _runSpeedTestButton.Text = "测试中...";
            }

            var progress = new Progress<string>(message =>
            {
                _status.Text = message;
                _speedTestOutput.Text = $"{message}{Environment.NewLine}";
            });
            var results = await _app.NetworkTests.RunAsync(5, progress);
            _speedTestOutput.Text = BuildSpeedTestReport(results);
            _status.Text = "测速完成。";
        });
    }

    private async Task UpdateAndRestartAsync()
    {
        await RunBusyAsync("正在检查并准备更新...", async () =>
        {
            var update = await _app.Updates.CheckDevSpaceAsync();
            if (!update.HasUpdate)
            {
                MessageBox.Show(update.Notes, "DevSpace 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"{update.Notes}{Environment.NewLine}{Environment.NewLine}确认更新并在完成后重启 DevSpace？",
                "确认更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var progress = new Progress<string>(message => _status.Text = message);
            await _app.Updates.UpdateDevSpaceAsync(progress);
            _app.Processes.Start(ProcessRole.DevSpace);
            _status.Text = "更新完成，已请求重启 DevSpace。";
        });
    }

    private async Task RunProcessActionAsync(string actionName, Action action)
    {
        await RunBusyAsync($"正在{actionName}...", async () =>
        {
            action();
            _status.Text = $"{actionName}已执行，正在刷新状态...";
            await Task.Delay(1200);
            await RefreshOverviewAsync();
            _status.Text = $"{actionName}完成。";
        });
    }

    private async Task SyncDomainAsync()
    {
        await RunBusyAsync("正在同步域名...", async () =>
        {
            SaveAll();
            var config = _app.ConfigStore.Reload();
            var hostname = new Uri(config.PublicBaseUrl).Host;
            WriteDevSpaceConnection(config.DevSpaceConfigPath, config.PublicBaseUrl, config.DevSpacePort);
            WriteCloudflaredIngress(config.CloudflaredConfigPath, hostname, config.DevSpacePort);
            RunCloudflaredDnsRoute(config, hostname);
            _app.Processes.Restart(ProcessRole.CloudflareTunnel);
            _app.Processes.Restart(ProcessRole.DevSpace);
            LoadRawConfigs();
            await RefreshOverviewAsync();
            _status.Text = $"域名已同步：{hostname}";
        });
    }

    private void SaveAll()
    {
        try
        {
            var config = _app.ConfigStore.Current;
            config.NodeVersion = _nodeVersion.SelectedItem?.ToString() ?? config.NodeVersion;
            config.NodeDirectory = Path.Combine(Environment.GetEnvironmentVariable("NVM_HOME") ??
                                                Path.Combine(AppPaths.UserProfile, "AppData", "Local", "nvm"),
                                                $"v{config.NodeVersion}");
            config.DevSpaceCommand = Path.Combine(config.NodeDirectory, "devspace");
            config.NpmCommand = Path.Combine(config.NodeDirectory, "npm");
            config.DevSpacePort = (int)_devSpacePort.Value;
            config.LocalHealthUrl = $"http://127.0.0.1:{config.DevSpacePort}/healthz";
            config.PublicBaseUrl = NormalizeBaseUrl(_publicBaseUrl.Text);
            config.PublicHealthUrl = $"{config.PublicBaseUrl}/healthz";
            config.CloudflareTunnelName = NormalizeTunnelName(_tunnelName.Text);
            config.CloudflaredProtocol = _cloudflaredProtocol.SelectedItem?.ToString() ?? "auto";
            config.AutoStartDevSpace = _autoStartDevSpace.Checked;
            config.AutoStartTunnel = _autoStartTunnel.Checked;
            config.AutoRestart = _autoRestart.Checked;
            config.CheckUpdates = _checkUpdates.Checked;
            config.UpdateCheckHours = ParseHours(_updateHours.SelectedItem?.ToString(), config.UpdateCheckHours);
            _app.ConfigStore.Save(config);
            SaveAllowedRoots(config.DevSpaceConfigPath);
            SaveRawConfigs();
            WriteDevSpaceConnection(config.DevSpaceConfigPath, config.PublicBaseUrl, config.DevSpacePort);
            WriteCloudflaredIngress(config.CloudflaredConfigPath, new Uri(config.PublicBaseUrl).Host, config.DevSpacePort);
            LoadAdvancedSummary(config);
            LoadRawConfigs();
            UpdateDerivedUrls();
            LoadOwnerPassword();
            _status.Text = "已保存。部分修改需要重启服务后生效。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择允许 ChatGPT 打开的项目目录"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (_allowedRoots.Items.Contains(dialog.SelectedPath)) return;
        _allowedRoots.Items.Add(dialog.SelectedPath, true);
    }

    private void RemoveSelectedRoot()
    {
        if (_allowedRoots.SelectedItem is null) return;
        _allowedRoots.Items.Remove(_allowedRoots.SelectedItem);
    }

    private void SetAllRoots(bool enabled)
    {
        for (var i = 0; i < _allowedRoots.Items.Count; i++)
        {
            _allowedRoots.SetItemChecked(i, enabled);
        }
    }

    private void LoadRawConfigs()
    {
        var config = _app.ConfigStore.Current;
        _devspaceConfig.Text = File.Exists(config.DevSpaceConfigPath)
            ? FormatJsonIfPossible(File.ReadAllText(config.DevSpaceConfigPath))
            : "";
        _cloudflaredConfig.Text = File.Exists(config.CloudflaredConfigPath)
            ? NormalizeEditorText(File.ReadAllText(config.CloudflaredConfigPath))
            : "";
    }

    private void SaveRawConfigs()
    {
        var config = _app.ConfigStore.Current;
        WriteTextFile(config.DevSpaceConfigPath, FormatJsonIfPossible(_devspaceConfig.Text));
        WriteTextFile(config.CloudflaredConfigPath, NormalizeEditorText(_cloudflaredConfig.Text));
    }

    private void LoadAdvancedSummary(ManagerConfig config)
    {
        _advancedText.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<string> ReadAllowedRoots(string devspaceConfigPath)
    {
        if (!File.Exists(devspaceConfigPath)) return Array.Empty<string>();
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(devspaceConfigPath));
            if (!json.RootElement.TryGetProperty("allowedRoots", out var roots) || roots.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return roots.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))!.Cast<string>().ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void SaveAllowedRoots(string devspaceConfigPath)
    {
        var root = ReadJsonObjectOrEmpty(devspaceConfigPath);
        var roots = _allowedRoots.CheckedItems.Cast<object>().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        root["allowedRoots"] = roots;
        WriteTextFile(devspaceConfigPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ParseHours(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : fallback;
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("公网地址格式不正确，请输入类似 https://devspace.onemem.cc");
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string NormalizeTunnelName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("隧道名称不能为空。建议每台电脑使用不同名称，例如 devspace-home、devspace-company。");
        }

        if (normalized.Length > 64)
        {
            throw new InvalidOperationException("隧道名称太长，请控制在 64 个字符以内。");
        }

        if (normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-')))
        {
            throw new InvalidOperationException("隧道名称只能包含小写字母、数字和短横线，例如 devspace-home。");
        }

        if (normalized.StartsWith('-') || normalized.EndsWith('-') || normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("隧道名称不要以短横线开头或结尾，也不要连续使用短横线。");
        }

        return normalized;
    }

    private static string? ReadCloudflaredTunnelId(string path)
    {
        if (!File.Exists(path)) return null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("tunnel:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["tunnel:".Length..].Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private void UpdateDerivedUrls()
    {
        if (_mcpUrl is null || _healthUrl is null) return;
        var value = _publicBaseUrl.Text.Trim().TrimEnd('/');
        _mcpUrl.Text = string.IsNullOrWhiteSpace(value) ? "" : $"{value}/mcp";
        _healthUrl.Text = string.IsNullOrWhiteSpace(value) ? "" : $"{value}/healthz";
    }

    private void LoadOwnerPassword()
    {
        if (_ownerPassword is null) return;
        var password = _app.AuthSecrets.ReadOwnerPassword();
        _ownerPassword.Text = string.IsNullOrWhiteSpace(password) ? "未找到密钥" : password;
        _ownerPassword.UseSystemPasswordChar = !_ownerPasswordVisible && !string.IsNullOrWhiteSpace(password);
    }

    private void ToggleOwnerPassword()
    {
        _ownerPasswordVisible = !_ownerPasswordVisible;
        _ownerPassword.UseSystemPasswordChar = !_ownerPasswordVisible;
        _toggleOwnerPasswordButton.Text = _ownerPasswordVisible ? "隐藏" : "显示";
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static TextBox ReadOnlyBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window
    };

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30 };
        button.Click += click;
        return button;
    }

    private static Button SideButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, Width = 96, Height = 30, Margin = new Padding(0, 0, 0, 8) };
        button.Click += click;
        return button;
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            _status.Text = "已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunBusyAsync(string statusText, Func<Task> action)
    {
        var previousStatus = _status.Text;
        SetBusy(true, statusText);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DevSpace 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = previousStatus;
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private void SetBusy(bool busy, string statusText)
    {
        UseWaitCursor = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (_checkUpdatesButton is not null) _checkUpdatesButton.Enabled = !busy;
        if (_updateAndRestartButton is not null) _updateAndRestartButton.Enabled = !busy;
        if (_quickCheckUpdatesButton is not null) _quickCheckUpdatesButton.Enabled = !busy;
        if (_runSpeedTestButton is not null)
        {
            _runSpeedTestButton.Enabled = !busy;
            _runSpeedTestButton.Text = busy ? "测试中..." : "开始测试 5 轮";
        }
        _status.Text = statusText;
    }

    private static string BuildSpeedTestReport(IReadOnlyList<SpeedTestRound> rounds)
    {
        var lines = new List<string>
        {
            $"测试时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "说明：耗时越低越好；公网耗时 - 本地耗时，约等于 Cloudflare Tunnel / 网络链路额外开销。",
            ""
        };

        foreach (var round in rounds)
        {
            lines.Add($"第 {round.Round} 轮");
            lines.Add(FormatEndpoint(round.Local));
            lines.Add(FormatEndpoint(round.Public));
            lines.Add($"  额外开销：{Math.Max(0, round.Public.ElapsedMs - round.Local.ElapsedMs)} ms");
            lines.Add("");
        }

        lines.Add("汇总");
        lines.Add(FormatSummary("本地", rounds.Select(round => round.Local)));
        lines.Add(FormatSummary("公网", rounds.Select(round => round.Public)));
        var extras = rounds.Select(round => Math.Max(0, round.Public.ElapsedMs - round.Local.ElapsedMs)).ToArray();
        lines.Add($"公网额外开销：平均 {Average(extras):0} ms，最快 {extras.Min()} ms，最慢 {extras.Max()} ms");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatEndpoint(EndpointTestResult result)
    {
        var state = result.Ok ? "正常" : "异常";
        var code = result.StatusCode == 0 ? "-" : result.StatusCode.ToString();
        return $"  {result.Name}：{state}  {result.ElapsedMs} ms  HTTP {code}  {result.Bytes} bytes  {result.Message}";
    }

    private static string FormatSummary(string name, IEnumerable<EndpointTestResult> items)
    {
        var values = items.ToArray();
        var ok = values.Count(item => item.Ok);
        var times = values.Select(item => item.ElapsedMs).ToArray();
        return $"{name}：成功 {ok}/{values.Length}，平均 {Average(times):0} ms，最快 {times.Min()} ms，最慢 {times.Max()} ms";
    }

    private static double Average(IEnumerable<long> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }

    private void SetStatusChecking(bool checking)
    {
        UseWaitCursor = checking;
        Cursor = checking ? Cursors.WaitCursor : Cursors.Default;
        if (_refreshStatusButton is not null)
        {
            _refreshStatusButton.Enabled = !checking;
            _refreshStatusButton.Text = checking ? "检查中..." : "检查状态";
        }

        if (checking)
        {
            _status.Text = "正在检查 DevSpace 和公网连接...";
            _overview.Text =
                $"正在检查：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                "正在检查本地服务、Cloudflare Tunnel 和公网地址...";
        }
    }

    private static string FormatJsonIfPossible(string text)
    {
        var normalized = NormalizeEditorText(text);
        if (string.IsNullOrWhiteSpace(normalized)) return "";
        try
        {
            using var document = JsonDocument.Parse(normalized);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return normalized;
        }
    }

    private static string NormalizeEditorText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "" : normalized + Environment.NewLine;
    }

    private static void WriteTextFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteCloudflaredIngress(string path, string hostname, int port)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        if (lines.Count == 0)
        {
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://localhost:{port}");
            lines.Add("  - service: http_status:404");
            File.WriteAllLines(path, lines);
            return;
        }

        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains("hostname:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"  - hostname: {hostname}";
                changed = true;
                continue;
            }

            if (lines[i].Contains("service: http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains("service: http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"    service: http://localhost:{port}";
                changed = true;
            }
        }

        if (!changed)
        {
            lines.Clear();
            lines.Add("ingress:");
            lines.Add($"  - hostname: {hostname}");
            lines.Add($"    service: http://localhost:{port}");
            lines.Add("  - service: http_status:404");
        }

        File.WriteAllLines(path, lines);
    }

    private static void WriteDevSpaceConnection(string path, string publicBaseUrl, int port)
    {
        var root = ReadJsonObjectOrEmpty(path);
        root["publicBaseUrl"] = publicBaseUrl.TrimEnd('/');
        root["port"] = port;
        root.TryAdd("host", "127.0.0.1");
        root.TryAdd("allowedRoots", Array.Empty<string>());
        WriteTextFile(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, object?> ReadJsonObjectOrEmpty(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, object?>();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object?>();

        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json.RootElement.GetRawText()) ??
                   new Dictionary<string, object?>();
        }
        catch
        {
            BackupInvalidFile(path);
            return new Dictionary<string, object?>();
        }
    }

    private static void BackupInvalidFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = $"{path}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // Best-effort backup only; saving a valid config is more important.
        }
    }

    private void RunCloudflaredDnsRoute(ManagerConfig config, string hostname)
    {
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        if (!File.Exists(cloudflaredPath)) return;
        var start = new ProcessStartInfo
        {
            FileName = cloudflaredPath,
            Arguments = $"tunnel route dns -f {CommandProcess.Quote(ReadCloudflaredTunnelId(config.CloudflaredConfigPath) ?? config.CloudflareTunnelName)} {hostname}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(config.CloudflaredConfigPath) ?? AppPaths.UserProfile
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 cloudflared。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string State(bool ok) => ok ? "正常" : "异常";

    private static void Safe(Action action)
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
}
