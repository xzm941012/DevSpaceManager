using DevSpaceManager.Core;
using DevSpaceManager.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfButton = System.Windows.Controls.Button;

namespace DevSpaceManager.UI;

public sealed partial class EnvironmentSetupWindow
{
    private readonly AppHost _app;
    private readonly bool _orderedMode;
    private readonly List<EnvironmentCheck> _latestBasicChecks = [];
    private readonly Dictionary<SetupStep, StepCheckState> _stepStates = new();
    private SetupStep _currentStep = SetupStep.Basic;
    private bool _basicPassed;
    private bool _devSpacePassed;
    private bool _cloudflarePassed;
    private bool _fullCheckRunning;

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
                StepTitle.Text = "DevSpace 初始化";
                StepDescription.Text = "生成 DevSpace 配置文件。初始化时会使用前一步 Cloudflare 配置确定 MCP 公网地址。";
                SecondaryActionButton.Content = "重新检测";
                PrimaryActionButton.Content = "运行 devspace init";
                await RenderDevSpaceInitAsync();
                break;
            case SetupStep.Cloudflare:
                StepTitle.Text = "Cloudflare 配置";
                StepDescription.Text = "先选择临时地址或固定域名地址。临时地址无需登录 Cloudflare；固定域名地址需要登录并配置 tunnel。";
                await RenderCloudflareAsync();
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
        _devSpacePassed = File.Exists(config.DevSpaceConfigPath);
        await Task.CompletedTask;
    }

    private async Task RenderCloudflareAsync()
    {
        ResultPanel.Children.Clear();
        var config = _app.ConfigStore.Reload();
        if (config.UseTemporaryCloudflareTunnel)
        {
            _cloudflarePassed = true;
            SecondaryActionButton.Content = "改用域名地址";
            PrimaryActionButton.Content = "重新检测";
            ResultPanel.Children.Add(ResultRow("地址模式", "临时地址。无需 Cloudflare 登录，每次启动会生成新的 trycloudflare.com 地址。", CheckVisualState.Ok, null, "使用域名地址", SwitchToDomainTunnel, ""));
            ResultPanel.Children.Add(ResultRow("Cloudflare 登录", "临时地址模式不需要登录。", CheckVisualState.Ok, null));
            ResultPanel.Children.Add(ResultRow("Tunnel 配置", "临时地址模式不需要固定 tunnel 配置。", CheckVisualState.Ok, null));
            FooterStatus.Text = "当前使用临时地址，适合临时调试。";
        }
        else
        {
            SecondaryActionButton.Content = "改用临时地址";
            PrimaryActionButton.Content = "一键配置 Cloudflare";
            var checks = (await _app.Environment.CheckInitializationAsync())
                .Where(check => check.Name.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                                check.Name.Contains("Tunnel", StringComparison.OrdinalIgnoreCase))
                .ToList();
            ResultPanel.Children.Add(ResultRow("地址模式", "固定域名地址。需要 Cloudflare 登录、tunnel 凭据和域名绑定。", CheckVisualState.Ok, null, "使用临时地址", SwitchToTemporaryTunnel, ""));
            foreach (var check in checks)
            {
                var action = check.Name.Contains("登录", StringComparison.OrdinalIgnoreCase)
                    ? _app.Environment.RunCloudflaredLogin
                    : (Action?)null;
                ResultPanel.Children.Add(ResultRow(check.Name, check.Detail, check.Ok ? CheckVisualState.Ok : CheckVisualState.Fail, action, "登录", action, "已打开 Cloudflare 登录窗口，完成后请重新检测。"));
            }

            _cloudflarePassed = checks.Count > 0 && checks.All(check => check.Ok);
            FooterStatus.Text = _cloudflarePassed ? "Cloudflare 配置已就绪。" : "固定域名地址配置未完成。";
        }

        RenderStepButtons();
        await Task.CompletedTask;
    }

    private async Task RenderDevSpaceInitAsync()
    {
        ResultPanel.Children.Clear();
        var config = _app.ConfigStore.Reload();
        var ok = File.Exists(config.DevSpaceConfigPath);
        _devSpacePassed = ok;
        ResultPanel.Children.Add(ResultRow("DevSpace 配置", ok ? config.DevSpaceConfigPath : $"缺失：{config.DevSpaceConfigPath}", ok ? CheckVisualState.Ok : CheckVisualState.Fail, _app.Environment.RunDevSpaceInit));
        FooterStatus.Text = ok ? "DevSpace 初始化已完成。" : "需要运行 devspace init。";
        RenderStepButtons();
        await Task.CompletedTask;
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
                _app.Environment.RunDevSpaceInit();
                FooterStatus.Text = "已打开 devspace init 窗口，完成后重新检测。";
                break;
            case SetupStep.Cloudflare:
                var config = _app.ConfigStore.Reload();
                if (config.UseTemporaryCloudflareTunnel)
                {
                    await RenderCloudflareAsync();
                }
                else
                {
                    _app.Environment.SetupCloudflareTunnel();
                    FooterStatus.Text = "已打开 Cloudflare 一键配置窗口，完成后重新检测。";
                }
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
            case SetupStep.DevSpaceInit:
                await RenderDevSpaceInitAsync();
                break;
            case SetupStep.Cloudflare:
                ToggleTemporaryTunnel();
                await RenderCloudflareAsync();
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

    private void EnableTemporaryTunnel()
    {
        var config = _app.ConfigStore.Reload();
        config.UseTemporaryCloudflareTunnel = true;
        _app.ConfigStore.Save(config);
    }

    private void DisableTemporaryTunnel()
    {
        var config = _app.ConfigStore.Reload();
        config.UseTemporaryCloudflareTunnel = false;
        _app.ConfigStore.Save(config);
        _ = RenderCloudflareAsync();
    }

    private void ToggleTemporaryTunnel()
    {
        var config = _app.ConfigStore.Reload();
        config.UseTemporaryCloudflareTunnel = !config.UseTemporaryCloudflareTunnel;
        _app.ConfigStore.Save(config);
    }

    private void SwitchToTemporaryTunnel()
    {
        EnableTemporaryTunnel();
        FooterStatus.Text = "已切换为临时地址模式。";
        _ = RenderCloudflareAsync();
    }

    private void SwitchToDomainTunnel()
    {
        DisableTemporaryTunnel();
        FooterStatus.Text = "已切换为固定域名地址模式。";
        _ = RenderCloudflareAsync();
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
        ConfigureStepButton(DevSpaceStepButton, SetupStep.DevSpaceInit, "3  DevSpace 初始化", _devSpacePassed);
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

    private FrameworkElement SmallPathIcon(string data, string stroke, double size)
    {
        return new System.Windows.Shapes.Path
        {
            Width = size,
            Height = size,
            Stroke = BrushFrom(stroke),
            StrokeThickness = 1.45,
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
