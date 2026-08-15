using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Installer;

namespace DshNgDesktop.Views;

public partial class SetupWindow : Window
{
    private SetupRuntime? _runtime;
    private SetupResult? _result;
    private bool _stopRequested;
    private bool _installationStarted;

    public SetupWindow()
    {
        InitializeComponent();
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
        var existingDataState = SetupLocations.InspectExistingProductData(Runtime.Paths);
        if (existingDataState != ExistingProductDataState.None)
        {
            ShowExistingDataChoice(existingDataState);
            return;
        }

        await StartInstallationAsync().ConfigureAwait(true);
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
            _result = await Runtime.Coordinator.RunAsync().ConfigureAwait(true);
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
        StageText.Text = progress.Title;
        DetailText.Text = progress.Detail;
        LogSummary.Items.Add($"{DateTime.Now:T}  {progress.Title}: {progress.Detail}");
        while (LogSummary.Items.Count > 8)
        {
            LogSummary.Items.RemoveAt(0);
        }
    }

    private void ShowResult(SetupResult result)
    {
        _result = result;
        StageProgress.IsIndeterminate = false;
        StageProgress.Value = 100;
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

    private void ShowExistingDataChoice(ExistingProductDataState state)
    {
        StageProgress.IsIndeterminate = false;
        StageProgress.Value = 0;
        StopButton.IsVisible = false;
        CloseButton.IsVisible = true;
        PreserveDataButton.IsVisible = state != ExistingProductDataState.UnverifiedManifest;
        FreshInstallButton.IsVisible = true;
        RemediationText.IsVisible = false;

        switch (state)
        {
            case ExistingProductDataState.VerifiedInstallation:
                StageText.Text = "检测到已完成的安装";
                DetailText.Text = "覆盖安装会替换客户端文件并保留 DSH 配置、会话、插件、缓存和日志；全新安装会清理产品数据后重新安装，并保留当前安装日志供诊断。首次下载 DSH 依赖可能需要数分钟。";
                break;
            case ExistingProductDataState.InterruptedInstallation:
                StageText.Text = "检测到未完成的旧安装";
                DetailText.Text = "可覆盖安装并保留已有 DSH 数据，或选择全新安装以清理旧的 DSH Desktop 产品数据；当前安装日志会保留供诊断。首次下载 DSH 依赖可能需要数分钟。";
                break;
            default:
                StageText.Text = "检测到无法验证的安装清单";
                DetailText.Text = "为了避免覆盖来源不明的数据，只能选择全新安装；该操作会清理 DSH Desktop 的产品目录。";
                break;
        }
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
        ContinueButton.IsVisible = false;
        StopButton.IsEnabled = false;
        StageText.Text = "正在停止安装";
        DetailText.Text = "正在取消事务并回滚本次安装创建的产品资源。";
        Runtime.Coordinator.RequestStop();
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
        if (!_installationStarted || _result is not null || _stopRequested)
        {
            return;
        }

        eventArgs.Cancel = true;
        StageText.Text = "要停止安装吗？";
        DetailText.Text = "现在停止将取消安装，并回滚本次创建的产品文件和系统注册项。";
        StopButton.Content = "停止并回滚";
        ContinueButton.IsVisible = true;
        StopButton.Focus();
    }

    private void SetupWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        Runtime.Coordinator.ProgressChanged -= Coordinator_OnProgressChanged;
        Closing -= SetupWindow_OnClosing;
        if (_result is { Succeeded: false })
        {
            _ = Runtime.Coordinator.FinalizeFailedInstallationAsync();
        }
    }
}
