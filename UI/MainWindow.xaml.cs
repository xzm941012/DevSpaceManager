using DevSpaceManager.Core;
using DevSpaceManager.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevSpaceManager.UI;

public sealed partial class MainWindow
{
    private const double LoadingGlowCycleMs = 2400;
    private const double LoadingGlowInitialPhaseMs = 900;
    private const string ChromiumErrorPagePrefix = "chrome-error://chromewebdata/";
    private const double StartupWorkAreaMargin = 16;
    private static readonly TimeSpan InitialAnimationLeadTime = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan EnvironmentServiceStartupGrace = TimeSpan.FromSeconds(90);

    private readonly AppHost _app;
    private readonly Action? _showUpdateWindow;
    private SettingsWindow? _settingsWindow;
    private EnvironmentSetupWindow? _environmentSetupWindow;
    private WebView2? _chatGptView;
    private bool _chatGptEventsAttached;
    private bool _isInitialChatGptLoad = true;
    private bool _initialLoadStarted;
    private string _lastRequestedChatGptUri = "https://chatgpt.com";
    private readonly DispatcherTimer _loadingGlowTimer;
    private readonly DispatcherTimer _environmentDiagnosticTimer = new();
    private readonly Stopwatch _loadingGlowClock = new();
    private TaskCompletionSource? _initialLoadCompletion;
    private DateTimeOffset _environmentStartupGraceUntil = DateTimeOffset.Now.Add(EnvironmentServiceStartupGrace);

    internal MainWindow(AppHost app, Action? showUpdateWindow = null)
    {
        _app = app;
        _showUpdateWindow = showUpdateWindow;
        InitializeComponent();
        ChatGptHost.Visibility = System.Windows.Visibility.Collapsed;
        ChatGptLoadingOverlay.Visibility = System.Windows.Visibility.Visible;
        _loadingGlowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _loadingGlowTimer.Tick += (_, _) => UpdateLoadingGlow();
        _environmentDiagnosticTimer.Interval = TimeSpan.FromSeconds(60);
        _environmentDiagnosticTimer.Tick += async (_, _) => await RefreshEnvironmentDiagnosticAsync();
        ContentRendered += async (_, _) => await BeginInitialLoadAsync();
        Loaded += async (_, _) => await InitializeEnvironmentExperienceAsync();
        Loaded += (_, _) => FitWindowToCurrentWorkArea();
        SourceInitialized += (_, _) =>
        {
            ApplyWindowDwmAttributes();
            RegisterWindowSizingHook();
            FitWindowToCurrentWorkArea();
        };
    }

    private async Task InitializeEnvironmentExperienceAsync()
    {
        await RefreshEnvironmentDiagnosticAsync(allowStartupGrace: true);
        QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(8), allowStartupGrace: true);
        QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(20), allowStartupGrace: true);
        QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(40), allowStartupGrace: true);
        QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(70), allowStartupGrace: true);
        QueueEnvironmentDiagnosticRefresh(TimeSpan.FromSeconds(100));
        _environmentDiagnosticTimer.Start();
    }

    internal void QueueEnvironmentDiagnosticRefresh(TimeSpan delay, bool allowStartupGrace = false)
    {
        _ = RefreshEnvironmentDiagnosticAfterDelayAsync(delay, allowStartupGrace);
    }

    private async Task RefreshEnvironmentDiagnosticAfterDelayAsync(TimeSpan delay, bool allowStartupGrace)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        await RefreshEnvironmentDiagnosticAsync(allowStartupGrace);
    }

    internal async Task RefreshEnvironmentDiagnosticAsync(bool allowStartupGrace = false)
    {
        try
        {
            if (allowStartupGrace && DateTimeOffset.Now < _environmentStartupGraceUntil)
            {
                var immediate = await DiagnoseImmediateEnvironmentIssueAsync();
                if (!immediate.Ok)
                {
                    ShowEnvironmentDiagnostic(immediate);
                    return;
                }

                var config = _app.ConfigStore.Reload();
                var local = await _app.Health.CheckLocalAsync();
                var publicHealth = await _app.Health.CheckPublicAsync();
                var waitingForDevSpace = config.AutoStartDevSpace && !local.Ok;
                var waitingForTunnel = config.AutoStartTunnel &&
                                       !config.UseTemporaryCloudflareTunnel &&
                                       !publicHealth.Ok;
                if (waitingForDevSpace || waitingForTunnel)
                {
                    HideEnvironmentWarning("正在启动并检测服务");
                    return;
                }
            }

            var diagnostic = await _app.Environment.DiagnoseStartupAsync(_app.Health);
            ShowEnvironmentDiagnostic(diagnostic);
        }
        catch (Exception ex)
        {
            ShowEnvironmentDiagnostic(EnvironmentDiagnostic.Unhealthy($"环境检查失败：{ex.Message}"));
        }
    }

    private async Task<EnvironmentDiagnostic> DiagnoseImmediateEnvironmentIssueAsync()
    {
        var basic = await _app.Environment.CheckBasicEnvironmentAsync();
        var failedBasic = basic.FirstOrDefault(check => !check.Ok);
        if (failedBasic is not null)
        {
            return EnvironmentDiagnostic.Unhealthy($"基础环境异常：{failedBasic.Name} - {failedBasic.Detail}");
        }

        var init = await _app.Environment.CheckInitializationAsync();
        var failedInit = init.FirstOrDefault(check => !check.Ok);
        return failedInit is null
            ? EnvironmentDiagnostic.Healthy("基础环境和初始化配置已就绪。")
            : EnvironmentDiagnostic.Unhealthy($"初始化异常：{failedInit.Name} - {failedInit.Detail}");
    }

    private void ShowEnvironmentDiagnostic(EnvironmentDiagnostic diagnostic)
    {
        Dispatcher.Invoke(() =>
        {
            EnvironmentWarningButton.Visibility = diagnostic.Ok
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
            EnvironmentWarningToolTipText.Text = diagnostic.Ok
                ? "环境已就绪"
                : ShortEnvironmentTip(diagnostic.Message);
        });
    }

    private void HideEnvironmentWarning(string message)
    {
        Dispatcher.Invoke(() =>
        {
            EnvironmentWarningButton.Visibility = System.Windows.Visibility.Collapsed;
            EnvironmentWarningToolTipText.Text = message;
        });
    }

    private static string ShortEnvironmentTip(string message)
    {
        if (message.Contains("基础环境", StringComparison.OrdinalIgnoreCase))
        {
            return "基础环境缺失或异常";
        }

        if (message.Contains("初始化", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DevSpace", StringComparison.OrdinalIgnoreCase))
        {
            return "初始化配置未完成";
        }

        return "点击查看并处理";
    }

    private async Task BeginInitialLoadAsync()
    {
        if (_initialLoadStarted)
        {
            return;
        }

        _initialLoadStarted = true;
        ShowChatGptLoading();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(InitialAnimationLeadTime);
        await ReloadChatGptViewAsync();
    }

    internal async Task ReloadChatGptViewAsync()
    {
        if (_isInitialChatGptLoad)
        {
            _initialLoadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ShowChatGptLoading();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        var environmentRequest = await CreateChatGptEnvironmentRequestAsync();
        var env = await CoreWebView2Environment.CreateAsync(
            null,
            environmentRequest.UserDataFolder,
            environmentRequest.Options);
        ResetChatGptView();
        var chatGptView = EnsureChatGptView();
        await chatGptView.EnsureCoreWebView2Async(env);
        chatGptView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        chatGptView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        if (!_chatGptEventsAttached)
        {
            chatGptView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!IsChromiumErrorPage(args.Uri))
                {
                    _lastRequestedChatGptUri = args.Uri;
                }

                if (_isInitialChatGptLoad)
                {
                    ShowChatGptLoading();
                    return;
                }

                ShowChatGptNavigationGuard();
            };
            chatGptView.CoreWebView2.SourceChanged += (_, _) => ShowErrorOverlayIfCurrentSourceIsChromiumError();
            chatGptView.CoreWebView2.DOMContentLoaded += (_, _) => CompleteInitialChatGptLoadIfCurrentSourceIsReady();
            chatGptView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                UpdateNavigationButtons();
                if (!args.IsSuccess)
                {
                    ShowChatGptErrorOverlay();
                    return;
                }

                HideChatGptErrorOverlay();
                CompleteInitialChatGptLoadIfCurrentSourceIsReady();
            };
            chatGptView.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationButtons();
            chatGptView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternalNewWindowUri(args.Uri);
            };
            _chatGptEventsAttached = true;
        }
        _lastRequestedChatGptUri = "https://chatgpt.com";
        chatGptView.Source = new Uri(_lastRequestedChatGptUri);
        UpdateNavigationButtons();
    }

    private WebView2 EnsureChatGptView()
    {
        if (_chatGptView is not null)
        {
            return _chatGptView;
        }

        _chatGptView = new WebView2
        {
            Visibility = System.Windows.Visibility.Collapsed
        };
        ChatGptHost.Children.Add(_chatGptView);
        return _chatGptView;
    }

    private void ResetChatGptView()
    {
        if (_chatGptView is null)
        {
            _chatGptEventsAttached = false;
            return;
        }

        ChatGptHost.Children.Remove(_chatGptView);
        _chatGptView.Dispose();
        _chatGptView = null;
        _chatGptEventsAttached = false;
    }

    private Task<ChatGptEnvironmentRequest> CreateChatGptEnvironmentRequestAsync()
    {
        return Task.Run(() =>
        {
            var config = _app.ConfigStore.Reload();
            var profile = config.BrowserProfiles.FirstOrDefault(item =>
                string.Equals(item.Id, config.ActiveBrowserProfileId, StringComparison.OrdinalIgnoreCase)) ??
                          config.BrowserProfiles[0];
            Directory.CreateDirectory(profile.UserDataFolder);

            var options = new CoreWebView2EnvironmentOptions();
            var args = new List<string>
            {
                "--disable-features=msSmartScreenProtection"
            };
            if (!string.IsNullOrWhiteSpace(profile.ProxyServer))
            {
                args.Add($"--proxy-server={profile.ProxyServer}");
            }

            if (config.LocalDebugEnabled)
            {
                args.Add($"--remote-debugging-port={config.LocalDebugPort}");
                args.Add("--remote-allow-origins=http://127.0.0.1");
            }

            options.AdditionalBrowserArguments = string.Join(" ", args);
            return new ChatGptEnvironmentRequest(profile.UserDataFolder, options);
        });
    }

    private sealed record ChatGptEnvironmentRequest(
        string UserDataFolder,
        CoreWebView2EnvironmentOptions Options);

    private void ShowChatGptLoading()
    {
        Dispatcher.Invoke(() =>
        {
            ChatGptLoadingOverlay.Visibility = System.Windows.Visibility.Visible;
            ChatGptHost.Visibility = System.Windows.Visibility.Collapsed;
            if (_chatGptView is not null)
            {
                _chatGptView.Visibility = System.Windows.Visibility.Collapsed;
            }
            if (!_loadingGlowClock.IsRunning)
            {
                _loadingGlowClock.Restart();
            }
            if (!_loadingGlowTimer.IsEnabled)
            {
                _loadingGlowTimer.Start();
            }
            UpdateLoadingGlow();
        });
    }

    private void HideChatGptLoading()
    {
        Dispatcher.Invoke(() =>
        {
            if (IsCurrentSourceChromiumErrorPage())
            {
                ShowChatGptErrorOverlay();
                return;
            }

            ChromeNavigation.Visibility = System.Windows.Visibility.Visible;
            ChatGptHost.Visibility = System.Windows.Visibility.Visible;
            if (_chatGptView is not null)
            {
                _chatGptView.Visibility = System.Windows.Visibility.Visible;
            }
            ChatGptLoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;
            _loadingGlowTimer.Stop();
            _loadingGlowClock.Reset();
        });
    }

    private void ShowChatGptNavigationGuard()
    {
        Dispatcher.Invoke(() =>
        {
            ChatGptHost.Visibility = System.Windows.Visibility.Visible;
            if (_chatGptView is not null)
            {
                _chatGptView.Visibility = System.Windows.Visibility.Collapsed;
            }
            ChatGptErrorContent.Visibility = System.Windows.Visibility.Collapsed;
            ChatGptErrorOverlay.Visibility = System.Windows.Visibility.Visible;
        });
    }

    private void HideChatGptErrorOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            ChatGptErrorOverlay.Visibility = System.Windows.Visibility.Collapsed;
            ChatGptErrorContent.Visibility = System.Windows.Visibility.Collapsed;
            ChatGptHost.Visibility = System.Windows.Visibility.Visible;
            if (_chatGptView is not null)
            {
                _chatGptView.Visibility = System.Windows.Visibility.Visible;
            }
        });
    }

    private void ShowChatGptErrorOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            ChromeNavigation.Visibility = System.Windows.Visibility.Visible;
            ChatGptLoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;
            ChatGptHost.Visibility = System.Windows.Visibility.Visible;
            if (_chatGptView is not null)
            {
                _chatGptView.Visibility = System.Windows.Visibility.Collapsed;
            }
            ChatGptErrorOverlay.Visibility = System.Windows.Visibility.Visible;
            ChatGptErrorContent.Visibility = System.Windows.Visibility.Visible;
            _loadingGlowTimer.Stop();
            _loadingGlowClock.Reset();
        });
    }

    private void UpdateErrorOverlayForCurrentSource()
    {
        if (IsCurrentSourceChromiumErrorPage())
        {
            ShowChatGptErrorOverlay();
            return;
        }

        if (!_isInitialChatGptLoad)
        {
            HideChatGptErrorOverlay();
        }
    }

    private void ShowErrorOverlayIfCurrentSourceIsChromiumError()
    {
        if (IsCurrentSourceChromiumErrorPage())
        {
            ShowChatGptErrorOverlay();
        }
    }

    private bool IsCurrentSourceChromiumErrorPage()
    {
        return IsChromiumErrorPage(_chatGptView?.CoreWebView2?.Source);
    }

    private static bool IsChromiumErrorPage(string? source)
    {
        return source is not null &&
               source.StartsWith(ChromiumErrorPagePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private void CompleteInitialChatGptLoadIfCurrentSourceIsReady()
    {
        if (!_isInitialChatGptLoad)
        {
            return;
        }

        if (IsCurrentSourceChromiumErrorPage())
        {
            ShowChatGptErrorOverlay();
            return;
        }

        _isInitialChatGptLoad = false;
        HideChatGptLoading();
        _initialLoadCompletion?.TrySetResult();
    }

    private void UpdateLoadingGlow()
    {
        var elapsedMs = _loadingGlowClock.Elapsed.TotalMilliseconds + LoadingGlowInitialPhaseMs;
        var progress = (elapsedMs % LoadingGlowCycleMs) / LoadingGlowCycleMs;
        var eased = 0.5 - Math.Cos(progress * Math.PI) / 2;
        if (LoadingGlowBand.Fill is LinearGradientBrush brush &&
            brush.RelativeTransform is TranslateTransform transform)
        {
            transform.X = -0.88 + eased * 1.76;
        }
    }

    private void RefreshButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ReloadCurrentPage();
    }

    private void BackButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_chatGptView?.CoreWebView2?.CanGoBack == true)
        {
            _chatGptView.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_chatGptView?.CoreWebView2?.CanGoForward == true)
        {
            _chatGptView.CoreWebView2.GoForward();
        }
    }

    private void EnvironmentWarningButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowEnvironmentSetup(orderedMode: false);
    }

    internal void SetUpdateAvailable(bool hasUpdate, string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateAvailableButton.Visibility = hasUpdate
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            UpdateAvailableToolTipText.Text = string.IsNullOrWhiteSpace(message)
                ? "点击打开检查更新"
                : message;
        });
    }

    private void UpdateAvailableButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        _showUpdateWindow?.Invoke();
    }

    private void SettingsMenuItem_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_app, ReloadChatGptViewAsync);
        }

        PositionSettingsWindow();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowEnvironmentSetup(bool orderedMode)
    {
        if (_environmentSetupWindow is null || !_environmentSetupWindow.IsLoaded)
        {
            _environmentSetupWindow = new EnvironmentSetupWindow(_app, orderedMode)
            {
                Owner = this
            };
            _environmentSetupWindow.Closed += (_, _) =>
            {
                _environmentSetupWindow = null;
                _ = RefreshEnvironmentDiagnosticAsync();
            };
        }

        PositionEnvironmentSetupWindow();
        _environmentSetupWindow.Show();
        _environmentSetupWindow.Activate();
    }

    private void PositionEnvironmentSetupWindow()
    {
        if (_environmentSetupWindow is null)
        {
            return;
        }

        _environmentSetupWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        _environmentSetupWindow.Left = Left + Math.Max(0, (ActualWidth - _environmentSetupWindow.Width) / 2);
        _environmentSetupWindow.Top = Top + Math.Max(0, (ActualHeight - _environmentSetupWindow.Height) / 2);
    }

    private void PositionSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        _settingsWindow.Left = Left + Math.Max(0, (ActualWidth - _settingsWindow.Width) / 2);
        _settingsWindow.Top = Top + Math.Max(0, (ActualHeight - _settingsWindow.Height) / 2);
    }

    private void ReloadCurrentPage()
    {
        if (_chatGptView?.CoreWebView2 is null)
        {
            _ = ReloadChatGptViewAsync();
            return;
        }

        ShowChatGptNavigationGuard();
        if (IsChromiumErrorPage(_chatGptView.CoreWebView2.Source))
        {
            _chatGptView.CoreWebView2.Navigate(_lastRequestedChatGptUri);
            return;
        }

        _chatGptView.CoreWebView2.Reload();
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _chatGptView?.CoreWebView2?.CanGoBack == true;
        ForwardButton.IsEnabled = _chatGptView?.CoreWebView2?.CanGoForward == true;
    }

    private void RefreshMenuItem_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ReloadCurrentPage();
    }

    private void OpenChatGptMenuItem_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowChatGptNavigationGuard();
        _lastRequestedChatGptUri = "https://chatgpt.com";
        _chatGptView?.CoreWebView2?.Navigate(_lastRequestedChatGptUri);
    }

    private void EnvironmentSetupMenuItem_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowEnvironmentSetup(orderedMode: false);
    }

    private static void OpenExternalNewWindowUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            Log.App($"Ignored WebView2 new-window request with unsupported URI: {uri}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.App($"Failed to open WebView2 new-window URI in default browser: {parsed.AbsoluteUri} - {ex}");
        }
    }

    private void ErrorRefreshButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ReloadCurrentPage();
    }

    private void ExitMenuItem_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.Forms.Application.Exit();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        WindowState = System.Windows.WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _environmentDiagnosticTimer.Stop();
        ResetChatGptView();
        _settingsWindow?.ForceClose();
        _settingsWindow = null;
        _environmentSetupWindow?.Close();
        _environmentSetupWindow = null;
        base.OnClosed(e);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;
        MaximizeIcon.Data = Geometry.Parse(WindowState == System.Windows.WindowState.Maximized
            ? "M8 8h10v10H8z M5 5h10v3 M5 5v10h3"
            : "M5 5h14v14H5z");
    }

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

    private void RegisterWindowSizingHook()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (message == wmGetMinMaxInfo)
        {
            ApplyMonitorWorkAreaMaxSize(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void FitWindowToCurrentWorkArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var workWidth = bottomRight.X - topLeft.X;
        var workHeight = bottomRight.Y - topLeft.Y;

        var maxWidth = Math.Max(520, workWidth - StartupWorkAreaMargin * 2);
        var maxHeight = Math.Max(420, workHeight - StartupWorkAreaMargin * 2);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
        Left = topLeft.X + Math.Max(StartupWorkAreaMargin, (workWidth - Width) / 2);
        Top = topLeft.Y + Math.Max(StartupWorkAreaMargin, (workHeight - Height) / 2);
    }

    private static void ApplyMonitorWorkAreaMaxSize(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo();
        monitorInfo.Size = Marshal.SizeOf<MonitorInfo>();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.Monitor;
        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize.X = minMaxInfo.MaxSize.X;
        minMaxInfo.MaxTrackSize.Y = minMaxInfo.MaxSize.Y;
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public int Flags;
    }
}
