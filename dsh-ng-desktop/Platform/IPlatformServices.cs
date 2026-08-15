namespace DshNgDesktop.Platform;

public enum PlatformKind
{
    Windows,
    MacOS,
    Unsupported
}

public enum StartupRegistrationState
{
    Registered,
    NotRegistered,
    Unknown
}

public sealed record PlatformOperationResult(bool Succeeded, string? Error = null)
{
    public static PlatformOperationResult Success() => new(true);

    public static PlatformOperationResult Failure(string error) => new(false, error);
}

public sealed record StartupRegistration(
    string ProductId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);

public sealed record InstallationRegistration(
    string ProductId,
    string DisplayName,
    string InstallRoot,
    string UninstallCommand);

/// <summary>
/// Represents only a process collection created by this application. It must
/// never enumerate or claim unrelated Node, npm or npx processes.
/// </summary>
public interface IPlatformProcessGroup : IAsyncDisposable
{
    Task<PlatformOperationResult> AddProcessAsync(int processId, CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> RequestGracefulStopAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> TerminateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Contains all OS-specific seams used by DSH supervision and installers.
/// Shared coordinators depend on this interface and therefore contain no
/// scattered operating-system conditionals.
/// </summary>
public interface IPlatformServices
{
    PlatformKind Kind { get; }

    IPlatformProcessGroup CreateProcessGroup();

    Task<PlatformOperationResult> RegisterStartupAsync(
        StartupRegistration registration,
        CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> UnregisterStartupAsync(
        string productId,
        CancellationToken cancellationToken = default);

    Task<StartupRegistrationState> GetStartupRegistrationStateAsync(
        string productId,
        CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> RegisterInstallationAsync(
        InstallationRegistration registration,
        CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> UnregisterInstallationAsync(
        string productId,
        CancellationToken cancellationToken = default);
}
