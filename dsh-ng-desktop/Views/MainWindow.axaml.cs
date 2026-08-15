using System.Diagnostics;
using Avalonia.Controls;

namespace DshNgDesktop.Views;

public partial class MainWindow : Window
{
    private static readonly Uri _dshUri = new("http://127.0.0.1:3080/");

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is not { } request || IsDshUri(request))
        {
            return;
        }

        e.Cancel = true;
        Process.Start(new ProcessStartInfo(request.ToString()) { UseShellExecute = true });
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        FailurePanel.IsVisible = !e.IsSuccess;
        if (!e.IsSuccess)
        {
            FailureMessage.Text = "无法连接 http://127.0.0.1:3080/。请先运行 npx @deepseek-ai/dsh web。";
        }
    }

    private void OnReloadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FailurePanel.IsVisible = false;
        DshWebView.Navigate(_dshUri);
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private static bool IsDshUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        && uri.Port == 3080;
}
