using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using DshNgDesktop.Core;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Platform;
using System.ComponentModel;

namespace DshNgDesktop.Views;

public partial class MainWindow : Window
{
    private DesktopRuntime? _runtime;
    private NativeWebView? _webView;
    private bool _closingForExit;
    private bool _windowPresented;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(DesktopRuntime runtime)
        : this()
    {
        _runtime = runtime;
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        Runtime.Coordinator.SnapshotChanged += Coordinator_OnSnapshotChanged;
        ApplySnapshot(Runtime.Coordinator.Snapshot);
    }

    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        _windowPresented = true;
        ApplySnapshot(Runtime.Coordinator.Snapshot);
        Activate();
    }

    public void HideToTray()
    {
        _windowPresented = false;
        DestroyWebView();
        Hide();
    }

    public void DestroyWebViewForExit()
    {
        _closingForExit = true;
        _windowPresented = false;
        DestroyWebView();
    }

    private void Coordinator_OnSnapshotChanged(object? sender, DesktopRuntimeSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(DesktopRuntimeSnapshot snapshot)
    {
        switch (snapshot.Status)
        {
            case DesktopRuntimeStatus.Ready when snapshot.WebUiUri is not null:
                if (_windowPresented)
                {
                    ShowWebUi(snapshot.WebUiUri);
                }
                else
                {
                    ShowNativeStatus("DSH 正在后台运行", "可从托盘图标打开 DSH。", showActions: false);
                }
                break;
            case DesktopRuntimeStatus.Faulted:
                DestroyWebView();
                ShowNativeStatus(snapshot.ActivityTitle ?? "DSH 需要处理", snapshot.Detail, showActions: true);
                break;
            case DesktopRuntimeStatus.Stopping:
                DestroyWebView();
                ShowNativeStatus("正在停止 DSH", snapshot.Detail, showActions: false);
                break;
            case DesktopRuntimeStatus.Stopped:
                DestroyWebView();
                ShowNativeStatus("DSH 已停止", snapshot.Detail, showActions: true);
                break;
            default:
                DestroyWebView();
                ShowNativeStatus(snapshot.ActivityTitle ?? "正在启动 DSH", snapshot.Detail, showActions: false);
                break;
        }
    }

    private void ShowWebUi(Uri endpoint)
    {
        if (_webView is null)
        {
            var webView = new NativeWebView();
            webView.EnvironmentRequested += WebView_OnEnvironmentRequested;
            _webView = webView;
            WebViewHost.Child = webView;
        }

        if (_webView.Source != endpoint)
        {
            _webView.Navigate(endpoint);
        }

        NativeStatusPanel.IsVisible = false;
        WebViewHost.IsVisible = true;
    }

    private void WebView_OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        switch (args)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                Directory.CreateDirectory(Runtime.Paths.WebViewDataDirectory);
                webView2.ProfileName = Runtime.Paths.ProductId;
                webView2.UserDataFolder = Runtime.Paths.WebViewDataDirectory;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs wkWebView:
                wkWebView.DataStoreIdentifier = MacOSWebViewDataStore.Identifier;
                break;
        }
    }

    private void ShowNativeStatus(string title, string detail, bool showActions)
    {
        RuntimeStatusText.Text = title;
        RuntimeDetailText.Text = detail;
        RuntimeActions.IsVisible = showActions;
        RetryButton.IsVisible = showActions;
        NativeStatusPanel.IsVisible = true;
        WebViewHost.IsVisible = false;
    }

    private void DestroyWebView()
    {
        if (_webView is null)
        {
            return;
        }

        _webView.EnvironmentRequested -= WebView_OnEnvironmentRequested;
        WebViewHost.Child = null;
        _webView = null;
    }

    private async void RetryButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        RetryButton.IsEnabled = false;
        try
        {
            await Runtime.Coordinator.RetryAsync().ConfigureAwait(true);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var result = Runtime.Log.OpenLogFolder();
        if (!result.Succeeded)
        {
            RuntimeDetailText.Text = $"The log folder could not be opened: {result.Error}";
        }
    }

    private async void CopyDiagnosticsButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await DiagnosticClipboard.CopyAsync(
                this,
                Runtime.Log.CreateCopyableDiagnosticText(
                    Runtime.Coordinator.ApplicationState,
                    [Runtime.Coordinator.Snapshot.Detail]))
                .ConfigureAwait(true);
            RuntimeDetailText.Text = "Diagnostic information was copied to the clipboard.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            RuntimeDetailText.Text = $"Diagnostics could not be copied: {exception.Message}";
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_closingForExit)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideToTray();
    }

    private void MainWindow_OnOpened(object? sender, EventArgs eventArgs)
    {
        _windowPresented = true;
        ApplySnapshot(Runtime.Coordinator.Snapshot);
    }

    private void MainWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_runtime is not null)
        {
            Opened -= MainWindow_OnOpened;
            Runtime.Coordinator.SnapshotChanged -= Coordinator_OnSnapshotChanged;
        }
        DestroyWebView();
    }

    private DesktopRuntime Runtime => _runtime ?? throw new InvalidOperationException("The desktop runtime was not configured.");
}
