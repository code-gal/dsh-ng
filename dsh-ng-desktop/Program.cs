using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Diagnostics;
using DshNgDesktop.Core;
using DshNgDesktop.Diagnostics;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Installer;
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

        var installRoot = ReadOption(args, "--install-root") ?? SetupLocations.GetDefaultInstallRoot();
        var payloadDirectory = ReadOption(args, "--payload");
        var paths = AppPaths.CreateDefault(installRoot);
        var installerSession = args.Any(argument => string.Equals(argument, "--installer-session", StringComparison.OrdinalIgnoreCase));
        if (args.Any(argument => string.Equals(argument, "--uninstall-finalize", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstallFinalize(paths);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            StartUninstallHelper(paths);
            return;
        }

        var bootstrap = new SetupApplicationBootstrap(
            paths,
            payloadDirectory,
            args.Any(argument => string.Equals(argument, "--install", StringComparison.OrdinalIgnoreCase)),
            args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)),
            installerSession);
        SetupApplicationBootstrap.Configure(bootstrap);

        SingleInstanceCoordinator? instance = null;
        if (!installerSession)
        {
            instance = new SingleInstanceCoordinator(paths.ProductId);
            if (!instance.TryAcquirePrimary())
            {
                instance.RequestActivationAsync().GetAwaiter().GetResult();
                instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }

            instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(ActivateMainWindow);
            instance.UninstallRequested += (_, request) =>
                request.Accepted = Dispatcher.UIThread.InvokeAsync(RequestApplicationUninstall).GetAwaiter().GetResult();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (bootstrap.StartInstalledClientAfterExit)
        {
            StartInstalledClient(paths);
        }
        else if (installerSession)
        {
            Environment.ExitCode = 1;
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void RunDoctor(string[] arg)
    {
        var paths = AppPaths.CreateDefault();
        var doctor = new EnvironmentDoctor(paths, PlatformServices.CreateDefault());
        var report = doctor.RunAsync().GetAwaiter().GetResult();
        Console.Out.WriteLine(JsonSerializer.Serialize(report, EnvironmentDiagnosticJsonContext.Default.EnvironmentDiagnosticReport));
        Environment.ExitCode = report.HasErrors ? 1 : 0;
    }

    private static void StartInstalledClient(AppPaths paths)
    {
        var executable = Path.Combine(paths.InstallRoot, $"{typeof(Program).Assembly.GetName().Name}.exe");
        if (!File.Exists(executable))
        {
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            if (Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true,
                    WorkingDirectory = paths.InstallRoot
                }) is null)
            {
                Environment.ExitCode = 1;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Environment.ExitCode = 1;
        }
    }

    private static void ActivateMainWindow()
    {
        if (Application.Current is App app)
        {
            app.ShowMainWindow();
            return;
        }

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

    private static bool RequestApplicationUninstall()
    {
        if (Application.Current is App app)
        {
            return app.TryRequestUninstall();
        }

        return false;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[index + 1];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"The {option} option requires a directory path.");
                }

                return Path.GetFullPath(value);
            }
        }

        return null;
    }

    private static void StartUninstallHelper(AppPaths paths)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) || !IsWithin(paths.InstallRoot, executable))
        {
            Environment.ExitCode = 1;
            return;
        }

        var helperDirectory = Path.Combine(Path.GetTempPath(), "DshDesktop-Uninstall", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(helperDirectory);
            var helperExecutable = Path.Combine(helperDirectory, Path.GetFileName(executable));
            File.Copy(executable, helperExecutable, overwrite: false);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperExecutable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--uninstall-finalize");
            startInfo.ArgumentList.Add("--install-root");
            startInfo.ArgumentList.Add(paths.InstallRoot);
            if (Process.Start(startInfo) is null)
            {
                Environment.ExitCode = 1;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Environment.ExitCode = 1;
        }
    }

    private static void RunUninstallFinalize(AppPaths paths)
    {
        var result = RunUninstallFinalizeAsync(paths)
            .GetAwaiter()
            .GetResult();
        Environment.ExitCode = result.Succeeded ? 0 : 1;
    }

    private static async Task<PlatformOperationResult> RunUninstallFinalizeAsync(AppPaths paths)
    {
        await using var instance = new SingleInstanceCoordinator(paths.ProductId);
        if (!instance.TryAcquireUninstallLock(TimeSpan.Zero))
        {
            var request = await instance.RequestUninstallAsync().ConfigureAwait(false);
            if (!request.Delivered || !request.Accepted)
            {
                return PlatformOperationResult.Failure(
                    $"无法与正在运行的 DSH Desktop 建立卸载协作：{request.Error ?? "实例未确认卸载请求。"}");
            }

            if (!instance.TryAcquireUninstallLock(TimeSpan.FromSeconds(30)))
            {
                return PlatformOperationResult.Failure("正在运行的 DSH Desktop 未在 30 秒内完成停止；为避免删除仍在使用的文件，卸载未开始。");
            }
        }

        return await new UninstallCoordinator(paths, PlatformServices.CreateDefault(), new ProductDataCleaner(paths))
            .RunAsync()
            .ConfigureAwait(false);
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !relative.Equals("..", comparison) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", comparison));
    }
}
