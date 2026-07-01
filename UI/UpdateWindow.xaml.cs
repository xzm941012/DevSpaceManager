using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DevSpaceManager.Core;
using DevSpaceManager.Services;

namespace DevSpaceManager.UI;

public sealed partial class UpdateWindow
{
    private readonly AppHost _app;
    private bool _busy;
    private bool _devSpaceHasUpdate;
    private bool _cloudflaredHasUpdate;
    private string _devSpaceUpdateLabel = "";
    private string _cloudflaredUpdateLabel = "";

    internal event EventHandler<UpdateAvailabilityChangedEventArgs>? UpdateStatusChanged;

    internal UpdateWindow(AppHost app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowDwmAttributes();
        Loaded += async (_, _) => await CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync(bool force = false)
    {
        if (_busy && !force) return;
        SetBusy(true, "正在检查更新...");
        try
        {
            var devSpaceTask = _app.Updates.CheckDevSpaceAsync();
            var cloudflaredTask = _app.Updates.CheckCloudflaredAsync();
            await Task.WhenAll(devSpaceTask, cloudflaredTask);

            ApplyDevSpaceInfo(await devSpaceTask);
            ApplyCloudflaredInfo(await cloudflaredTask);
            StatusText.Text = _devSpaceHasUpdate || _cloudflaredHasUpdate
                ? "发现可更新组件。"
                : "所有组件都已是最新状态。";
            RaiseUpdateStatusChanged();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检查失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyDevSpaceInfo(UpdateInfo info)
    {
        _devSpaceHasUpdate = info.HasUpdate;
        _devSpaceUpdateLabel = info.HasUpdate
            ? $"DevSpace {info.CurrentVersion} -> {info.LatestVersion}"
            : "";
        DevSpaceVersionText.Text = $"当前版本：{info.CurrentVersion}    最新版本：{info.LatestVersion}";
        DevSpaceNotesText.Text = info.Notes;
        UpdateDevSpaceButton.IsEnabled = info.HasUpdate;
    }

    private void ApplyCloudflaredInfo(ToolVersionInfo info)
    {
        _cloudflaredHasUpdate = info.HasUpdate;
        _cloudflaredUpdateLabel = info.HasUpdate
            ? $"cloudflared {info.CurrentVersion} -> {info.LatestVersion}"
            : "";
        CloudflaredVersionText.Text = $"当前版本：{info.CurrentVersion}    最新版本：{info.LatestVersion}";
        CloudflaredNotesText.Text = info.Notes;
        UpdateCloudflaredButton.IsEnabled = info.HasUpdate;
    }

    private void RaiseUpdateStatusChanged()
    {
        var available = new[] { _devSpaceUpdateLabel, _cloudflaredUpdateLabel }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        var message = string.Join("；", available);
        UpdateStatusChanged?.Invoke(
            this,
            new UpdateAvailabilityChangedEventArgs(
                available.Length > 0,
                message,
                string.Join("|", available)));
    }

    private async Task RunUpdateAsync(string name, Func<IProgress<string>, Task> update)
    {
        if (_busy) return;
        SetBusy(true, $"正在更新 {name}...");
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await update(progress);
            StatusText.Text = $"{name} 更新完成，正在重新检查版本...";
            await CheckUpdatesAsync(force: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{name} 更新失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        UpdateDevSpaceButton.IsEnabled = !busy && _devSpaceHasUpdate;
        UpdateCloudflaredButton.IsEnabled = !busy && _cloudflaredHasUpdate;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private async void CheckButton_OnClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async void UpdateDevSpaceButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunUpdateAsync("DevSpace", progress => _app.Updates.UpdateDevSpaceAsync(progress));

    private async void UpdateCloudflaredButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunUpdateAsync("cloudflared", progress => _app.Updates.UpdateCloudflaredAsync(progress));

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

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
}

internal sealed record UpdateAvailabilityChangedEventArgs(
    bool HasUpdate,
    string ToolTipMessage,
    string NotificationVersion);
