using Microsoft.Win32;
using System.Runtime.Versioning;

namespace DshNgDesktop.Platform;

public static class PlatformServices
{
    public static IPlatformServices CreateDefault() => OperatingSystem.IsWindows()
        ? new WindowsPlatformServices()
        : OperatingSystem.IsMacOS()
            ? new MacOSPlatformServices()
            : new UnsupportedPlatformServices();
}

internal abstract class PlatformServicesBase : IPlatformServices
{
    protected PlatformServicesBase(PlatformKind kind)
    {
        Kind = kind;
    }

    public PlatformKind Kind { get; }

    public virtual IPlatformProcessGroup CreateProcessGroup() => new DeferredPlatformProcessGroup(Kind);

    public virtual Task<PlatformOperationResult> RegisterStartupAsync(StartupRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} startup registration is not available in this build."));

    public virtual Task<PlatformOperationResult> UnregisterStartupAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} startup registration is not available in this build."));

    public virtual Task<StartupRegistrationState> GetStartupRegistrationStateAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(StartupRegistrationState.Unknown);

    public virtual Task<PlatformOperationResult> RegisterInstallationAsync(InstallationRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} installation registration is not available in this build."));

    public virtual Task<PlatformOperationResult> UnregisterInstallationAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} installation registration is not available in this build."));
}

/// <summary>
/// Windows registration uses only the current-user registry hives. Process
/// groups deliberately remain a M2 implementation because they must be bound
/// to the exact child process created by DshSupervisor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatformServices : PlatformServicesBase
{
    private const string StartupKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string UninstallKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

    public WindowsPlatformServices()
        : base(PlatformKind.Windows)
    {
    }

    public override Task<PlatformOperationResult> RegisterStartupAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true);
            if (key is null)
            {
                return Task.FromResult(PlatformOperationResult.Failure("The current-user startup registry key is unavailable."));
            }

            key.SetValue(registration.ProductId, BuildCommand(registration.ExecutablePath, registration.Arguments), RegistryValueKind.String);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<PlatformOperationResult> UnregisterStartupAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true);
            key?.DeleteValue(productId, throwOnMissingValue: false);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<StartupRegistrationState> GetStartupRegistrationStateAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
            return Task.FromResult(key?.GetValue(productId) is null
                ? StartupRegistrationState.NotRegistered
                : StartupRegistrationState.Registered);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(StartupRegistrationState.Unknown);
        }
    }

    public override Task<PlatformOperationResult> RegisterInstallationAsync(InstallationRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{UninstallKeyPath}\\{registration.ProductId}", writable: true);
            if (key is null)
            {
                return Task.FromResult(PlatformOperationResult.Failure("The current-user uninstall registry key is unavailable."));
            }

            key.SetValue("DisplayName", registration.DisplayName, RegistryValueKind.String);
            key.SetValue("InstallLocation", registration.InstallRoot, RegistryValueKind.String);
            key.SetValue("UninstallString", registration.UninstallCommand, RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<PlatformOperationResult> UnregisterInstallationAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            key?.DeleteSubKeyTree(productId, throwOnMissingSubKey: false);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    private static string BuildCommand(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var quotedExecutable = $"\"{executablePath.Replace("\"", "\\\"")}\"";
        return arguments.Count == 0
            ? quotedExecutable
            : $"{quotedExecutable} {string.Join(" ", arguments.Select(argument => $"\"{argument.Replace("\"", "\\\"")}\""))}";
    }
}

/// <summary>
/// macOS calls are intentionally kept at this one boundary. Service Management
/// registration and process-group interop are supplied with the installer and
/// supervisor milestones, without changing any shared coordinator contracts.
/// </summary>
internal sealed class MacOSPlatformServices : PlatformServicesBase
{
    public MacOSPlatformServices()
        : base(PlatformKind.MacOS)
    {
    }
}

internal sealed class UnsupportedPlatformServices : PlatformServicesBase
{
    public UnsupportedPlatformServices()
        : base(PlatformKind.Unsupported)
    {
    }
}

internal sealed class DeferredPlatformProcessGroup(PlatformKind kind) : IPlatformProcessGroup
{
    private static readonly string Message = "Process-group binding is supplied by the DSH supervisor milestone.";

    public Task<PlatformOperationResult> AddProcessAsync(int processId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public Task<PlatformOperationResult> RequestGracefulStopAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public Task<PlatformOperationResult> TerminateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
