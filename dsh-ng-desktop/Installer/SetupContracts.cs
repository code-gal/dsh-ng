using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

public enum SetupStage
{
    Preflight,
    DeployingClient,
    ProvisioningDsh,
    WaitingForWebUi,
    Registering,
    Committed,
    Stopping,
    RollingBack,
    Failed
}

public sealed record SetupProgress(
    SetupStage Stage,
    string Title,
    string Detail,
    bool IsTerminal = false,
    bool IsHeartbeat = false);

public sealed record SetupResult(
    bool Succeeded,
    bool WasCancelled,
    string Summary,
    string? Remediation)
{
    public static SetupResult Success() => new(
        true,
        false,
        "DSH Desktop 已安装完成，本地 Web 界面已就绪。",
        null);

    public static SetupResult Failure(string summary, string? remediation, bool wasCancelled = false) => new(
        false,
        wasCancelled,
        summary,
        remediation);
}

/// <summary>
/// The installer always copies a package payload into a different, product-owned
/// target directory. Replacing an existing target is permitted only after the
/// user explicitly selects the data-preserving repair path.
/// </summary>
public sealed record ClientDeploymentRequest(
    string PayloadDirectory,
    string InstallRoot,
    bool ReplaceExistingInstallRoot = false);

public sealed record ClientDeploymentResult(
    string InstallRoot,
    bool CreatedInstallRoot,
    string? ReplacedInstallRootBackup = null);

public interface IClientDeployment
{
    Task<ClientDeploymentResult> DeployAsync(ClientDeploymentRequest request, CancellationToken cancellationToken = default);

    Task RollbackAsync(ClientDeploymentResult deployment, CancellationToken cancellationToken = default);

    Task CommitAsync(ClientDeploymentResult deployment, CancellationToken cancellationToken = default);
}

public enum ExistingProductDataState
{
    None,
    VerifiedInstallation,
    InterruptedInstallation,
    UnverifiedManifest
}

public enum ExistingDataHandling
{
    RequireUserChoice,
    ReplaceClientPreservingData,
    FreshInstall
}

public sealed record InstallMaintenanceAcquisition(
    bool Succeeded,
    string? Error,
    IAsyncDisposable? Lease)
{
    public static InstallMaintenanceAcquisition Success(IAsyncDisposable lease) => new(true, null, lease);

    public static InstallMaintenanceAcquisition Failure(string error) => new(false, error, null);
}

public interface IInstallMaintenanceCoordinator
{
    Task<InstallMaintenanceAcquisition> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record SetupCoordinatorOptions(
    string PayloadDirectory,
    string InstalledExecutablePath,
    string DisplayName,
    string UninstallCommand,
    InstallPackageMetadata? PackageMetadata = null)
{
    public static SetupCoordinatorOptions CreateDefault(AppPaths paths, string? payloadDirectory = null)
    {
        var payload = Path.GetFullPath(payloadDirectory ?? AppContext.BaseDirectory);
        var executableRelativePath = ResolvePayloadExecutableRelativePath(payload);
        var installedExecutable = Path.Combine(paths.InstallRoot, executableRelativePath);
        return new SetupCoordinatorOptions(
            payload,
            installedExecutable,
            "DSH Desktop",
            $"\"{installedExecutable.Replace("\"", "\\\"")}\" --uninstall --install-root \"{paths.InstallRoot.Replace("\"", "\\\"")}\"");
    }

    private static string ResolvePayloadExecutableRelativePath(string payloadDirectory)
    {
        var assemblyName = typeof(SetupCoordinatorOptions).Assembly.GetName().Name ?? "DshNgDesktop";
        if (OperatingSystem.IsMacOS())
        {
            var macExecutable = Path.Combine("Contents", "MacOS", assemblyName);
            if (File.Exists(Path.Combine(payloadDirectory, macExecutable)))
            {
                return macExecutable;
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(processPath)),
                payloadDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            File.Exists(processPath))
        {
            return Path.GetFileName(processPath);
        }

        var platformExecutableName = OperatingSystem.IsWindows() ? $"{assemblyName}.exe" : assemblyName;
        if (File.Exists(Path.Combine(payloadDirectory, platformExecutableName)))
        {
            return platformExecutableName;
        }

        throw new InvalidOperationException("The installer payload does not contain the DSH Desktop executable.");
    }
}
