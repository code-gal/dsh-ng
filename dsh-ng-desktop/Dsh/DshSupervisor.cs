using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DshNgDesktop.Core;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Dsh;

public enum DshSupervisorStatus
{
    Stopped,
    Starting,
    Ready,
    Faulted,
    Stopping
}

public enum DshStartFailure
{
    None,
    Prerequisite,
    Storage,
    PortUnavailable,
    Launch,
    ProcessOwnership,
    HealthCheck,
    Cancelled
}

public sealed record DshSupervisorSnapshot(
    DshSupervisorStatus Status,
    DshRuntimeState? RuntimeState,
    string? Detail,
    string? ActivityTitle = null);

public sealed record DshStartResult(
    bool Succeeded,
    DshStartFailure Failure,
    string Detail,
    string? Remediation,
    DshRuntimeState? RuntimeState,
    string? ActivityTitle = null)
{
    public static DshStartResult Success(DshRuntimeState runtimeState) =>
        new(true, DshStartFailure.None, "DSH Web UI is ready.", null, runtimeState);

    public static DshStartResult Failed(
        DshStartFailure failure,
        string detail,
        string? remediation = null,
        string? activityTitle = null) =>
        new(false, failure, detail, remediation, null, activityTitle);
}

public sealed record DshSupervisorOptions(
    TimeSpan PrerequisiteTimeout,
    TimeSpan StartupTimeout,
    TimeSpan HealthRequestTimeout,
    TimeSpan HealthPollInterval,
    TimeSpan GracefulStopTimeout,
    int MaximumPortAttempts)
{
    public static DshSupervisorOptions Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(10),
        3);

    /// <summary>
    /// A null value intentionally means that the installer keeps waiting until
    /// DSH is ready, exits, or the user cancels. First npx supply can take a
    /// long time on slow networks, so it must not reuse normal-start timeout.
    /// </summary>
    public TimeSpan? InstallationStartupTimeout { get; init; }

    public TimeSpan InstallationProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan LongWaitNoticeAfter { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (PrerequisiteTimeout <= TimeSpan.Zero ||
            StartupTimeout <= TimeSpan.Zero ||
            HealthRequestTimeout <= TimeSpan.Zero ||
            HealthPollInterval <= TimeSpan.Zero ||
            GracefulStopTimeout <= TimeSpan.Zero ||
            InstallationStartupTimeout is { } installationTimeout && installationTimeout <= TimeSpan.Zero ||
            InstallationProgressInterval <= TimeSpan.Zero ||
            LongWaitNoticeAfter <= TimeSpan.Zero ||
            MaximumPortAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(DshSupervisorOptions), "Supervisor timeouts must be positive and port attempts must be between 1 and 10.");
        }
    }
}

public sealed record DshCommandProbeResult(
    string Command,
    string? ExecutablePath,
    string? Version,
    string? Error,
    string Remediation)
{
    public bool Succeeded => ExecutablePath is not null && Version is not null && Error is null;
}

public sealed record DshExecutableValidationResult(DshCommandProbeResult Node, DshCommandProbeResult Npx)
{
    public bool Succeeded => Node.Succeeded && Npx.Succeeded;

    public DshCommandProbeResult FirstFailure => !Node.Succeeded ? Node : Npx;
}

public interface IDshExecutableValidator
{
    Task<DshExecutableValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
}

public sealed record DshProcessLaunchRequest(
    string NpxExecutable,
    int Port,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    Action<string, bool> OutputReceived);

public interface IDshProcessHandle : IAsyncDisposable
{
    int ProcessId { get; }

    DateTimeOffset StartedAtUtc { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    event EventHandler? Exited;

    Task<bool> RequestGracefulStopAsync(CancellationToken cancellationToken = default);

    Task ForceStopAsync(CancellationToken cancellationToken = default);

    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

public interface IDshProcessLauncher
{
    Task<IDshProcessHandle> LaunchAsync(DshProcessLaunchRequest request, CancellationToken cancellationToken = default);
}

public sealed record DshHealthCheckResult(bool IsHealthy, string Detail);

/// <summary>
/// A compact, user-facing activity update for the setup window. Raw npx output
/// continues to be recorded only in the runtime log.
/// </summary>
public sealed record DshInstallationProgress(
    string Title,
    string Detail,
    TimeSpan Elapsed,
    bool IsHeartbeat = false);

public interface IDshHealthProbe
{
    Task<DshHealthCheckResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken = default);
}

public interface ILoopbackPortReservation : IAsyncDisposable
{
    int Port { get; }
}

public interface ILoopbackPortReservationProvider
{
    Task<ILoopbackPortReservation?> TryReserveAsync(int port, CancellationToken cancellationToken = default);

    Task<ILoopbackPortReservation> ReserveEphemeralAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The installer consumes only the start/stop contract. The concrete
/// supervisor remains the sole production owner of npx, Node and DSH process
/// trees while installer tests can use a controlled runtime boundary.
/// </summary>
public interface IDshRuntimeSupervisor
{
    Task<DshStartResult> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional setup-specific contract that keeps the installer responsive during
/// an unbounded first npx supply without widening the normal runtime contract.
/// </summary>
public interface IDshInstallationProgressSource
{
    event EventHandler<DshInstallationProgress>? InstallationProgressChanged;

    Task<DshStartResult> StartForInstallationAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The sole owner of DSH npx/Node process trees. It has injectable process,
/// HTTP and port seams so supply and supervision can be verified without
/// running a real DSH package in unit tests.
/// </summary>
public sealed class DshSupervisor : IDshRuntimeSupervisor, IDshInstallationProgressSource, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platformServices;
    private readonly AppLog _log;
    private readonly IDshExecutableValidator _executableValidator;
    private readonly IDshProcessLauncher _processLauncher;
    private readonly IDshHealthProbe _healthProbe;
    private readonly ILoopbackPortReservationProvider _portReservations;
    private readonly DshRuntimeStateStore _runtimeStateStore;
    private readonly DshSupervisorOptions _options;
    private readonly bool _ownsHealthProbe;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IDshProcessHandle? _process;
    private IPlatformProcessGroup? _processGroup;
    private DshRuntimeState? _runtimeState;
    private DshSupervisorStatus _status = DshSupervisorStatus.Stopped;
    private string? _detail;
    private string? _activityTitle;
    private DshStartupFailureHint? _latestNpxFailure;
    private DshStartupActivityRank _startupActivityRank;
    private long _generation;
    private bool _isStopping;
    private bool _disposed;

    public event EventHandler<DshInstallationProgress>? InstallationProgressChanged;

    public DshSupervisor(
        AppPaths paths,
        IPlatformServices platformServices,
        AppLog log,
        DshSupervisorOptions? options = null,
        IDshExecutableValidator? executableValidator = null,
        IDshProcessLauncher? processLauncher = null,
        IDshHealthProbe? healthProbe = null,
        ILoopbackPortReservationProvider? portReservations = null,
        DshRuntimeStateStore? runtimeStateStore = null)
    {
        _paths = paths;
        _platformServices = platformServices;
        _log = log;
        _options = options ?? DshSupervisorOptions.Default;
        _options.Validate();
        _executableValidator = executableValidator ?? new SystemDshExecutableValidator(_options.PrerequisiteTimeout);
        _processLauncher = processLauncher ?? new SystemDshProcessLauncher();
        _ownsHealthProbe = healthProbe is null;
        _healthProbe = healthProbe ?? new DshHttpHealthProbe(_options.HealthRequestTimeout);
        _portReservations = portReservations ?? new TcpLoopbackPortReservationProvider();
        _runtimeStateStore = runtimeStateStore ?? new DshRuntimeStateStore(paths);
    }

    public event EventHandler<DshSupervisorSnapshot>? StateChanged;

    public DshSupervisorSnapshot Snapshot => new(_status, _runtimeState, _detail, _activityTitle);

    public async Task<DshStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        return await StartWithPolicyAsync(
                new DshStartupPolicy(_options.StartupTimeout, IsInstallation: false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DshStartResult> StartForInstallationAsync(CancellationToken cancellationToken = default)
    {
        return await StartWithPolicyAsync(
                new DshStartupPolicy(_options.InstallationStartupTimeout, IsInstallation: true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DshStartResult> StartWithPolicyAsync(DshStartupPolicy startupPolicy, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status == DshSupervisorStatus.Ready && _process is { HasExited: false } && _runtimeState is not null)
            {
                return DshStartResult.Success(_runtimeState);
            }

            return await StartCoreAsync(startupPolicy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Performs at most two ordinary launches. A third launch is allowed only
    /// when the caller has independently confirmed the private npx cache is
    /// corrupt; DSH_HOME is never touched by this recovery path.
    /// </summary>
    public async Task<DshStartResult> StartWithRecoveryAsync(
        bool privateNpmCacheConfirmedCorrupt,
        CancellationToken cancellationToken = default)
    {
        var first = await StartAsync(cancellationToken).ConfigureAwait(false);
        if (first.Succeeded)
        {
            return first;
        }

        var second = await StartAsync(cancellationToken).ConfigureAwait(false);
        if (second.Succeeded || !privateNpmCacheConfirmedCorrupt)
        {
            return second;
        }

        try
        {
            ClearPrivateNpmCache();
            await _log.WarningAsync(AppLogStream.Runtime, "dsh-cache-reset", "The confirmed private npx cache was cleared before one final DSH start attempt.", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DshStartResult.Failed(
                DshStartFailure.Storage,
                $"The private npx cache could not be cleared: {exception.Message}",
                "Close applications using DSH Desktop data and retry. DSH_HOME was not changed.");
        }

        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<DshStartResult> StartCoreAsync(DshStartupPolicy startupPolicy, CancellationToken cancellationToken)
    {
        _startupActivityRank = DshStartupActivityRank.Environment;
        TransitionTo(
            DshSupervisorStatus.Starting,
            null,
            "正在确认系统 Node.js 和 npx 可用。",
            "正在检查 DSH 运行环境");
        PublishInstallationProgress(
            startupPolicy,
            "正在检查 DSH 运行环境",
            "正在确认系统 Node.js 和 npx 可用。",
            TimeSpan.Zero);
        DshExecutableValidationResult executables;
        try
        {
            executables = await _executableValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TransitionTo(DshSupervisorStatus.Stopped, null, "DSH prerequisite validation was cancelled.");
            return DshStartResult.Failed(DshStartFailure.Cancelled, "DSH prerequisite validation was cancelled.");
        }
        catch (Exception exception)
        {
            return await FailStartAsync(
                DshStartFailure.Prerequisite,
                $"Node.js and npx could not be validated: {exception.Message}",
                "Ensure a supported system Node.js installation is available on PATH.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!executables.Succeeded)
        {
            var failure = executables.FirstFailure;
            return await FailStartAsync(DshStartFailure.Prerequisite, failure.Error ?? $"{failure.Command} could not be validated.", failure.Remediation, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(_paths.NpmCacheDirectory);
            Directory.CreateDirectory(_paths.DshHomeDirectory);
            Directory.CreateDirectory(_paths.LauncherWorkingDirectory);
            ReportStartupActivity(
                startupPolicy,
                new DshStartupActivity(
                    DshStartupActivityRank.UpdateCheck,
                    "正在检查 DSH 更新",
                    "正在通过 npx 检查 DSH 的本地缓存和可用版本。"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await FailStartAsync(
                DshStartFailure.Storage,
                $"The private DSH runtime directories could not be prepared: {exception.Message}",
                "Ensure the current user can write to the DSH Desktop application-data directory.",
                cancellationToken).ConfigureAwait(false);
        }

        DshRuntimeState? persistedState = null;
        try
        {
            persistedState = await _runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            await _log.WarningAsync(AppLogStream.Runtime, "dsh-runtime-state-invalid", $"Ignoring unreadable DSH runtime state: {exception.Message}", exception, cancellationToken)
                .ConfigureAwait(false);
        }

        var excludedPorts = new HashSet<int>();
        var preferredPort = persistedState is { Port: >= 1 and <= 65535 } ? persistedState.Port : (int?)null;
        DshStartResult? lastFailure = null;

        for (var attempt = 0; attempt < _options.MaximumPortAttempts; attempt++)
        {
            var reservation = await ReservePortAsync(preferredPort, excludedPorts, cancellationToken).ConfigureAwait(false);
            if (reservation is null)
            {
                break;
            }

            var port = reservation.Port;
            excludedPorts.Add(port);
            await reservation.DisposeAsync().ConfigureAwait(false);

            var started = await TryStartOnPortAsync(executables.Npx.ExecutablePath!, port, startupPolicy, cancellationToken).ConfigureAwait(false);
            if (started.Succeeded)
            {
                return started;
            }

            lastFailure = started;

            if (started.Failure is DshStartFailure.Prerequisite or DshStartFailure.Storage or DshStartFailure.ProcessOwnership or DshStartFailure.Cancelled)
            {
                return started;
            }

            preferredPort = null;
        }

        if (lastFailure is not null)
        {
            return await FailStartAsync(
                    lastFailure.Failure,
                    lastFailure.Detail,
                    lastFailure.Remediation,
                    cancellationToken,
                    lastFailure.ActivityTitle)
                .ConfigureAwait(false);
        }

        return await FailStartAsync(
            DshStartFailure.PortUnavailable,
            "DSH could not obtain a usable private loopback port.",
            "Close the conflicting local service or retry DSH Desktop; unrelated processes were not changed.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DshStartResult> TryStartOnPortAsync(
        string npxExecutable,
        int port,
        DshStartupPolicy startupPolicy,
        CancellationToken cancellationToken)
    {
        IDshProcessHandle? process = null;
        IPlatformProcessGroup? processGroup = null;
        _latestNpxFailure = null;
        try
        {
            process = await _processLauncher.LaunchAsync(
                new DshProcessLaunchRequest(
                    npxExecutable,
                    port,
                    _paths.LauncherWorkingDirectory,
                    BuildDshEnvironment(),
                    (line, isError) =>
                    {
                        _ = WriteProcessOutputAsync(line, isError);
                        HandleProcessOutput(startupPolicy, line, isError);
                    }),
                cancellationToken).ConfigureAwait(false);

            processGroup = _platformServices.CreateProcessGroup();
            var ownership = await processGroup.AddProcessAsync(process.ProcessId, cancellationToken).ConfigureAwait(false);
            if (!ownership.Succeeded)
            {
                await ForceStopExactProcessAsync(process, cancellationToken).ConfigureAwait(false);
                await processGroup.DisposeAsync().ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
                return await FailStartAsync(
                    DshStartFailure.ProcessOwnership,
                    $"DSH process ownership could not be established: {ownership.Error}",
                    "DSH Desktop stopped the process it created. Inspect the runtime log and retry.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                var exitCode = process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                await processGroup.DisposeAsync().ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
                return CreateProcessExitFailure(exitCode);
            }

            var waitDescription = startupPolicy.Timeout is { } timeout
                ? $"Waiting up to {timeout.TotalMinutes:0} minutes for the DSH Web UI at http://127.0.0.1:{port}/."
                : $"Waiting without a fixed timeout for the DSH Web UI at http://127.0.0.1:{port}/; the installer can be stopped at any time.";
            await _log.InformationAsync(
                    AppLogStream.Runtime,
                    "dsh-health-wait",
                    waitDescription,
                    cancellationToken)
                .ConfigureAwait(false);
            _latestNpxFailure = null;
            ReportStartupActivity(
                startupPolicy,
                new DshStartupActivity(
                    DshStartupActivityRank.StartingService,
                    "正在启动 DSH 服务",
                    "正在等待 DSH 本地服务响应。"));
            var ready = await WaitForHealthyWebUiAsync(process, port, startupPolicy, cancellationToken).ConfigureAwait(false);
            if (!ready.IsHealthy)
            {
                await _log.WarningAsync(
                        AppLogStream.Runtime,
                        "dsh-health-wait-failed",
                        $"DSH did not become healthy on port {port}: {ready.Detail}",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                await StopOwnedProcessAsync(process, processGroup, CancellationToken.None).ConfigureAwait(false);
                await processGroup.DisposeAsync().ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
                return CreateHealthCheckFailure(ready);
            }

            var runtimeState = new DshRuntimeState(
                port,
                process.ProcessId,
                process.StartedAtUtc.UtcDateTime.Ticks,
                Guid.NewGuid().ToString("N"));
            await _runtimeStateStore.SaveAsync(runtimeState, cancellationToken).ConfigureAwait(false);

            _process = process;
            _processGroup = processGroup;
            _runtimeState = runtimeState;
            _isStopping = false;
            var generation = ++_generation;
            process.Exited += (_, _) => _ = HandleProcessExitedAsync(process, generation);
            if (process.HasExited)
            {
                _ = HandleProcessExitedAsync(process, generation);
            }

            TransitionTo(DshSupervisorStatus.Ready, runtimeState, $"DSH Web UI is ready at http://127.0.0.1:{port}/.");
            await _log.InformationAsync(AppLogStream.Runtime, "dsh-ready", $"DSH Web UI passed identity validation on loopback port {port}.", cancellationToken)
                .ConfigureAwait(false);
            return DshStartResult.Success(runtimeState);
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                await StopOwnedProcessAsync(process, processGroup, CancellationToken.None).ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (processGroup is not null)
            {
                await processGroup.DisposeAsync().ConfigureAwait(false);
            }

            return await FailStartAsync(DshStartFailure.Cancelled, "DSH startup was cancelled.", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SocketException or System.ComponentModel.Win32Exception)
        {
            if (process is not null)
            {
                await StopOwnedProcessAsync(process, processGroup, CancellationToken.None).ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (processGroup is not null)
            {
                await processGroup.DisposeAsync().ConfigureAwait(false);
            }

            return DshStartResult.Failed(
                DshStartFailure.Launch,
                $"DSH could not start on loopback port {port}: {exception.Message}",
                "Inspect the runtime log and retry. No unrelated Node process was changed.",
                "DSH 启动失败");
        }
    }

    private async Task<DshHealthCheckResult> WaitForHealthyWebUiAsync(
        IDshProcessHandle process,
        int port,
        DshStartupPolicy startupPolicy,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startupPolicy.Timeout is { } timeout ? startedAt + timeout : (DateTimeOffset?)null;
        var nextProgressAt = startedAt;
        var lastLoggedMinute = -1L;
        var endpoint = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var lastFailure = "DSH Web 界面仍在启动。";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var exitCode = process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                return new DshHealthCheckResult(false, $"DSH exited before its Web UI became ready (exit code {exitCode}).");
            }

            try
            {
                var result = await _healthProbe.ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (result.IsHealthy)
                {
                    return result;
                }

                lastFailure = result.Detail;
            }
            catch (HttpRequestException exception)
            {
                lastFailure = $"DSH Web UI is not reachable yet: {exception.Message}";
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = "DSH Web 界面未能在健康检查时限内响应。";
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - startedAt;
            if (startupPolicy.IsInstallation && now >= nextProgressAt)
            {
                var waitingDetail = elapsed >= _options.LongWaitNoticeAfter
                    ? "DSH 仍在启动；网络较慢时可继续等待或停止安装。"
                    : "DSH 正在启动并验证本地 Web 界面。";
                PublishInstallationProgress(startupPolicy, "正在启动 DSH 服务", waitingDetail, elapsed, isHeartbeat: true);
                nextProgressAt = now + _options.InstallationProgressInterval;
            }

            var elapsedMinutes = (long)elapsed.TotalMinutes;
            if (elapsedMinutes > 0 && elapsedMinutes != lastLoggedMinute)
            {
                lastLoggedMinute = elapsedMinutes;
                await _log.InformationAsync(
                        AppLogStream.Runtime,
                        "dsh-health-waiting",
                        $"DSH Web UI is still starting after {FormatElapsed(elapsed)}: {lastFailure}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (deadline is { } timeoutAt && now >= timeoutAt)
            {
                return new DshHealthCheckResult(false, lastFailure);
            }

            await Task.Delay(_options.HealthPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ILoopbackPortReservation?> ReservePortAsync(int? preferredPort, ISet<int> excludedPorts, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { preferredPort, 3080 })
        {
            if (candidate is not { } port || excludedPorts.Contains(port))
            {
                continue;
            }

            var reservation = await _portReservations.TryReserveAsync(port, cancellationToken).ConfigureAwait(false);
            if (reservation is not null)
            {
                return reservation;
            }
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var reservation = await _portReservations.ReserveEphemeralAsync(cancellationToken).ConfigureAwait(false);
            if (excludedPorts.Add(reservation.Port))
            {
                return reservation;
            }

            await reservation.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var processGroup = _processGroup;
        if (process is null || processGroup is null)
        {
            await _runtimeStateStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
            _runtimeState = null;
            TransitionTo(DshSupervisorStatus.Stopped, null, "DSH is not running.");
            return;
        }

        _isStopping = true;
        TransitionTo(DshSupervisorStatus.Stopping, _runtimeState, "Stopping the DSH process group.");
        await StopOwnedProcessAsync(process, processGroup, cancellationToken).ConfigureAwait(false);
        await processGroup.DisposeAsync().ConfigureAwait(false);
        await process.DisposeAsync().ConfigureAwait(false);
        _process = null;
        _processGroup = null;
        _runtimeState = null;
        await _runtimeStateStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
        TransitionTo(DshSupervisorStatus.Stopped, null, "DSH stopped.");
        await _log.InformationAsync(AppLogStream.Runtime, "dsh-stopped", "The DSH process group stopped.", cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOwnedProcessAsync(IDshProcessHandle process, IPlatformProcessGroup? processGroup, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        if (processGroup is not null)
        {
            var graceful = await processGroup.RequestGracefulStopAsync(cancellationToken).ConfigureAwait(false);
            if (!graceful.Succeeded)
            {
                await _log.WarningAsync(AppLogStream.Runtime, "dsh-graceful-stop-unavailable", graceful.Error ?? "A platform graceful-stop request was unavailable.", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await process.RequestGracefulStopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await process.RequestGracefulStopAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var gracefulTimeout = new CancellationTokenSource(_options.GracefulStopTimeout);
            await process.WaitForExitAsync(gracefulTimeout.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // The owned process group is forcibly terminated below.
        }

        if (processGroup is not null)
        {
            var forced = await processGroup.TerminateAsync(cancellationToken).ConfigureAwait(false);
            if (!forced.Succeeded)
            {
                await ForceStopExactProcessAsync(process, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ForceStopExactProcessAsync(process, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var forceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(forceTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _log.WarningAsync(AppLogStream.Runtime, "dsh-force-stop-timeout", "The owned DSH process did not report exit after forced termination.", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ForceStopExactProcessAsync(IDshProcessHandle process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            await process.ForceStopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DshStartResult CreateProcessExitFailure(string exitCode)
    {
        if (_latestNpxFailure is { } failure)
        {
            return DshStartResult.Failed(
                DshStartFailure.Launch,
                $"{failure.Detail}（npx 退出代码 {exitCode}）。",
                failure.Remediation,
                failure.Title);
        }

        return DshStartResult.Failed(
            DshStartFailure.Launch,
            $"DSH 在其 Web 界面就绪前退出（退出代码 {exitCode}）。",
            "请打开运行日志，检查 Node.js、npx 和本地网络策略后重试。",
            "DSH 启动失败");
    }

    private DshStartResult CreateHealthCheckFailure(DshHealthCheckResult healthResult)
    {
        if (_latestNpxFailure is { } failure)
        {
            return DshStartResult.Failed(
                DshStartFailure.Launch,
                failure.Detail,
                failure.Remediation,
                failure.Title);
        }

        return DshStartResult.Failed(
            DshStartFailure.HealthCheck,
            healthResult.Detail,
            "Inspect the runtime log and retry. DSH Desktop did not claim or stop unrelated loopback services.",
            "DSH 启动失败");
    }

    private async Task<DshStartResult> FailStartAsync(
        DshStartFailure failure,
        string detail,
        string? remediation,
        CancellationToken cancellationToken,
        string? activityTitle = null)
    {
        _runtimeState = null;
        var title = activityTitle ?? "DSH 启动失败";
        TransitionTo(DshSupervisorStatus.Faulted, null, detail, title);
        await _log.ErrorAsync(AppLogStream.Runtime, "dsh-start-failed", detail, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DshStartResult.Failed(failure, detail, remediation, title);
    }

    private async Task HandleProcessExitedAsync(IDshProcessHandle process, long generation)
    {
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _isStopping || generation != _generation || !ReferenceEquals(_process, process))
                {
                    return;
                }

                var exitCode = process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                var processGroup = _processGroup;
                _process = null;
                _processGroup = null;
                _runtimeState = null;
                await _runtimeStateStore.RemoveAsync().ConfigureAwait(false);
                TransitionTo(
                    DshSupervisorStatus.Faulted,
                    null,
                    $"DSH exited unexpectedly (exit code {exitCode}).",
                    "DSH 运行异常");
                await _log.ErrorAsync(AppLogStream.Runtime, "dsh-exited", $"The owned DSH process exited unexpectedly with code {exitCode}.").ConfigureAwait(false);
                if (processGroup is not null)
                {
                    await processGroup.DisposeAsync().ConfigureAwait(false);
                }

                await process.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // The process already exited; there is no safe remediation besides
            // recording the fault when the normal event path can acquire state.
        }
    }

    private IReadOnlyDictionary<string, string> BuildDshEnvironment() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npm_config_cache"] = _paths.NpmCacheDirectory,
            ["DSH_HOME"] = _paths.DshHomeDirectory,
            ["npm_config_yes"] = "true",
            ["CI"] = "true",
            ["NO_COLOR"] = "1"
        };

    private void ClearPrivateNpmCache()
    {
        if (!_paths.IsExactManagedPath(ManagedPathKind.NpmCache, _paths.NpmCacheDirectory))
        {
            throw new InvalidOperationException("The configured npm cache is not an exact product-managed path.");
        }

        if (Directory.Exists(_paths.NpmCacheDirectory))
        {
            Directory.Delete(_paths.NpmCacheDirectory, recursive: true);
        }
    }

    private void PublishInstallationProgress(
        DshStartupPolicy startupPolicy,
        string title,
        string detail,
        TimeSpan elapsed,
        bool isHeartbeat = false)
    {
        if (!startupPolicy.IsInstallation)
        {
            return;
        }

        InstallationProgressChanged?.Invoke(this, new DshInstallationProgress(title, detail, elapsed, isHeartbeat));
    }

    private void HandleProcessOutput(DshStartupPolicy startupPolicy, string line, bool isError)
    {
        if (ClassifyProcessFailure(line, isError) is { } failure)
        {
            _latestNpxFailure = failure;
            ReportStartupActivity(
                startupPolicy,
                new DshStartupActivity(_startupActivityRank, failure.Title, failure.Detail));
            return;
        }

        if (ClassifyProcessOutput(line) is { } activity)
        {
            if (activity.Rank == DshStartupActivityRank.StartingService)
            {
                _latestNpxFailure = null;
            }

            ReportStartupActivity(startupPolicy, activity);
        }
    }

    private void ReportStartupActivity(DshStartupPolicy startupPolicy, DshStartupActivity activity)
    {
        if (_status != DshSupervisorStatus.Starting || activity.Rank < _startupActivityRank)
        {
            return;
        }

        if (activity.Rank == _startupActivityRank &&
            string.Equals(_activityTitle, activity.Title, StringComparison.Ordinal) &&
            string.Equals(_detail, activity.Detail, StringComparison.Ordinal))
        {
            return;
        }

        _startupActivityRank = activity.Rank;
        TransitionTo(DshSupervisorStatus.Starting, null, activity.Detail, activity.Title);
        PublishInstallationProgress(startupPolicy, activity.Title, activity.Detail, TimeSpan.Zero);
    }

    private DshStartupFailureHint? ClassifyProcessFailure(string line, bool isError)
    {
        if (_status != DshSupervisorStatus.Starting || !isError)
        {
            return null;
        }

        var normalized = line.Trim().ToLowerInvariant();
        var isErrorMessage = normalized.Contains("npm err", StringComparison.Ordinal) ||
                             normalized.Contains("error", StringComparison.Ordinal) ||
                             normalized.Contains("failed", StringComparison.Ordinal);
        if (!isErrorMessage)
        {
            return null;
        }

        var isNetworkError = normalized.Contains("eai_again", StringComparison.Ordinal) ||
                             normalized.Contains("enotfound", StringComparison.Ordinal) ||
                             normalized.Contains("etimedout", StringComparison.Ordinal) ||
                             normalized.Contains("econnreset", StringComparison.Ordinal) ||
                             normalized.Contains("econnrefused", StringComparison.Ordinal) ||
                             normalized.Contains("network", StringComparison.Ordinal) ||
                             normalized.Contains("fetch failed", StringComparison.Ordinal);
        if (isNetworkError && _startupActivityRank == DshStartupActivityRank.UpdateCheck)
        {
            return new DshStartupFailureHint(
                "检查 DSH 更新时网络错误",
                "无法连接 npm registry，请检查网络、代理或 npm registry 配置。",
                "检查网络、代理或 npm registry 配置后重试。");
        }

        if (_startupActivityRank == DshStartupActivityRank.Updating)
        {
            return new DshStartupFailureHint(
                "更新 DSH 失败",
                "npx 无法下载或安装 DSH 依赖。",
                "打开运行日志检查 npm 错误后重试。");
        }

        if (_startupActivityRank == DshStartupActivityRank.UpdateCheck)
        {
            return new DshStartupFailureHint(
                "检查 DSH 更新失败",
                "npx 无法完成 DSH 更新检查。",
                "打开运行日志检查 npm 错误后重试。");
        }

        return new DshStartupFailureHint(
            "DSH 启动失败",
            "DSH 启动过程报告错误。",
            "打开运行日志检查 DSH 错误后重试。");
    }

    private static DshStartupActivity? ClassifyProcessOutput(string line)
    {
        var normalized = line.Trim().ToLowerInvariant();
        if (normalized.Contains("fetch", StringComparison.Ordinal) ||
            normalized.Contains("download", StringComparison.Ordinal) ||
            normalized.Contains("tarball", StringComparison.Ordinal))
        {
            return new DshStartupActivity(
                DshStartupActivityRank.Updating,
                "正在更新 DSH",
                "正在下载 DSH 及其依赖。");
        }

        if (normalized.Contains("extract", StringComparison.Ordinal) ||
            normalized.Contains("unpack", StringComparison.Ordinal))
        {
            return new DshStartupActivity(
                DshStartupActivityRank.Updating,
                "正在更新 DSH",
                "正在解压 DSH 依赖。");
        }

        if (normalized.Contains("install", StringComparison.Ordinal) ||
            normalized.Contains("added", StringComparison.Ordinal) ||
            normalized.Contains("resolve", StringComparison.Ordinal))
        {
            return new DshStartupActivity(
                DshStartupActivityRank.Updating,
                "正在更新 DSH",
                "正在解析并安装 DSH 依赖。");
        }

        if (normalized.Contains("listen", StringComparison.Ordinal) ||
            normalized.Contains("server", StringComparison.Ordinal) ||
            normalized.Contains("localhost", StringComparison.Ordinal))
        {
            return new DshStartupActivity(
                DshStartupActivityRank.StartingService,
                "正在启动 DSH 服务",
                "DSH 服务已启动，正在验证本地 Web 界面。");
        }

        return null;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟"
            : elapsed.TotalMinutes >= 1
                ? $"{elapsed.Minutes} 分钟 {elapsed.Seconds} 秒"
                : $"{Math.Max(0, elapsed.Seconds)} 秒";

    private Task WriteProcessOutputAsync(string line, bool isError) =>
        isError
            ? _log.WarningAsync(AppLogStream.Runtime, "dsh-process-stderr", line)
            : _log.InformationAsync(AppLogStream.Runtime, "dsh-process-stdout", line);

    private void TransitionTo(
        DshSupervisorStatus status,
        DshRuntimeState? runtimeState,
        string? detail,
        string? activityTitle = null)
    {
        _status = status;
        _runtimeState = runtimeState;
        _detail = detail;
        _activityTitle = activityTitle;
        StateChanged?.Invoke(this, new DshSupervisorSnapshot(status, runtimeState, detail, activityTitle));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            if (_ownsHealthProbe && _healthProbe is IDisposable disposableHealthProbe)
            {
                disposableHealthProbe.Dispose();
            }
        }
    }

    private sealed record DshStartupPolicy(TimeSpan? Timeout, bool IsInstallation);

    private enum DshStartupActivityRank
    {
        Environment,
        UpdateCheck,
        Updating,
        StartingService
    }

    private sealed record DshStartupActivity(
        DshStartupActivityRank Rank,
        string Title,
        string Detail);

    private sealed record DshStartupFailureHint(
        string Title,
        string Detail,
        string Remediation);
}

public sealed class SystemDshExecutableValidator : IDshExecutableValidator
{
    private readonly TimeSpan _timeout;

    public SystemDshExecutableValidator(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
    }

    public async Task<DshExecutableValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var node = await ValidateCommandAsync("node", "请安装受支持的系统 Node.js，并将 node 加入 PATH。", cancellationToken)
            .ConfigureAwait(false);
        var npx = await ValidateCommandAsync("npx", "请安装提供 npx 的 npm 发行版，并将 npx 加入 PATH。", cancellationToken)
            .ConfigureAwait(false);
        return new DshExecutableValidationResult(node, npx);
    }

    private async Task<DshCommandProbeResult> ValidateCommandAsync(string command, string remediation, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(command);
        if (executable is null)
        {
            return new DshCommandProbeResult(command, null, null, $"在 PATH 中找不到 {command}。", remediation);
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return new DshCommandProbeResult(command, executable, null, $"无法启动 {command}。", remediation);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(_timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var detail = string.IsNullOrWhiteSpace(error) ? $"{command} --version 以退出代码 {process.ExitCode} 结束。" : error;
                return new DshCommandProbeResult(command, executable, null, detail, remediation);
            }

            return new DshCommandProbeResult(command, executable, output, null, remediation);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DshCommandProbeResult(command, executable, null, $"{command} --version 执行超时。", remediation);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new DshCommandProbeResult(command, executable, null, $"无法执行 {command}：{exception.Message}", remediation);
        }
    }

    private static string? FindExecutable(string command)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { $"{command}.exe", $"{command}.cmd", $"{command}.bat", command }
            : new[] { command };
        var rawPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        foreach (var directory in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var executable = Path.Combine(directory, candidate);
                if (File.Exists(executable))
                {
                    return executable;
                }
            }
        }

        return null;
    }
}

public sealed class SystemDshProcessLauncher : IDshProcessLauncher
{
    public Task<IDshProcessHandle> LaunchAsync(DshProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = request.NpxExecutable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--yes");
        startInfo.ArgumentList.Add("@deepseek-ai/dsh");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                request.OutputReceived(eventArgs.Data, false);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                request.OutputReceived(eventArgs.Data, true);
            }
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("npx did not start a DSH process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return Task.FromResult<IDshProcessHandle>(new SystemDshProcessHandle(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

public sealed class DshHttpHealthProbe : IDshHealthProbe, IDisposable
{
    private const int MaximumDocumentBytes = 256 * 1024;
    private readonly HttpClient _client;

    public DshHttpHealthProbe(TimeSpan timeout)
    {
        _client = new HttpClient { Timeout = timeout };
    }

    public async Task<DshHealthCheckResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new DshHealthCheckResult(false, $"DSH endpoint returned HTTP {(int)response.StatusCode}.");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            return new DshHealthCheckResult(false, "DSH endpoint returned successful HTTP but not an HTML Web UI response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await ReadBoundedDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        return HasDshPageSignature(document)
            ? new DshHealthCheckResult(true, "DSH Web UI identity was verified.")
            : new DshHealthCheckResult(false, "The loopback endpoint did not expose the expected DSH Web UI page signature.");
    }

    private static async Task<string> ReadBoundedDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        int read;
        while (content.Length < MaximumDocumentBytes &&
               (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumDocumentBytes - content.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    private static bool HasDshPageSignature(string document)
    {
        var hasTitle = document.Contains("<title", StringComparison.OrdinalIgnoreCase);
        var hasDeepSeek = document.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
        var hasHarness = document.Contains("harness", StringComparison.OrdinalIgnoreCase) ||
            document.Contains(">dsh<", StringComparison.OrdinalIgnoreCase) ||
            document.Contains(" dsh ", StringComparison.OrdinalIgnoreCase);
        return hasTitle && hasDeepSeek && hasHarness;
    }

    public void Dispose() => _client.Dispose();
}

public sealed class TcpLoopbackPortReservationProvider : ILoopbackPortReservationProvider
{
    public Task<ILoopbackPortReservation?> TryReserveAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return Task.FromResult<ILoopbackPortReservation?>(new TcpLoopbackPortReservation(listener));
        }
        catch (SocketException)
        {
            return Task.FromResult<ILoopbackPortReservation?>(null);
        }
    }

    public Task<ILoopbackPortReservation> ReserveEphemeralAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult<ILoopbackPortReservation>(new TcpLoopbackPortReservation(listener));
    }

    private sealed class TcpLoopbackPortReservation(TcpListener listener) : ILoopbackPortReservation
    {
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public ValueTask DisposeAsync()
        {
            listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class SystemDshProcessHandle : IDshProcessHandle
{
    private readonly Process _process;

    public SystemDshProcessHandle(Process process)
    {
        _process = process;
        StartedAtUtc = process.StartTime.ToUniversalTime();
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public int ProcessId => _process.Id;

    public DateTimeOffset StartedAtUtc { get; }

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public event EventHandler? Exited;

    public Task<bool> RequestGracefulStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process.HasExited)
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(_process.CloseMainWindow());
    }

    public Task ForceStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        return Task.CompletedTask;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.HasExited ? Task.CompletedTask : _process.WaitForExitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
