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
        var installRequested = HasFlag(args, "--install");
        var installerSession = HasFlag(args, "--installer-session");
        var developmentMode = HasFlag(args, "--development");
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

        var installationLaunch = ResolveInstallationLaunch(installRequested, installerSession, payloadDirectory);
        if (installationLaunch == InstallationLaunch.Invalid ||
            (developmentMode && installationLaunch == InstallationLaunch.Setup))
        {
            Environment.ExitCode = 1;
            return;
        }

        var runDesktopHost = installationLaunch == InstallationLaunch.None &&
            (developmentMode || SetupLocations.IsCurrentProcessInstalled(paths));
        if (!runDesktopHost && installationLaunch != InstallationLaunch.Setup)
        {
            // DshNgDesktop.exe is an installed-client entry point. A source
            // build must opt in with --development; it must never infer an
            // installer role merely because its manifest is absent.
            Environment.ExitCode = 1;
            return;
        }

        var bootstrap = new SetupApplicationBootstrap(
            paths,
            payloadDirectory,
            HasFlag(args, "--background"),
            installerSession,
            runDesktopHost);
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

        if (installerSession)
        {
            Environment.ExitCode = bootstrap.InstallerSessionSucceeded == true ? 0 : 1;
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
        string? result = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                if (result is not null)
                {
                    throw new ArgumentException($"The {option} option may only be supplied once.");
                }

                if (index == args.Count - 1)
                {
                    throw new ArgumentException($"The {option} option requires a directory path.");
                }

                var value = args[index + 1];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"The {option} option requires a directory path.");
                }

                result = Path.GetFullPath(value);
            }
        }

        return result;
    }

    private static bool HasFlag(IEnumerable<string> args, string flag)
    {
        var count = args.Count(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
        if (count > 1)
        {
            throw new ArgumentException($"The {flag} option may only be supplied once.");
        }

        return count == 1;
    }

    private static InstallationLaunch ResolveInstallationLaunch(bool installRequested, bool installerSession, string? payloadDirectory)
    {
        if (!installRequested && !installerSession && payloadDirectory is null)
        {
            return InstallationLaunch.None;
        }

        // The Windows transport host is the sole caller that may request the
        // installer session. macOS has its own signed package bootstrap and
        // still supplies the same explicit --install plus --payload boundary.
        var validWindowsSession = OperatingSystem.IsWindows() && installRequested && installerSession && payloadDirectory is not null;
        var validMacOSSession = OperatingSystem.IsMacOS() && installRequested && !installerSession && payloadDirectory is not null;
        return validWindowsSession || validMacOSSession
            ? InstallationLaunch.Setup
            : InstallationLaunch.Invalid;
    }

    private enum InstallationLaunch
    {
        None,
        Setup,
        Invalid
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
