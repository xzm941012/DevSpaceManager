using DevSpaceManager.Core;
using DevSpaceManager.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading;
using System.Text;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace DevSpaceManager.UI;

public sealed partial class EnvironmentSetupWindow
{
    private readonly AppHost _app;
    private readonly bool _orderedMode;
    private const string LucideCopyIcon = "M8 8m0 2a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-8a2 2 0 0 1-2-2z M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2";
    private const string LucideCheckIcon = "M20 6 9 17l-5-5";
    private readonly List<EnvironmentCheck> _latestBasicChecks = [];
    private readonly List<EnvironmentCheck> _latestCloudflareChecks = [];
    private readonly Dictionary<SetupStep, StepCheckState> _stepStates = new();
    private SetupStep _currentStep = SetupStep.Basic;
    private bool _basicPassed;
    private bool _devSpacePassed;
    private bool _cloudflarePassed;
    private bool _fullCheckRunning;
    private bool _cloudflareModeSwitching;
    private int _cloudflareRenderNonce;
    private bool? _cloudflareTunnelRunning;
    private bool? _cloudflareAllowed;
    private string _cloudflareTunnelStatus = "点击刷新检测 Cloudflare Tunnel 启动状态。";
    private string _cloudflareAllowedStatus = "点击刷新检测固定地址是否可用。";
    private bool? _devSpaceRunning;
    private string _devSpaceStatus = "点击刷新检测 DevSpace 启动状态。";
    private WpfTextBox? _cloudflarePublicBaseUrlTextBox;
    private WpfTextBox? _cloudflareTunnelNameTextBox;
    private WpfTextBox? _cloudflarePortTextBox;
    private WpfComboBox? _cloudflareProtocolComboBox;
    private WpfComboBox? _devSpaceNodeVersionComboBox;
    private WpfTextBox? _devSpacePortTextBox;
    private WpfListBox? _devSpaceAllowedRootsList;
    private WpfButton? _devSpaceRemoveRootButton;
    private WpfButton? _lastCopiedButton;

    internal EnvironmentSetupWindow(AppHost app, bool orderedMode)
    {
        _app = app;
        _orderedMode = orderedMode;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowDwmAttributes();
        Loaded += async (_, _) =>
        {
            RenderStepButtons();
            await ShowStepAsync(SetupStep.Basic, runCheck: false);
        };
    }

    private async Task ShowStepAsync(SetupStep step, bool runCheck, bool forceOpen = false)
    {
        if (!forceOpen && !CanOpenStep(step)) return;
        _currentStep = step;
        RenderStepButtons();
        ResultPanel.Children.Clear();
        FooterStatus.Text = "";
        SecondaryActionButton.Visibility = Visibility.Visible;
        PrimaryActionButton.Visibility = Visibility.Visible;
        PrimaryActionButton.IsEnabled = !_fullCheckRunning;
        SecondaryActionButton.IsEnabled = !_fullCheckRunning;

        switch (step)
        {
            case SetupStep.Basic:
                StepTitle.Text = "基础环境检查";
                StepDescription.Text = "检查 Git Bash、nvm、Node/npm、DevSpace 和 cloudflared。未通过的项目可以单独安装，也可以一键安装缺失环境。";
                SecondaryActionButton.Content = "一键安装缺失";
                PrimaryActionButton.Content = _fullCheckRunning ? "检测中..." : "开始检测";
                RenderCachedBasicChecks();
                if (runCheck) await RunBasicCheckAsync();
                break;
            case SetupStep.DevSpaceInit:
                StepTitle.Text = "DevSpace 配置";
                StepDescription.Text = "配置 DevSpace 端口和允许访问目录。当前公网地址直接读取 Cloudflare 隧道地址。";
                SecondaryActionButton.Content = "刷新";
                PrimaryActionButton.Content = "保存";
                RenderDevSpaceConfig();
                break;
            case SetupStep.Cloudflare:
                StepTitle.Text = "Cloudflare 配置";
                StepDescription.Text = "配置 Cloudflare 隧道模式、固定公网域名和本地代理端口。保存后会自动重启并检测状态。";
                SecondaryActionButton.Content = "刷新";
                PrimaryActionButton.Content = "保存";
                RenderCloudflare();
                break;
        }
    }

    private async Task RunBasicCheckAsync()
    {
        ResultPanel.Children.Clear();
        _latestBasicChecks.Clear();
        var pending = new[]
        {
            "Git Bash",
            "nvm for Windows",
            "Node 运行时",
            "npm 命令",
            "cloudflared",
            "DevSpace"
        };
        foreach (var item in pending)
        {
            ResultPanel.Children.Add(ResultRow(item, "正在检查...", CheckVisualState.Progress, null));
            await Task.Delay(120);
        }

        var checks = NormalizeBasicChecks(await _app.Environment.CheckBasicEnvironmentAsync()).ToList();
        _latestBasicChecks.AddRange(checks);
        ResultPanel.Children.Clear();
        foreach (var check in checks)
        {
            ResultPanel.Children.Add(ResultRow(check.Name, check.Detail, check.Ok ? CheckVisualState.Ok : CheckVisualState.Fail, InstallActionFor(check.Name)));
        }

        _basicPassed = checks.All(check => check.Ok);
        FooterStatus.Text = _basicPassed ? "基础环境已就绪。" : "存在缺失环境，请先安装未通过项目。";
        RenderStepButtons();
    }

    private async Task RunFullEnvironmentCheckAsync()
    {
        if (_fullCheckRunning) return;

        _fullCheckRunning = true;
        ResetStepStates();
        PrimaryActionButton.IsEnabled = false;
        SecondaryActionButton.IsEnabled = false;

        try
        {
            await RunStepCheckAsync(SetupStep.Basic);
            await RunStepCheckAsync(SetupStep.Cloudflare);
            await RunStepCheckAsync(SetupStep.DevSpaceInit);

            var allPassed = _basicPassed && _devSpacePassed && _cloudflarePassed;
            FooterStatus.Text = allPassed ? "全部环境检查通过。" : "环境检查完成，存在需要处理的项目。";
        }
        finally
        {
            _fullCheckRunning = false;
            PrimaryActionButton.IsEnabled = true;
            SecondaryActionButton.IsEnabled = true;
            PrimaryActionButton.Content = _currentStep == SetupStep.Basic ? "开始检测" : PrimaryActionButton.Content;
            RenderStepButtons();
        }
    }

    private async Task RunStepCheckAsync(SetupStep step)
    {
        SetStepState(step, StepCheckState.Progress);
        await Task.Delay(260);

        switch (step)
        {
            case SetupStep.Basic:
                await RunBasicCheckAsync();
                break;
            case SetupStep.DevSpaceInit:
                await CheckDevSpaceInitInBackgroundAsync();
                break;
            case SetupStep.Cloudflare:
                await CheckCloudflareInBackgroundAsync();
                break;
        }

        SetStepState(step, IsStepPassed(step) ? StepCheckState.Ok : StepCheckState.Fail);
        await Task.Delay(160);
    }

    private void RenderCachedBasicChecks()
    {
        if (_latestBasicChecks.Count == 0)
        {
            return;
        }

        ResultPanel.Children.Clear();
        foreach (var check in _latestBasicChecks)
        {
            ResultPanel.Children.Add(ResultRow(check.Name, check.Detail, check.Ok ? CheckVisualState.Ok : CheckVisualState.Fail, InstallActionFor(check.Name)));
        }

        FooterStatus.Text = _basicPassed ? "基础环境已就绪。" : "存在缺失环境，请先安装未通过项目。";
    }

    private async Task CheckCloudflareInBackgroundAsync()
    {
        var config = _app.ConfigStore.Reload();
        if (config.UseTemporaryCloudflareTunnel)
        {
            _cloudflarePassed = true;
            await Task.CompletedTask;
            return;
        }

        var checks = (await _app.Environment.CheckInitializationAsync())
            .Where(check => check.Name.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                            check.Name.Contains("Tunnel", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _cloudflarePassed = checks.Count > 0 && checks.All(check => check.Ok);
    }

    private async Task CheckDevSpaceInitInBackgroundAsync()
    {
        var config = _app.ConfigStore.Reload();
        if (!File.Exists(config.DevSpaceConfigPath))
        {
            _devSpacePassed = false;
            await Task.CompletedTask;
            return;
        }

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config.DevSpaceConfigPath));
            var root = json.RootElement;
            var hasPort = root.TryGetProperty("port", out var portProperty) &&
                          portProperty.TryGetInt32(out var port) &&
                          port is >= 1 and <= 65535;
            var hasPublicBaseUrl = root.TryGetProperty("publicBaseUrl", out var publicBaseUrl) &&
                                   !string.IsNullOrWhiteSpace(publicBaseUrl.GetString());
            _devSpacePassed = hasPort && hasPublicBaseUrl;
        }
        catch
        {
            _devSpacePassed = false;
        }

        await Task.CompletedTask;
    }

    private void RenderCloudflare()
    {
        var config = _app.ConfigStore.Reload();
        ResultPanel.Children.Clear();
        _cloudflarePublicBaseUrlTextBox = null;
        _cloudflareTunnelNameTextBox = null;
        _cloudflarePortTextBox = null;
        _cloudflareProtocolComboBox = null;
        PrimaryActionButton.Content = "保存";

        ResultPanel.Children.Add(AddressModeRow(config.UseTemporaryCloudflareTunnel));
        if (config.UseTemporaryCloudflareTunnel)
        {
            ResultPanel.Children.Add(CloudflarePortEditorRow(config.DevSpacePort));
            ResultPanel.Children.Add(StatusOnlyRow(
                "Cloudflare Tunnel",
                _cloudflareTunnelStatus,
                _cloudflareTunnelRunning));
            ResultPanel.Children.Add(ReadOnlyValueRow(
                "当前公网地址",
                TemporaryUrlDisplay(config, _cloudflareTunnelRunning == true),
                HasTemporaryUrl(config) ? CheckVisualState.Ok : CheckVisualState.Fail));
            _cloudflarePassed = true;
            FooterStatus.Text = "临时地址模式只需要本地代理端口和 Tunnel 启动状态。";
        }
        else
        {
            ResultPanel.Children.Add(PublicBaseUrlEditorRow(config));
            ResultPanel.Children.Add(TunnelNameEditorRow(config.CloudflareTunnelName));
            ResultPanel.Children.Add(CloudflarePortEditorRow(config.DevSpacePort));
            ResultPanel.Children.Add(CloudflareProtocolRow(config.CloudflaredProtocol));
            ResultPanel.Children.Add(StatusOnlyRow(
                "Cloudflare Tunnel",
                _cloudflareTunnelStatus,
                _cloudflareTunnelRunning));
            ResultPanel.Children.Add(StatusOnlyRow(
                "固定地址允许状态",
                _cloudflareAllowedStatus,
                _cloudflareAllowed));
            _cloudflarePassed = _cloudflareAllowed == true;
            FooterStatus.Text = "点击刷新可检测 Tunnel 启动状态和固定地址允许状态。";
        }

        RenderStepButtons();
    }

    private async Task RefreshCloudflareAsync()
    {
        var config = _app.ConfigStore.Reload();
        var renderNonce = ++_cloudflareRenderNonce;

        RenderCloudflareRefreshing(config);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var tunnelRunningTask = Task.Run(() => _app.Processes.IsRunning(ProcessRole.CloudflareTunnel));
        Task<bool>? allowedTask = config.UseTemporaryCloudflareTunnel ? null : Task.Run(async () => await CheckFixedCloudflareAllowedAsync());

        if (allowedTask is not null)
        {
            await Task.WhenAll(tunnelRunningTask, allowedTask);
        }
        else
        {
            await tunnelRunningTask;
        }

        if (_currentStep != SetupStep.Cloudflare || renderNonce != _cloudflareRenderNonce)
        {
            return;
        }

        _cloudflareTunnelRunning = tunnelRunningTask.Result;
        _cloudflareTunnelStatus = _cloudflareTunnelRunning == true
            ? "已启动"
            : "未启动";
        if (allowedTask is not null)
        {
            _cloudflareAllowed = allowedTask.Result;
            _cloudflareAllowedStatus = _cloudflareAllowed == true
                ? "允许访问"
                : "未允许访问，请保存补全配置后重试。";
        }
        RenderCloudflare();
    }

    private void RenderCloudflareRefreshing(ManagerConfig config)
    {
        ResultPanel.Children.Clear();
        PrimaryActionButton.Content = "保存";
        ResultPanel.Children.Add(AddressModeRow(config.UseTemporaryCloudflareTunnel));
        if (!config.UseTemporaryCloudflareTunnel)
        {
            ResultPanel.Children.Add(PublicBaseUrlEditorRow(config));
            ResultPanel.Children.Add(TunnelNameEditorRow(config.CloudflareTunnelName));
            ResultPanel.Children.Add(CloudflarePortEditorRow(config.DevSpacePort));
            ResultPanel.Children.Add(CloudflareProtocolRow(config.CloudflaredProtocol));
        }
        else
        {
            ResultPanel.Children.Add(CloudflarePortEditorRow(config.DevSpacePort));
        }

        ResultPanel.Children.Add(StatusOnlyRow("Cloudflare Tunnel", "正在读取启动状态...", null, CheckVisualState.Progress));
        ResultPanel.Children.Add(ReadOnlyValueRow(
            "当前公网地址",
            config.UseTemporaryCloudflareTunnel ? "正在读取临时地址状态..." : config.PublicBaseUrl,
            CheckVisualState.Progress));

        if (config.UseTemporaryCloudflareTunnel)
        {
            FooterStatus.Text = "正在读取临时地址模式状态...";
        }
        else
        {
            ResultPanel.Children.Add(StatusOnlyRow("固定地址允许状态", "正在检测固定地址是否可用...", null, CheckVisualState.Progress));
            FooterStatus.Text = "正在读取固定域名模式状态...";
        }

        RenderStepButtons();
    }

    private FrameworkElement ServiceStatusRow(string title, string detail, bool running)
    {
        return ResultRow(
            title,
            detail,
            running ? CheckVisualState.Ok : CheckVisualState.Fail,
            null);
    }

    private FrameworkElement AddressModeRow(bool useTemporaryTunnel)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = 56 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(ResultIcon(CheckVisualState.Ok));

        var detail = useTemporaryTunnel
            ? "临时地址，每次启动可能变化。"
            : "固定域名地址。";

        var copy = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = "地址模式", Foreground = BrushFrom("#20201D"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        copy.Children.Add(new TextBlock { Text = detail, Foreground = BrushFrom("#6F6F68"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);

        var togglePanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var segmentShell = new Border
        {
            Background = BrushFrom("#F7F7F3"),
            BorderBrush = BrushFrom("#CFCFC7"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2)
        };

        var segmentGrid = new Grid();
        segmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        segmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fixedButton = new WpfButton
        {
            Content = "固定域名",
            Style = (Style)FindResource("SegmentModeButtonStyle"),
            Background = useTemporaryTunnel ? System.Windows.Media.Brushes.Transparent : BrushFrom("#1F1F1B"),
            Foreground = useTemporaryTunnel ? BrushFrom("#4B4B44") : System.Windows.Media.Brushes.White,
            IsEnabled = !_fullCheckRunning && !_cloudflareModeSwitching && useTemporaryTunnel
        };
        fixedButton.Click += (_, _) => SwitchCloudflareMode(useTemporaryTunnel: false);
        segmentGrid.Children.Add(fixedButton);

        var temporaryButton = new WpfButton();
        temporaryButton.Content = "临时地址";
        temporaryButton.Style = (Style)FindResource("SegmentModeButtonStyle");
        temporaryButton.Background = useTemporaryTunnel ? BrushFrom("#1F1F1B") : System.Windows.Media.Brushes.Transparent;
        temporaryButton.Foreground = useTemporaryTunnel ? System.Windows.Media.Brushes.White : BrushFrom("#4B4B44");
        temporaryButton.IsEnabled = !_fullCheckRunning && !_cloudflareModeSwitching && !useTemporaryTunnel;
        temporaryButton.Click += (_, _) => SwitchCloudflareMode(useTemporaryTunnel: true);
        Grid.SetColumn(temporaryButton, 1);
        segmentGrid.Children.Add(temporaryButton);

        segmentShell.Child = segmentGrid;
        togglePanel.Children.Add(segmentShell);

        Grid.SetColumn(togglePanel, 2);
        row.Children.Add(togglePanel);
        return row;
    }

    private void SwitchCloudflareMode(bool useTemporaryTunnel)
    {
        if (_cloudflareModeSwitching) return;

        _cloudflareModeSwitching = true;
        var switched = false;
        try
        {
            if (useTemporaryTunnel)
            {
                _app.PublicEndpoints.ActivateTemporaryMode();
                FooterStatus.Text = "已切换为临时地址模式，点击保存后会重启隧道。";
            }
            else
            {
                _app.PublicEndpoints.ActivateFixedMode();
                FooterStatus.Text = "已切换为固定域名地址模式，点击保存后会重启隧道。";
            }

            _latestCloudflareChecks.Clear();
            switched = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "环境配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cloudflareModeSwitching = false;
        }

        if (switched && _currentStep == SetupStep.Cloudflare)
        {
            RenderCloudflare();
        }
    }

    private async Task<bool> CheckFixedCloudflareAllowedAsync()
    {
        var checks = await _app.Environment.CheckInitializationAsync();
        _latestCloudflareChecks.Clear();
        _latestCloudflareChecks.AddRange(checks.Where(check =>
            check.Name.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
            check.Name.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)));
        return _latestCloudflareChecks.Count > 0 && _latestCloudflareChecks.All(check => check.Ok);
    }

    private FrameworkElement PublicBaseUrlEditorRow(ManagerConfig config)
    {
        _cloudflarePublicBaseUrlTextBox = TextEditor(
            string.IsNullOrWhiteSpace(config.FixedPublicBaseUrl) ? config.PublicBaseUrl : config.FixedPublicBaseUrl,
            320);
        return SettingControlRow(
            "固定公网域名",
            CheckVisualState.Ok,
            _cloudflarePublicBaseUrlTextBox);
    }

    private FrameworkElement TunnelNameEditorRow(string tunnelName)
    {
        _cloudflareTunnelNameTextBox = TextEditor(tunnelName, 240);
        return SettingControlRow(
            "Tunnel 名称",
            CheckVisualState.Ok,
            _cloudflareTunnelNameTextBox);
    }

    private FrameworkElement CloudflarePortEditorRow(int port)
    {
        _cloudflarePortTextBox = TextEditor(port.ToString(), 120, maxLength: 5);
        return SettingControlRow(
            "本地代理端口",
            CheckVisualState.Ok,
            _cloudflarePortTextBox);
    }

    private FrameworkElement CloudflareProtocolRow(string protocol)
    {
        _cloudflareProtocolComboBox = new WpfComboBox
        {
            Width = 140,
            Height = 32,
            Style = (Style)FindResource("CompactComboBoxStyle"),
            Margin = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        _cloudflareProtocolComboBox.Items.Add("auto");
        _cloudflareProtocolComboBox.Items.Add("http2");
        _cloudflareProtocolComboBox.Items.Add("quic");
        _cloudflareProtocolComboBox.SelectedItem = string.IsNullOrWhiteSpace(protocol) ? "auto" : protocol;

        return SettingControlRow(
            "连接协议",
            CheckVisualState.Ok,
            _cloudflareProtocolComboBox);
    }

    private WpfTextBox TextEditor(string value, double width, int maxLength = 0)
    {
        return new WpfTextBox
        {
            Text = value,
            MaxLength = maxLength,
            Width = width,
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = BrushFrom("#D8D8D3"),
            BorderThickness = new Thickness(1),
            Foreground = BrushFrom("#20201D"),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
    }

    private void RenderDevSpaceConfig()
    {
        ResultPanel.Children.Clear();
        var config = _app.ConfigStore.Reload();
        var roots = PublicEndpointSyncService.ReadAllowedRoots(config.DevSpaceConfigPath).ToList();
        var currentDomain = CurrentCloudflareAddressDisplay(config, _cloudflareTunnelRunning == true);

        _devSpacePortTextBox = null;
        _devSpaceNodeVersionComboBox = null;
        _devSpaceAllowedRootsList = null;
        _devSpaceRemoveRootButton = null;

        ResultPanel.Children.Add(ReadOnlyValueRow(
            "当前公网地址",
            currentDomain,
            CheckVisualState.Ok));

        ResultPanel.Children.Add(NodeVersionRow(config.NodeVersion));
        ResultPanel.Children.Add(PortEditorRow(config.DevSpacePort));
        ResultPanel.Children.Add(AllowedRootsRow(roots));
        ResultPanel.Children.Add(DevSpaceStatusRow());

        _devSpacePassed = File.Exists(config.DevSpaceConfigPath) && File.Exists(AppPaths.DevSpaceAuthPath);
        FooterStatus.Text = "点击刷新可检测 DevSpace 启动状态。";
        RenderStepButtons();
    }

    private async Task RefreshDevSpaceConfigAsync()
    {
        ResultPanel.Children.Clear();
        var config = _app.ConfigStore.Reload();
        ResultPanel.Children.Add(ReadOnlyValueRow("当前公网地址", CurrentCloudflareAddressDisplay(config, _cloudflareTunnelRunning == true), CheckVisualState.Ok));
        ResultPanel.Children.Add(NodeVersionRow(config.NodeVersion));
        ResultPanel.Children.Add(PortEditorRow(config.DevSpacePort));
        ResultPanel.Children.Add(AllowedRootsRow(PublicEndpointSyncService.ReadAllowedRoots(config.DevSpaceConfigPath).ToList()));
        ResultPanel.Children.Add(StatusOnlyRow("DevSpace 启动状态", "正在检测启动状态...", null, CheckVisualState.Progress));
        FooterStatus.Text = "正在检测 DevSpace 启动状态...";
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        if (!File.Exists(AppPaths.DevSpaceAuthPath))
        {
            _devSpaceRunning = false;
            _devSpaceStatus = "未生成 Owner password，保存时会自动生成。";
        }
        else
        {
            _devSpaceRunning = await Task.Run(() => _app.Processes.IsRunning(ProcessRole.DevSpace));
            _devSpaceStatus = _devSpaceRunning == true
                ? $"已启动。本地地址：{config.LocalHealthUrl}"
                : $"未启动。本地地址：{config.LocalHealthUrl}";
        }
        RenderDevSpaceConfig();
    }

    private FrameworkElement ReadOnlyValueRow(string title, string value, CheckVisualState state)
    {
        var valueText = new TextBlock
        {
            Text = value,
            Foreground = BrushFrom("#3B3B36"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var copyButton = new WpfButton
        {
            Width = 30,
            Height = 30,
            Style = (Style)FindResource("IconButtonStyle"),
            ToolTip = "复制",
            Content = CopyButtonIcon()
        };
        copyButton.Click += async (_, _) => await CopyValueAsync(value, copyButton);

        var shell = new Border
        {
            Background = BrushFrom("#F7F7F4"),
            BorderBrush = BrushFrom("#DEDED8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 4, 0),
            MinWidth = 360,
            Width = 420,
            Height = 34,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(valueText);
        Grid.SetColumn(copyButton, 1);
        grid.Children.Add(copyButton);
        shell.Child = grid;

        return SettingControlRow(title, state, shell);
    }

    private FrameworkElement PortEditorRow(int port)
    {
        _devSpacePortTextBox = TextEditor(port.ToString(), 120, maxLength: 5);
        return SettingControlRow("本地服务端口", CheckVisualState.Ok, _devSpacePortTextBox);
    }

    private FrameworkElement NodeVersionRow(string selectedVersion)
    {
        _devSpaceNodeVersionComboBox = new WpfComboBox
        {
            Width = 140,
            Height = 32,
            Style = (Style)FindResource("CompactComboBoxStyle"),
            Margin = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        foreach (var version in FindInstalledNodeVersions(selectedVersion))
        {
            _devSpaceNodeVersionComboBox.Items.Add(version);
        }

        _devSpaceNodeVersionComboBox.SelectedItem =
            _devSpaceNodeVersionComboBox.Items.Cast<object>().FirstOrDefault(item =>
                string.Equals(item?.ToString(), selectedVersion, StringComparison.OrdinalIgnoreCase)) ??
            (_devSpaceNodeVersionComboBox.Items.Count > 0 ? _devSpaceNodeVersionComboBox.Items[0] : selectedVersion);

        return SettingControlRow("Node 版本", CheckVisualState.Ok, _devSpaceNodeVersionComboBox);
    }

    private FrameworkElement AllowedRootsRow(IReadOnlyList<string> roots)
    {
        _devSpaceAllowedRootsList = new WpfListBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = BrushFrom("#20201D"),
            MinHeight = 88,
            MaxHeight = 180,
            Padding = new Thickness(0)
        };

        foreach (var root in roots)
        {
            _devSpaceAllowedRootsList.Items.Add(root);
        }

        var listShell = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = BrushFrom("#DEDED8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            MinWidth = 520,
            Height = 112,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        listShell.Child = _devSpaceAllowedRootsList;

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var addButton = new WpfButton
        {
            Content = "添加目录",
            Style = (Style)FindResource("InlineSecondaryButtonStyle"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        addButton.Click += (_, _) => AddDevSpaceAllowedRoot();
        actions.Children.Add(addButton);

        _devSpaceRemoveRootButton = new WpfButton
        {
            Content = "删除所选",
            Style = (Style)FindResource("InlineSecondaryButtonStyle"),
            IsEnabled = _devSpaceAllowedRootsList.Items.Count > 0
        };
        _devSpaceRemoveRootButton.Click += (_, _) => RemoveSelectedDevSpaceAllowedRoot();
        actions.Children.Add(_devSpaceRemoveRootButton);

        return DirectoryListRow(listShell, actions);
    }

    private void AddDevSpaceAllowedRoot()
    {
        if (_devSpaceAllowedRootsList is null) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择允许 ChatGPT 打开的项目目录"
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var selectedPath = dialog.SelectedPath.Trim();
        if (string.IsNullOrWhiteSpace(selectedPath)) return;
        if (_devSpaceAllowedRootsList.Items.Cast<object>().Any(item =>
                string.Equals(item.ToString(), selectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            FooterStatus.Text = "这个目录已经在允许列表里了。";
            return;
        }

        _devSpaceAllowedRootsList.Items.Add(selectedPath);
        if (_devSpaceRemoveRootButton is not null)
        {
            _devSpaceRemoveRootButton.IsEnabled = true;
        }
        FooterStatus.Text = "已添加目录，记得点击保存。";
    }

    private void RemoveSelectedDevSpaceAllowedRoot()
    {
        if (_devSpaceAllowedRootsList?.SelectedItem is null)
        {
            FooterStatus.Text = "请先选择要删除的目录。";
            return;
        }

        _devSpaceAllowedRootsList.Items.Remove(_devSpaceAllowedRootsList.SelectedItem);
        if (_devSpaceRemoveRootButton is not null)
        {
            _devSpaceRemoveRootButton.IsEnabled = _devSpaceAllowedRootsList.Items.Count > 0;
        }
        FooterStatus.Text = "已移除目录，记得点击保存。";
    }

    private async Task CopyValueAsync(string value, WpfButton button)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("尚未", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("正在", StringComparison.OrdinalIgnoreCase))
        {
            FooterStatus.Text = "当前没有可复制的公网地址。";
            return;
        }

        if (_lastCopiedButton is not null && !ReferenceEquals(_lastCopiedButton, button))
        {
            _lastCopiedButton.Content = CopyButtonIcon();
        }

        _lastCopiedButton = button;
        button.Content = CheckButtonIcon();
        FooterStatus.Text = "正在复制公网地址...";

        try
        {
            await SetClipboardTextFastAsync(value);
            FooterStatus.Text = "已复制公网地址。";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"复制失败：{ex.Message}";
        }

        await Task.Delay(900);
        if (ReferenceEquals(_lastCopiedButton, button))
        {
            button.Content = CopyButtonIcon();
            _lastCopiedButton = null;
        }
    }

    private async Task SetClipboardTextFastAsync(string value)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                await Task.Run(() => SetClipboardTextWin32(value));
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(25);
            }
        }

        throw lastError ?? new InvalidOperationException("剪贴板暂时不可用，请稍后再试。");
    }

    private static void SetClipboardTextWin32(string value)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("剪贴板正被其他程序占用。");
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            handle = GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法分配剪贴板内存。");
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法写入剪贴板内存。");
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(13, handle) == IntPtr.Zero)
            {
                throw new InvalidOperationException("写入剪贴板失败。");
            }

            handle = IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    private FrameworkElement CopyButtonIcon() => SmallPathIcon(LucideCopyIcon, "#4D4D47", 15, 1.25);

    private FrameworkElement CheckButtonIcon() => SmallPathIcon(LucideCheckIcon, "#187352", 15, 1.35);

    private FrameworkElement CustomRow(string title, string detail, CheckVisualState state, FrameworkElement content, UIElement? trailing = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(ResultIcon(state));

        var copy = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = title, Foreground = BrushFrom("#20201D"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            copy.Children.Add(new TextBlock { Text = detail, Foreground = BrushFrom("#6F6F68"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        copy.Children.Add(content);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);

        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }

        return row;
    }

    private FrameworkElement SettingControlRow(string title, CheckVisualState state, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(ResultIcon(state));

        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = BrushFrom("#20201D"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 20, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 1);
        row.Children.Add(titleBlock);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        row.Children.Add(control);
        return row;
    }

    private FrameworkElement DirectoryListRow(FrameworkElement list, UIElement actions)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = 154 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(ResultIcon(CheckVisualState.Ok));

        var titleBlock = new TextBlock
        {
            Text = "允许访问目录",
            Foreground = BrushFrom("#20201D"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 20, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 1);
        row.Children.Add(titleBlock);

        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        list.Margin = new Thickness(8, 10, 0, 0);
        list.VerticalAlignment = VerticalAlignment.Top;
        list.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        Grid.SetRow(list, 1);
        Grid.SetColumn(list, 1);
        Grid.SetColumnSpan(list, 2);
        row.Children.Add(list);
        return row;
    }

    private FrameworkElement StatusOnlyRow(string title, string detail, bool? ok, CheckVisualState? forceState = null)
    {
        var state = forceState ?? (ok == true ? CheckVisualState.Ok : CheckVisualState.Fail);
        return ResultRow(title, detail, state, null);
    }

    private FrameworkElement DevSpaceStatusRow()
    {
        if (IsNativeModuleMismatch(_devSpaceStatus))
        {
            return ResultRow(
                "DevSpace 启动状态",
                "DevSpace 依赖与当前 Node 版本不匹配，请修复后再保存启动。",
                CheckVisualState.Fail,
                RepairDevSpaceDependencies,
                "修复");
        }

        if (File.Exists(AppPaths.DevSpaceAuthPath))
        {
            return StatusOnlyRow("DevSpace 启动状态", _devSpaceStatus, _devSpaceRunning);
        }

        return ResultRow(
            "DevSpace 启动状态",
            "未生成 Owner password，保存时会自动生成。",
            CheckVisualState.Fail,
            () =>
            {
                _app.AuthSecrets.EnsureOwnerPassword();
                FooterStatus.Text = "已生成 DevSpace Owner password，点击保存会重启服务。";
                RenderDevSpaceConfig();
            },
            "生成",
            afterActionStatus: "");
    }

    private FrameworkElement ResultRow(
        string title,
        string detail,
        CheckVisualState state,
        Action? action,
        string? secondaryActionText = null,
        Action? secondaryAction = null,
        string? afterActionStatus = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(ResultIcon(state));

        var copy = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = title, Foreground = BrushFrom("#20201D"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        copy.Children.Add(new TextBlock { Text = detail, Foreground = BrushFrom("#6F6F68"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);

        var visibleAction = secondaryAction ?? (state == CheckVisualState.Fail ? action : null);
        if (visibleAction is not null)
        {
            var button = new WpfButton
            {
                Content = secondaryActionText ?? "安装",
                Style = (Style)FindResource("InlineSecondaryButtonStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            button.Click += (_, _) =>
            {
                visibleAction();
                if (!string.IsNullOrEmpty(afterActionStatus))
                {
                    FooterStatus.Text = afterActionStatus;
                }
                else if (afterActionStatus is null)
                {
                    FooterStatus.Text = "已打开安装或初始化窗口，完成后请重新检测。";
                }
            };
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }

        return row;
    }

    private async Task SaveDevSpaceConfigurationAsync()
    {
        if (_devSpaceNodeVersionComboBox is null || _devSpacePortTextBox is null || _devSpaceAllowedRootsList is null)
        {
            FooterStatus.Text = "请先打开 DevSpace 配置页。";
            return;
        }

        var nodeVersion = _devSpaceNodeVersionComboBox.SelectedItem?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(nodeVersion))
        {
            FooterStatus.Text = "请选择 DevSpace 使用的 Node 版本。";
            return;
        }

        if (!int.TryParse(_devSpacePortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            FooterStatus.Text = "端口格式不正确，请输入 1 到 65535。";
            _devSpacePortTextBox.Focus();
            _devSpacePortTextBox.SelectAll();
            return;
        }

        try
        {
            SetBusyActions("保存中...");
            var roots = _devSpaceAllowedRootsList.Items
                .Cast<object>()
                .Select(item => item.ToString() ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            ApplyDevSpaceNodeVersion(nodeVersion);
            _app.PublicEndpoints.ApplyDevSpaceConfiguration(port, roots);
            EnsureDevSpaceOwnerToken();

            FooterStatus.Text = "已保存，正在重启 DevSpace...";
            ClearLogFile(_app.ConfigStore.Current.DevSpaceStderrLog);
            ClearLogFile(_app.ConfigStore.Current.DevSpaceStdoutLog);
            await Task.Run(() => _app.Processes.Restart(ProcessRole.DevSpace));
            await WaitForServiceStateAsync(ProcessRole.DevSpace, "DevSpace", requireHealth: true);
            await CheckDevSpaceInitInBackgroundAsync();
            _devSpaceRunning = await Task.Run(() => _app.Processes.IsRunning(ProcessRole.DevSpace));
            if (_devSpaceRunning == true)
            {
                _devSpaceStatus = $"已启动。本地地址：{_app.ConfigStore.Current.LocalHealthUrl}";
            }
            else
            {
                var recentError = ReadRecentLogSummary(_app.ConfigStore.Current.DevSpaceStderrLog);
                _devSpaceStatus = string.IsNullOrWhiteSpace(recentError)
                    ? "未启动"
                    : $"启动失败：{recentError}";
            }
            RenderDevSpaceConfig();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "环境配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RestoreActionButtons();
        }
    }

    private void EnsureDevSpaceOwnerToken()
    {
        var hadOwnerToken = !string.IsNullOrWhiteSpace(_app.AuthSecrets.ReadOwnerPassword());
        _app.AuthSecrets.EnsureOwnerPassword();
        if (!hadOwnerToken)
        {
            FooterStatus.Text = "已自动生成 DevSpace Owner password。";
        }
    }

    private void RepairDevSpaceDependencies()
    {
        try
        {
            ClearLogFile(_app.ConfigStore.Current.DevSpaceStderrLog);
            ClearLogFile(_app.ConfigStore.Current.DevSpaceStdoutLog);
            _app.Environment.RepairDevSpaceNativeDependencies();
            _devSpaceStatus = "修复窗口已打开。完成后点击保存重启。";
            _devSpaceRunning = false;
            FooterStatus.Text = "已打开 DevSpace 修复窗口。完成后请回到此页点击保存。";
            RenderDevSpaceConfig();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "环境配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyDevSpaceNodeVersion(string nodeVersion)
    {
        var nvmHome = ResolveNvmHome();
        var nodeDirectory = Path.Combine(nvmHome, $"v{nodeVersion.TrimStart('v', 'V')}");
        var nodePath = Path.Combine(nodeDirectory, "node.exe");
        var npmPath = Path.Combine(nodeDirectory, "npm");
        var devSpacePath = Path.Combine(nodeDirectory, "devspace");

        if (!File.Exists(nodePath))
        {
            throw new InvalidOperationException($"未找到 Node {nodeVersion}：{nodePath}");
        }

        if (!File.Exists(npmPath))
        {
            throw new InvalidOperationException($"Node {nodeVersion} 下未找到 npm，请先用 nvm 修复该版本。");
        }

        var config = _app.ConfigStore.Reload();
        config.NodeVersion = nodeVersion.TrimStart('v', 'V');
        config.NodeDirectory = nodeDirectory;
        config.NpmCommand = npmPath;
        config.DevSpaceCommand = devSpacePath;
        _app.ConfigStore.Save(config);

        if (!File.Exists(devSpacePath))
        {
            throw new InvalidOperationException($"已切换到 Node {config.NodeVersion}。该版本下未安装 DevSpace，请先安装 DevSpace。");
        }
    }

    private static IReadOnlyList<string> FindInstalledNodeVersions(string selectedVersion)
    {
        var versions = new List<string>();
        var nvmHome = ResolveNvmHome();
        if (Directory.Exists(nvmHome))
        {
            versions.AddRange(Directory.GetDirectories(nvmHome, "v*")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.TrimStart('v', 'V'))
                .OrderByDescending(VersionSortKey));
        }

        if (!string.IsNullOrWhiteSpace(selectedVersion) &&
            versions.All(version => !string.Equals(version, selectedVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase)))
        {
            versions.Insert(0, selectedVersion.TrimStart('v', 'V'));
        }

        return versions.Count == 0 ? [selectedVersion] : versions;
    }

    private static Version VersionSortKey(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static string ResolveNvmHome() =>
        Environment.GetEnvironmentVariable("NVM_HOME") ??
        Path.Combine(AppPaths.UserProfile, "AppData", "Local", "nvm");

    private async Task SaveCloudflareConfigurationAsync()
    {
        var current = _app.ConfigStore.Reload();
        var useTemporaryTunnel = current.UseTemporaryCloudflareTunnel;
        var fixedPublicBaseUrl = _cloudflarePublicBaseUrlTextBox?.Text.Trim() ?? current.FixedPublicBaseUrl;
        var tunnelName = _cloudflareTunnelNameTextBox?.Text.Trim() ?? current.CloudflareTunnelName;
        var protocol = _cloudflareProtocolComboBox?.SelectedItem?.ToString() ?? current.CloudflaredProtocol;

        if (_cloudflarePortTextBox is null ||
            !int.TryParse(_cloudflarePortTextBox.Text.Trim(), out var port) ||
            port is < 1 or > 65535)
        {
            FooterStatus.Text = "本地代理端口格式不正确，请输入 1 到 65535。";
            _cloudflarePortTextBox?.Focus();
            _cloudflarePortTextBox?.SelectAll();
            return;
        }

        if (!useTemporaryTunnel)
        {
            if (string.IsNullOrWhiteSpace(fixedPublicBaseUrl))
            {
                FooterStatus.Text = "固定域名模式需要填写公网域名。";
                _cloudflarePublicBaseUrlTextBox?.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(tunnelName))
            {
                FooterStatus.Text = "固定域名模式需要填写 Tunnel 名称。";
                _cloudflareTunnelNameTextBox?.Focus();
                return;
            }

            try
            {
                ValidateTunnelName(tunnelName);
                _app.PublicEndpoints.NormalizeBaseUrl(fixedPublicBaseUrl);
            }
            catch (Exception ex)
            {
                FooterStatus.Text = ex.Message;
                return;
            }
        }

        try
        {
            SetBusyActions("保存中...");
            var config = _app.PublicEndpoints.ApplyCloudflareConfiguration(
                useTemporaryTunnel,
                fixedPublicBaseUrl,
                ValidateTunnelNameOrCurrent(tunnelName, current.CloudflareTunnelName, useTemporaryTunnel),
                protocol,
                port);

            FooterStatus.Text = "Cloudflare 配置已保存。";
            if (!useTemporaryTunnel)
            {
                _latestCloudflareChecks.Clear();
                await CheckCloudflareInBackgroundAsync();
                if (!_cloudflarePassed)
                {
                    var setupProcess = _app.Environment.SetupCloudflareTunnel();
                    FooterStatus.Text = "已打开 Cloudflare 配置窗口。完成后关闭窗口，将自动重启并检测状态。";
                    if (setupProcess is not null)
                    {
                        await setupProcess.WaitForExitAsync();
                    }
                    _latestCloudflareChecks.Clear();
                    await CheckCloudflareInBackgroundAsync();
                }
            }

            FooterStatus.Text = "正在确认 DevSpace 本地服务...";
            await Task.Run(() => _app.Processes.Restart(ProcessRole.DevSpace));
            await WaitForServiceStateAsync(ProcessRole.DevSpace, "DevSpace", requireHealth: true);

            var local = await _app.Health.CheckLocalAsync();
            if (!local.Ok)
            {
                _cloudflareTunnelRunning = false;
                _cloudflareTunnelStatus = $"DevSpace 未启动，隧道未启动：{local.Message}";
                RenderCloudflare();
                return;
            }

            FooterStatus.Text = "正在重启 Cloudflare Tunnel...";
            await _app.Processes.RestartAsync(ProcessRole.CloudflareTunnel);
            await WaitForServiceStateAsync(ProcessRole.CloudflareTunnel, "Cloudflare Tunnel", requireHealth: !useTemporaryTunnel);
            RestoreActionButtons();
            _cloudflareTunnelRunning = await Task.Run(() => _app.Processes.IsRunning(ProcessRole.CloudflareTunnel));
            _cloudflareTunnelStatus = _cloudflareTunnelRunning == true ? "已启动" : "未启动";
            if (!useTemporaryTunnel)
            {
                _cloudflareAllowed = await CheckFixedCloudflareAllowedAsync();
                _cloudflareAllowedStatus = _cloudflareAllowed == true ? "允许访问" : "未允许访问，请保存补全配置后重试。";
            }
            RenderCloudflare();
            if (config.UseTemporaryCloudflareTunnel && !HasTemporaryUrl(_app.ConfigStore.Reload()))
            {
                FooterStatus.Text = "隧道已启动，正在等待 cloudflared 输出临时公网地址。";
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "环境配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RestoreActionButtons();
        }
    }

    private async Task WaitForServiceStateAsync(ProcessRole role, string label, bool requireHealth)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await Task.Delay(attempt == 1 ? 900 : 1200);
            var running = await Task.Run(() => _app.Processes.IsRunning(role));
            var config = _app.ConfigStore.Reload();
            var waitingForTemporaryUrl = role == ProcessRole.CloudflareTunnel && config.UseTemporaryCloudflareTunnel;
            var health = role == ProcessRole.DevSpace
                ? await _app.Health.CheckLocalAsync()
                : await _app.Health.CheckPublicAsync();

            if (running && waitingForTemporaryUrl && HasTemporaryUrl(config))
            {
                FooterStatus.Text = $"{label} 已启动，临时公网地址已同步。";
                return;
            }

            if (running && !waitingForTemporaryUrl && (!requireHealth || health.Ok))
            {
                FooterStatus.Text = $"{label} 已启动，状态检测通过。";
                return;
            }

            if (role == ProcessRole.DevSpace && !running && attempt >= 2)
            {
                var recentError = ReadRecentLogSummary(_app.ConfigStore.Current.DevSpaceStderrLog);
                if (recentError.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
                    recentError.Contains("devspace init", StringComparison.OrdinalIgnoreCase))
                {
                    _devSpaceRunning = false;
                    _devSpaceStatus = "缺少 Owner password，保存时会自动生成。";
                    FooterStatus.Text = "DevSpace 启动失败：缺少 Owner password，请重新保存生成配置。";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(recentError))
                {
                    if (IsNativeModuleMismatch(recentError))
                    {
                        _devSpaceRunning = false;
                        _devSpaceStatus = $"启动失败：{recentError}";
                        FooterStatus.Text = "DevSpace 依赖与当前 Node 版本不匹配，请点击修复。";
                        return;
                    }

                    FooterStatus.Text = $"DevSpace 启动后退出：{recentError}";
                    return;
                }
            }

            if (waitingForTemporaryUrl && running)
            {
                FooterStatus.Text = $"Cloudflare Tunnel 已启动，正在等待临时公网地址...（{attempt}/10）";
                continue;
            }

            FooterStatus.Text = $"正在检测 {label} 状态...（{attempt}/10）";
        }

        var finalRunning = await Task.Run(() => _app.Processes.IsRunning(role));
        FooterStatus.Text = finalRunning
            ? $"{label} 已启动，但健康检查暂未通过，请稍后刷新查看。"
            : $"{label} 尚未启动成功，请查看日志。";
    }

    private static string ReadRecentLogSummary(string path)
    {
        if (!File.Exists(path)) return "";

        try
        {
            return string.Join(" ", File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(6)
                .Select(line => line.Trim()))
                .Trim();
        }
        catch
        {
            return "";
        }
    }

    private static void ClearLogFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        catch
        {
            // Best-effort only; stale logs should not block service startup.
        }
    }

    private static bool IsNativeModuleMismatch(string text) =>
        text.Contains("NODE_MODULE_VERSION", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("better-sqlite3 could not load", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("compiled against a different Node", StringComparison.OrdinalIgnoreCase);

    private void SetBusyActions(string primaryText)
    {
        PrimaryActionButton.Content = primaryText;
        PrimaryActionButton.IsEnabled = false;
        SecondaryActionButton.IsEnabled = false;
    }

    private void RestoreActionButtons()
    {
        PrimaryActionButton.IsEnabled = !_fullCheckRunning;
        SecondaryActionButton.IsEnabled = !_fullCheckRunning;
        PrimaryActionButton.Content = _currentStep switch
        {
            SetupStep.Basic => "开始检测",
            _ => "保存"
        };
    }

    private static string ValidateTunnelName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("隧道名称不能为空。");
        }

        if (normalized.Length > 64)
        {
            throw new InvalidOperationException("隧道名称太长，请控制在 64 个字符以内。");
        }

        if (normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-')))
        {
            throw new InvalidOperationException("隧道名称只能包含小写字母、数字和短横线。");
        }

        if (normalized.StartsWith('-') || normalized.EndsWith('-') || normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("隧道名称不要以短横线开头或结尾，也不要连续使用短横线。");
        }

        return normalized;
    }

    private static string ValidateTunnelNameOrCurrent(string value, string current, bool useTemporaryTunnel) =>
        useTemporaryTunnel ? current : ValidateTunnelName(value);

    private static bool HasTemporaryUrl(ManagerConfig config) =>
        !config.TemporaryPublicBaseUrlPending &&
        config.PublicBaseUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase);

    private FrameworkElement ResultIcon(CheckVisualState state)
    {
        if (state == CheckVisualState.Fail)
        {
            var icon = new Grid
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Children.Add(new System.Windows.Shapes.Path
            {
                Fill = BrushFrom("#F2C94C"),
                Stroke = BrushFrom("#A86F00"),
                StrokeThickness = 0.45,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M21.73 18 13.73 4a2 2 0 0 0-3.46 0L2.27 18A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z")
            });
            icon.Children.Add(new System.Windows.Shapes.Path
            {
                Width = 7,
                Height = 10,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Stroke = BrushFrom("#5F4200"),
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M12 8v5 M12 17h.01")
            });

            return new Border
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Child = icon
            };
        }

        var path = new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("ResultIconPathStyle"),
            Stroke = state switch
            {
                CheckVisualState.Ok => BrushFrom("#187352"),
                _ => BrushFrom("#6F6F68")
            },
            Data = Geometry.Parse(state switch
            {
                CheckVisualState.Ok => "M12 22C17.52 22 22 17.52 22 12S17.52 2 12 2 2 6.48 2 12s4.48 10 10 10Z M8.5 12.5l2.2 2.2 4.8-5.2",
                _ => "M21 12a9 9 0 1 1-6.22-8.56"
            })
        };

        return new Border
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = path
        };
    }

    private Action? InstallActionFor(string checkName)
    {
        if (checkName.Contains("Git Bash", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallGit;
        if (checkName.Contains("nvm", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallNvm;
        if (checkName.Contains("Node", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallSelectedNode;
        if (checkName.Contains("npm", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallSelectedNode;
        if (checkName.Contains("DevSpace", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallDevSpace;
        if (checkName.Contains("cloudflared", StringComparison.OrdinalIgnoreCase)) return _app.Environment.InstallCloudflared;
        return null;
    }

    private async void PrimaryActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case SetupStep.Basic:
                await RunFullEnvironmentCheckAsync();
                break;
            case SetupStep.DevSpaceInit:
                await SaveDevSpaceConfigurationAsync();
                break;
            case SetupStep.Cloudflare:
                await SaveCloudflareConfigurationAsync();
                break;
        }
    }

    private async void SecondaryActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case SetupStep.Basic:
                InstallMissingBasic();
                break;
            case SetupStep.Cloudflare:
                await RefreshCloudflareAsync();
                break;
            case SetupStep.DevSpaceInit:
                await RefreshDevSpaceConfigAsync();
                break;
        }
    }

    private void InstallMissingBasic()
    {
        if (_latestBasicChecks.Count == 0)
        {
            FooterStatus.Text = "请先执行一次基础环境检测。";
            return;
        }

        var failed = _latestBasicChecks.FirstOrDefault(check => !check.Ok);
        if (failed is null)
        {
            FooterStatus.Text = "基础环境已就绪。";
            return;
        }

        var action = InstallActionFor(failed.Name);
        if (action is null)
        {
            FooterStatus.Text = $"暂不支持自动安装：{failed.Name}";
            return;
        }

        action();
        FooterStatus.Text = InstallGuidanceFor(failed.Name);
    }

    private void StartProcessAndRefresh(ProcessRole role, string label)
    {
        try
        {
            _app.Processes.Start(role);
            FooterStatus.Text = $"正在启动 {label}...";
            _ = RefreshCloudflareAfterStartAsync(label);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "环境配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshCloudflareAfterStartAsync(string label)
    {
        await Task.Delay(1200);
        await RefreshCloudflareAsync();
        FooterStatus.Text = $"{label} 状态已刷新。";
    }

    private static string TemporaryUrlDisplay(ManagerConfig config, bool tunnelRunning)
    {
        var url = config.PublicBaseUrl;
        if (HasTemporaryUrl(config)) return url;
        return tunnelRunning ? "临时地址获取中..." : "尚未获取临时地址";
    }

    private static string CurrentCloudflareAddressDisplay(ManagerConfig config, bool tunnelRunning)
    {
        if (config.UseTemporaryCloudflareTunnel)
        {
            return TemporaryUrlDisplay(config, tunnelRunning);
        }

        return string.IsNullOrWhiteSpace(config.PublicBaseUrl) ? "尚未配置固定公网地址" : config.PublicBaseUrl;
    }

    private static IReadOnlyList<EnvironmentCheck> NormalizeBasicChecks(IReadOnlyList<EnvironmentCheck> checks)
    {
        return
        [
            MergeBasicChecks("Git Bash", checks.Where(check => check.Name.Contains("Git Bash", StringComparison.OrdinalIgnoreCase)).ToList()),
            MergeBasicChecks("nvm for Windows", checks.Where(check => check.Name.Contains("nvm", StringComparison.OrdinalIgnoreCase)).ToList()),
            MergeBasicChecks("Node 运行时", checks.Where(check => check.Name.Contains("Node", StringComparison.OrdinalIgnoreCase)).ToList()),
            MergeBasicChecks("npm 命令", checks.Where(check => check.Name.Contains("npm", StringComparison.OrdinalIgnoreCase)).ToList()),
            MergeBasicChecks("cloudflared", checks.Where(check => check.Name.Contains("cloudflared", StringComparison.OrdinalIgnoreCase)).ToList()),
            MergeBasicChecks("DevSpace", checks.Where(check => check.Name.Contains("DevSpace", StringComparison.OrdinalIgnoreCase)).ToList())
        ];
    }

    private string InstallGuidanceFor(string checkName)
    {
        var config = _app.ConfigStore.Reload();
        if (checkName.Contains("nvm", StringComparison.OrdinalIgnoreCase))
        {
            return "已打开 nvm 安装窗口。安装完成后请重启应用或重新检测，再继续安装 Node。";
        }

        if (checkName.Contains("Node", StringComparison.OrdinalIgnoreCase) ||
            checkName.Contains("npm", StringComparison.OrdinalIgnoreCase))
        {
            return $"已打开 Node {config.NodeVersion} 安装窗口。安装完成后请重新检测。";
        }

        if (checkName.Contains("DevSpace", StringComparison.OrdinalIgnoreCase))
        {
            return "已打开 DevSpace 安装窗口。安装完成后请重新检测。";
        }

        if (checkName.Contains("cloudflared", StringComparison.OrdinalIgnoreCase))
        {
            return "已打开 cloudflared 安装窗口。安装完成后请重新检测。";
        }

        return "已打开安装窗口。完成后请重新检测。";
    }

    private static EnvironmentCheck MergeBasicChecks(string name, IReadOnlyList<EnvironmentCheck> checks)
    {
        if (checks.Count == 0)
        {
            return new EnvironmentCheck(name, false, "未执行检测。");
        }

        var failed = checks.FirstOrDefault(check => !check.Ok);
        if (failed is not null)
        {
            return new EnvironmentCheck(name, false, failed.Detail);
        }

        var version = checks.LastOrDefault(check =>
            check.Name.Contains("版本", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(check.Detail));
        return new EnvironmentCheck(name, true, version?.Detail ?? checks[0].Detail);
    }

    private bool CanOpenStep(SetupStep step)
    {
        if (!_orderedMode) return true;
        return step switch
        {
            SetupStep.Basic => true,
            SetupStep.Cloudflare => _basicPassed,
            SetupStep.DevSpaceInit => _basicPassed && _cloudflarePassed,
            _ => false
        };
    }

    private void RenderStepButtons()
    {
        ConfigureStepButton(BasicStepButton, SetupStep.Basic, "1  基础环境", _basicPassed);
        ConfigureStepButton(CloudflareStepButton, SetupStep.Cloudflare, "2  Cloudflare 配置", _cloudflarePassed);
        ConfigureStepButton(DevSpaceStepButton, SetupStep.DevSpaceInit, "3  DevSpace 配置", _devSpacePassed);
    }

    private void ConfigureStepButton(WpfButton button, SetupStep step, string label, bool passed)
    {
        button.Content = StepButtonContent(label, GetStepState(step, passed));
        button.IsEnabled = !_fullCheckRunning && CanOpenStep(step);
        button.Background = step == _currentStep ? BrushFrom("#E8E8E5") : System.Windows.Media.Brushes.Transparent;
    }

    private FrameworkElement StepButtonContent(string label, StepCheckState state)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFrom("#3B3B36"),
            FontSize = 13
        };
        grid.Children.Add(text);

        var icon = StepStateIcon(state);
        if (icon is not null)
        {
            icon.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }

        return grid;
    }

    private FrameworkElement? StepStateIcon(StepCheckState state)
    {
        return state switch
        {
            StepCheckState.Ok => SmallPathIcon(
                "M12 22C17.52 22 22 17.52 22 12S17.52 2 12 2 2 6.48 2 12s4.48 10 10 10Z M8.5 12.5l2.2 2.2 4.8-5.2",
                "#187352",
                16),
            StepCheckState.Fail => SmallWarningIcon(),
            StepCheckState.Progress => ProgressStepIcon(),
            _ => null
        };
    }

    private FrameworkElement SmallPathIcon(string data, string stroke, double size, double strokeThickness = 1.45)
    {
        return new System.Windows.Shapes.Path
        {
            Width = size,
            Height = size,
            Stroke = BrushFrom(stroke),
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = System.Windows.Media.Brushes.Transparent,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse(data),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private FrameworkElement SmallWarningIcon()
    {
        var icon = new Grid
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Children.Add(new System.Windows.Shapes.Path
        {
            Fill = BrushFrom("#F2C94C"),
            Stroke = BrushFrom("#A86F00"),
            StrokeThickness = 0.45,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M21.73 18 13.73 4a2 2 0 0 0-3.46 0L2.27 18A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z")
        });
        icon.Children.Add(new System.Windows.Shapes.Path
        {
            Width = 6,
            Height = 9,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = BrushFrom("#5F4200"),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = System.Windows.Media.Brushes.Transparent,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M12 8v5 M12 17h.01")
        });
        return icon;
    }

    private FrameworkElement ProgressStepIcon()
    {
        var icon = (System.Windows.Shapes.Path)SmallPathIcon("M21 12a9 9 0 1 1-6.22-8.56", "#6F6F68", 15);
        var rotate = new RotateTransform(0);
        icon.RenderTransform = rotate;
        icon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        rotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        return icon;
    }

    private StepCheckState GetStepState(SetupStep step, bool passed)
    {
        if (_stepStates.TryGetValue(step, out var state))
        {
            return state;
        }

        return passed ? StepCheckState.Ok : StepCheckState.Unknown;
    }

    private void SetStepState(SetupStep step, StepCheckState state)
    {
        _stepStates[step] = state;
        RenderStepButtons();
    }

    private void ResetStepStates()
    {
        foreach (var step in Enum.GetValues<SetupStep>())
        {
            _stepStates[step] = StepCheckState.Unknown;
        }

        RenderStepButtons();
    }

    private bool IsStepPassed(SetupStep step) =>
        step switch
        {
            SetupStep.Basic => _basicPassed,
            SetupStep.DevSpaceInit => _devSpacePassed,
            SetupStep.Cloudflare => _cloudflarePassed,
            _ => false
        };

    private async void BasicStepButton_OnClick(object sender, RoutedEventArgs e) => await ShowStepAsync(SetupStep.Basic, runCheck: false);
    private async void DevSpaceStepButton_OnClick(object sender, RoutedEventArgs e) => await ShowStepAsync(SetupStep.DevSpaceInit, runCheck: false);
    private async void CloudflareStepButton_OnClick(object sender, RoutedEventArgs e) => await ShowStepAsync(SetupStep.Cloudflare, runCheck: false);

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private static SolidColorBrush BrushFrom(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private void ApplyWindowDwmAttributes()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        var borderColor = 0x00BABABF;
        _ = DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        var ncrpEnabled = 2;
        _ = DwmSetWindowAttribute(handle, 2, ref ncrpEnabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private enum SetupStep
    {
        Basic,
        DevSpaceInit,
        Cloudflare
    }

    private enum CheckVisualState
    {
        Progress,
        Ok,
        Fail
    }

    private enum StepCheckState
    {
        Unknown,
        Progress,
        Ok,
        Fail
    }
}
