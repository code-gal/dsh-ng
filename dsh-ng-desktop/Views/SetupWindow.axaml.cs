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
        Closed += SetupWindow_OnClosed;
    }

    private SetupRuntime Runtime => _runtime ?? throw new InvalidOperationException("The installer runtime was not configured.");

    private async void SetupWindow_OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            _result = await Runtime.Coordinator.RunAsync().ConfigureAwait(true);
            ShowResult(_result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowResult(SetupResult.Failure(
                $"The installer could not start: {exception.Message}",
                "Open the installation log, correct the problem, then run the installer again."));
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
        CloseButton.IsVisible = true;
        if (result.Succeeded)
        {
            StageText.Text = "Installation complete";
            DetailText.Text = result.Summary;
            return;
        }

        StageText.Text = result.WasCancelled ? "Installation stopped" : "Installation failed";
        DetailText.Text = result.Summary;
        RemediationText.IsVisible = !string.IsNullOrWhiteSpace(result.Remediation);
        RemediationText.Text = result.Remediation;
    }

    private void StopButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        StopButton.IsEnabled = false;
        StageText.Text = "Stopping installation";
        DetailText.Text = "Cancelling the transaction and rolling back product-owned resources.";
        Runtime.Coordinator.RequestStop();
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
            DetailText.Text = "Diagnostic information was copied to the clipboard.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            DetailText.Text = $"Diagnostics could not be copied: {exception.Message}";
        }
    }

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var result = Runtime.Log.OpenLogFolder();
        if (!result.Succeeded)
        {
            DetailText.Text = $"The log folder could not be opened: {result.Error}";
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void SetupWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        Runtime.Coordinator.ProgressChanged -= Coordinator_OnProgressChanged;
        if (_result is { Succeeded: false })
        {
            _ = Runtime.Coordinator.FinalizeFailedInstallationAsync();
        }
    }
}
