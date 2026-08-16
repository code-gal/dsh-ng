using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshNgDesktop.Core;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Installer;

namespace DshNgDesktop.Views;

public partial class SetupWindow : Window
{
    private SetupRuntime? _runtime;
    private SetupResult? _result;
    private bool _stopRequested;
    private bool _installationStarted;
    private readonly List<string> _summaryLines = [];
    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _webUiWaitStartedAt;
    private bool _followLogTail = true;
    private bool _updatingLogScroll;
    private string? _lastSummaryLine;

    public SetupWindow()
    {
        InitializeComponent();
        _waitTimer.Tick += WaitTimer_OnTick;
    }

    public SetupWindow(SetupRuntime runtime)
        : this()
    {
        _runtime = runtime;
        _runtime.Coordinator.ProgressChanged += Coordinator_OnProgressChanged;
        Opened += SetupWindow_OnOpened;
        Closing += SetupWindow_OnClosing;
        Closed += SetupWindow_OnClosed;
    }

    public event EventHandler? InstallationCommitted;

    private SetupRuntime Runtime => _runtime ?? throw new InvalidOperationException("The installer runtime was not configured.");

    private async void SetupWindow_OnOpened(object? sender, EventArgs eventArgs)
    {
        StageText.Text = "正在检查现有安装";
        DetailText.Text = "正在读取本地安装清单、版本和构建形态。";
        StopButton.IsVisible = false;
        CloseButton.IsVisible = true;
        try
        {
            // Native AOT can spend observable time initializing source-
            // generated JSON metadata. Never synchronously wait for that work
            // from the Avalonia Opened callback.
            var inspection = await Task.Run(InspectExistingInstallation).ConfigureAwait(true);
            if (inspection.State != ExistingProductDataState.None)
            {
                ShowExistingDataChoice(inspection.State, inspection.Change);
                return;
            }

            await StartInstallationAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ShowResult(SetupResult.Failure(
                $"无法读取现有安装状态：{exception.Message}",
                "请关闭安装器后重试；当前安装和 DSH 数据未被修改。"));
        }
    }

    private async Task StartInstallationAsync()
    {
        _installationStarted = true;
        PreserveDataButton.IsVisible = false;
        FreshInstallButton.IsVisible = false;
        CloseButton.IsVisible = false;
        StopButton.IsVisible = true;
        StageProgress.IsIndeterminate = true;
        try
        {
            // Cross-flavor replacement can spend several seconds in native
            // process shutdown and directory operations. Keep every part of
            // the transaction off the Avalonia UI thread so the window can
            // continue repainting and accept a stop request.
            _result = await Task.Run(() => Runtime.Coordinator.RunAsync()).ConfigureAwait(true);
            ShowResult(_result);
        }
        catch (Exception exception)
        {
            ShowResult(SetupResult.Failure(
                $"安装器无法启动：{exception.Message}",
                "请打开安装日志，处理问题后重新运行安装器。"));
        }
    }

    private void Coordinator_OnProgressChanged(object? sender, SetupProgress progress)
    {
        Dispatcher.UIThread.Post(() => ShowProgress(progress));
    }

    private void ShowProgress(SetupProgress progress)
    {
        if (!progress.IsHeartbeat)
        {
            StageText.Text = progress.Title;
            DetailText.Text = progress.Detail;
        }

        if (progress.Stage == SetupStage.WaitingForWebUi && !_stopRequested)
        {
            _webUiWaitStartedAt ??= DateTimeOffset.UtcNow;
            ElapsedText.IsVisible = true;
            _waitTimer.Start();
            UpdateWaitElapsedText();
        }
        else
        {
            StopWaitTimer();
        }

        var summaryLine = $"{progress.Title}: {progress.Detail}";
        if (!progress.IsHeartbeat && !string.Equals(_lastSummaryLine, summaryLine, StringComparison.Ordinal))
        {
            _lastSummaryLine = summaryLine;
            AppendSummaryLine(summaryLine);
        }
    }

    private void ShowResult(SetupResult result)
    {
        _result = result;
        StageProgress.IsIndeterminate = false;
        StageProgress.Value = 100;
        StopWaitTimer();
        StopButton.IsVisible = false;
        PreserveDataButton.IsVisible = false;
        FreshInstallButton.IsVisible = false;
        CloseButton.IsVisible = true;
        if (result.Succeeded)
        {
            StageText.Text = "安装完成";
            DetailText.Text = result.Summary;
            InstallationCommitted?.Invoke(this, EventArgs.Empty);
            return;
        }

        StageText.Text = result.WasCancelled ? "安装已停止" : "安装失败";
        DetailText.Text = result.Summary;
        RemediationText.IsVisible = !string.IsNullOrWhiteSpace(result.Remediation);
        RemediationText.Text = result.Remediation;
    }

    private void ShowExistingDataChoice(ExistingProductDataState state, InstallChangeClassification change)
    {
        StageProgress.IsIndeterminate = false;
        StageProgress.Value = 0;
        StopButton.IsVisible = false;
        CloseButton.IsVisible = true;
        PreserveDataButton.IsVisible = state != ExistingProductDataState.UnverifiedManifest;
        FreshInstallButton.IsVisible = true;
        RemediationText.IsVisible = false;
        PreserveDataButton.Content = change.ActionText;

        switch (state)
        {
            case ExistingProductDataState.VerifiedInstallation:
                StageText.Text = "检测到已完成的安装";
                DetailText.Text = $"{change.DetailText} 全新安装会清理产品数据后重新安装，并保留当前安装日志供诊断。首次下载 DSH 依赖可能需要数分钟。";
                break;
            case ExistingProductDataState.InterruptedInstallation:
                StageText.Text = "检测到未完成的旧安装";
                DetailText.Text = $"{change.DetailText} 也可选择全新安装以清理旧的 DSH Desktop 产品数据；当前安装日志会保留供诊断。首次下载 DSH 依赖可能需要数分钟。";
                break;
            default:
                StageText.Text = "检测到无法验证的安装清单";
                DetailText.Text = "为了避免覆盖来源不明的数据，只能选择全新安装；该操作会清理 DSH Desktop 的产品目录。";
                break;
        }
    }

    private ExistingInstallationInspection InspectExistingInstallation()
    {
        var state = SetupLocations.InspectExistingProductData(Runtime.Paths);
        var change = state == ExistingProductDataState.None
            ? InstallPackageClassifier.Classify(null, Runtime.Coordinator.PackageMetadata)
            : GetInstallChangeClassification();
        return new ExistingInstallationInspection(state, change);
    }

    private InstallChangeClassification GetInstallChangeClassification()
    {
        InstallPackageMetadata? installed = null;
        try
        {
            var manifest = InstallManifest.LoadAsync(Runtime.Paths).GetAwaiter().GetResult();
            if (manifest is not null)
            {
                installed = new InstallPackageMetadata(manifest.ProductVersion, manifest.BuildFlavor);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // The state picker already treats this as an unverified manifest.
        }

        return InstallPackageClassifier.Classify(installed, Runtime.Coordinator.PackageMetadata);
    }

    private async void PreserveDataButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        PreserveDataButton.IsEnabled = false;
        FreshInstallButton.IsEnabled = false;
        Runtime.Coordinator.SelectExistingDataHandling(ExistingDataHandling.ReplaceClientPreservingData);
        await StartInstallationAsync().ConfigureAwait(true);
    }

    private async void FreshInstallButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        FreshInstallButton.IsEnabled = false;
        PreserveDataButton.IsEnabled = false;
        Runtime.Coordinator.SelectExistingDataHandling(ExistingDataHandling.FreshInstall);
        await StartInstallationAsync().ConfigureAwait(true);
    }

    private void StopButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        StopWaitTimer();
        ContinueButton.IsVisible = false;
        StopButton.IsEnabled = false;
        StageText.Text = "正在停止安装";
        DetailText.Text = "正在取消事务并回滚本次安装创建的产品资源。";
        Runtime.Coordinator.RequestStop();
    }

    private void LogSummary_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        _followLogTail = false;
        UpdateLogFollowHint();
    }

    private void LogSummaryScroller_OnScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (_updatingLogScroll)
        {
            return;
        }

        _followLogTail = IsLogAtEnd();
        UpdateLogFollowHint();
    }

    private void FollowLogButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _followLogTail = true;
        LogSummary.ClearSelection();
        RefreshSummaryLog();
        ScrollSummaryToEnd();
        UpdateLogFollowHint();
    }

    private void ContinueButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        ContinueButton.IsVisible = false;
        StopButton.Content = "停止安装";
        StageText.Text = "安装继续进行";
        DetailText.Text = "安装事务仍在运行。";
    }

    private async void CopyDiagnosticsButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var details = new List<string>();
            if (_result is not null)
            {
                details.Add(_result.Summary);
                if (!string.IsNullOrWhiteSpace(_result.Remediation))
                {
                    details.Add(_result.Remediation);
                }
            }

            await DiagnosticClipboard.CopyAsync(this, Runtime.Log.CreateCopyableDiagnosticText(Runtime.Coordinator.State, details)).ConfigureAwait(true);
            DetailText.Text = "诊断信息已复制到剪贴板。";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            DetailText.Text = $"无法复制诊断信息：{exception.Message}";
        }
    }

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var result = Runtime.Log.OpenLogFolder();
        if (!result.Succeeded)
        {
            DetailText.Text = $"无法打开日志目录：{result.Error}";
        }
    }

    private void OpenInstallLocationButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var result = SetupLocations.OpenInstallLocation(Runtime.Paths);
        DetailText.Text = result.Succeeded
            ? $"已打开安装位置：{result.OpenedPath}"
            : $"无法打开安装位置：{result.Error}";
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void SetupWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (!_installationStarted || _result is not null)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_stopRequested)
        {
            StageText.Text = Runtime.Coordinator.State.State == DshNgDesktop.Core.ApplicationState.RollingBack
                ? "正在回滚安装"
                : "正在停止安装";
            DetailText.Text = "正在清理本次安装创建的产品资源。回滚完成并显示结果前无法关闭此窗口。";
            return;
        }

        StageText.Text = "要停止安装吗？";
        DetailText.Text = "现在停止将取消安装，并回滚本次创建的产品文件和系统注册项。";
        StopButton.Content = "停止并回滚";
        ContinueButton.IsVisible = true;
        StopButton.Focus();
    }

    private void SetupWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        _waitTimer.Stop();
        Runtime.Coordinator.ProgressChanged -= Coordinator_OnProgressChanged;
        Closing -= SetupWindow_OnClosing;
        if (_result is { Succeeded: false })
        {
            _ = Runtime.Coordinator.FinalizeFailedInstallationAsync();
        }
    }

    private void AppendSummaryLine(string line)
    {
        _summaryLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_summaryLines.Count > 250)
        {
            _summaryLines.RemoveAt(0);
        }

        if (!_followLogTail)
        {
            UpdateLogFollowHint();
            return;
        }

        RefreshSummaryLog();
        ScrollSummaryToEnd();
    }

    private void RefreshSummaryLog() => LogSummary.Text = string.Join(Environment.NewLine, _summaryLines);

    private void ScrollSummaryToEnd()
    {
        _updatingLogScroll = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                LogSummaryScroller.ScrollToEnd();
                _updatingLogScroll = false;
            },
            DispatcherPriority.Render);
    }

    private bool IsLogAtEnd() =>
        LogSummaryScroller.Offset.Y >= Math.Max(0, LogSummaryScroller.Extent.Height - LogSummaryScroller.Viewport.Height - 2);

    private void UpdateLogFollowHint() => LogFollowHint.IsVisible = !_followLogTail;

    private void WaitTimer_OnTick(object? sender, EventArgs eventArgs) => UpdateWaitElapsedText();

    private void UpdateWaitElapsedText()
    {
        if (_webUiWaitStartedAt is not { } startedAt)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var display = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟"
            : elapsed.TotalMinutes >= 1
                ? $"{elapsed.Minutes} 分钟 {elapsed.Seconds} 秒"
                : $"{elapsed.Seconds} 秒";
        ElapsedText.Text = elapsed >= TimeSpan.FromMinutes(2)
            ? $"已等待 {display} · 网络较慢时可继续等待或停止安装"
            : $"已等待 {display} · 可随时停止安装";
    }

    private void StopWaitTimer()
    {
        _waitTimer.Stop();
        _webUiWaitStartedAt = null;
        ElapsedText.IsVisible = false;
    }

    private sealed record ExistingInstallationInspection(
        ExistingProductDataState State,
        InstallChangeClassification Change);
}
