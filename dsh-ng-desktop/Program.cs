using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DshNgDesktop.Core;
using DshNgDesktop.Diagnostics;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Platform;
using System.Text.Json;

namespace DshNgDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--doctor", StringComparison.OrdinalIgnoreCase)))
        {
            RunDoctor(args);
            return;
        }

        var paths = AppPaths.CreateDefault();
        var instance = new SingleInstanceCoordinator(paths.ProductId);
        if (!instance.TryAcquirePrimary())
        {
            instance.RequestActivationAsync().GetAwaiter().GetResult();
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(ActivateMainWindow);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void RunDoctor(string[] args)
    {
        var paths = AppPaths.CreateDefault();
        var doctor = new EnvironmentDoctor(paths, PlatformServices.CreateDefault());
        var report = doctor.RunAsync().GetAwaiter().GetResult();
        Console.Out.WriteLine(JsonSerializer.Serialize(report, EnvironmentDiagnosticJsonContext.Default.EnvironmentDiagnosticReport));
        Environment.ExitCode = report.HasErrors ? 1 : 0;
    }

    private static void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
