using DshNgDesktop.Core;
using DshNgDesktop.Diagnostics;
using DshNgDesktop.Dsh;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Installer;

/// <summary>
/// Owns the entire pre-commit installer transaction. The coordinator has no UI
/// dependency, which keeps all failure and cancellation paths deterministic in
/// automated tests and prevents views from bypassing compensation steps.
/// </summary>
public sealed class SetupCoordinator : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly ApplicationStateMachine _stateMachine;
    private readonly IInstallerPreflight _doctor;
    private readonly IClientDeployment _deployment;
    private readonly IDshRuntimeSupervisor _dshSupervisor;
    private readonly IPlatformServices _platformServices;
    private readonly AppLog _log;
    private readonly ProductDataCleaner _dataCleaner;
    private readonly SetupCoordinatorOptions _options;
    private readonly CancellationTokenSource _stopSource = new();
    private int _runStarted;
    private bool _disposed;
    private bool _startupRegistrationAttempted;
    private bool _installationRegistrationAttempted;
    private InstallManifest? _manifest;
    private ClientDeploymentResult? _deploymentResult;
    private SetupResult? _result;

    public SetupCoordinator(
        AppPaths paths,
        ApplicationStateMachine stateMachine,
        IInstallerPreflight doctor,
        IClientDeployment deployment,
        IDshRuntimeSupervisor dshSupervisor,
        IPlatformServices platformServices,
        AppLog log,
        ProductDataCleaner dataCleaner,
        SetupCoordinatorOptions options)
    {
        _paths = paths;
        _stateMachine = stateMachine;
        _doctor = doctor;
        _deployment = deployment;
        _dshSupervisor = dshSupervisor;
        _platformServices = platformServices;
        _log = log;
        _dataCleaner = dataCleaner;
        _options = options;
    }

    public event EventHandler<SetupProgress>? ProgressChanged;

    public ApplicationStateSnapshot State => _stateMachine.Snapshot;

    public SetupResult? Result => _result;

    public void RequestStop() => _stopSource.Cancel();

    public async Task<SetupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The installation transaction can only run once.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSource.Token);
        var transactionToken = linkedCancellation.Token;
        try
        {
            Transition(ApplicationState.Preflight, SetupStage.Preflight, "Checking this computer", "Checking Node.js, npx, storage and desktop prerequisites.");
            var preflight = await _doctor.RunInstallerPreflightAsync(transactionToken).ConfigureAwait(false);
            await LogPreflightAsync(preflight, transactionToken).ConfigureAwait(false);
            var blockingChecks = preflight.Checks.Where(check => check.Severity == DiagnosticSeverity.Error).ToList();
            if (blockingChecks.Count > 0)
            {
                var firstFailure = blockingChecks[0];
                return await FailAndRollbackAsync(
                    firstFailure.Detail,
                    firstFailure.Remediation,
                    wasCancelled: false).ConfigureAwait(false);
            }

            EnsureNoExistingProductData();

            Transition(ApplicationState.DeployingClient, SetupStage.DeployingClient, "Installing DSH Desktop", "Copying the client into its current-user installation directory.");
            _deploymentResult = await _deployment.DeployAsync(
                new ClientDeploymentRequest(_options.PayloadDirectory, _paths.InstallRoot),
                transactionToken).ConfigureAwait(false);
            _manifest = InstallManifest.Create(_paths);
            await _log.InformationAsync(AppLogStream.Installation, "setup-client-deployed", "The client payload was copied to the transaction-owned installation directory.", transactionToken)
                .ConfigureAwait(false);

            Transition(ApplicationState.ProvisioningDsh, SetupStage.ProvisioningDsh, "Preparing DeepSeek Harness", "Starting npx with the product-private cache and DSH home.");
            // DshSupervisor validates both the npx launch and the Web UI. The
            // state changes before this call make the native progress view show
            // the two meaningful user-facing phases without faking download %.
            Transition(ApplicationState.WaitingForWebUi, SetupStage.WaitingForWebUi, "Waiting for the local Web UI", "Verifying the DSH page identity on its private loopback address.");
            var started = await _dshSupervisor.StartAsync(transactionToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                return await FailAndRollbackAsync(started.Detail, started.Remediation, wasCancelled: started.Failure == DshStartFailure.Cancelled)
                    .ConfigureAwait(false);
            }

            Transition(ApplicationState.Registering, SetupStage.Registering, "Registering DSH Desktop", "Registering current-user login startup and the platform uninstall entry.");
            _startupRegistrationAttempted = true;
            var startup = await _platformServices.RegisterStartupAsync(
                new StartupRegistration(_paths.ProductId, _options.InstalledExecutablePath, ["--background"]),
                transactionToken).ConfigureAwait(false);
            EnsureSucceeded(startup, "The current-user startup entry could not be registered.");

            _installationRegistrationAttempted = true;
            var installation = await _platformServices.RegisterInstallationAsync(
                new InstallationRegistration(_paths.ProductId, _options.DisplayName, _paths.InstallRoot, _options.UninstallCommand),
                transactionToken).ConfigureAwait(false);
            EnsureSucceeded(installation, "The platform uninstall entry could not be registered.");

            await _manifest.SaveAsync(_paths, transactionToken).ConfigureAwait(false);
            await _log.InformationAsync(AppLogStream.Installation, "setup-registered", "Startup, uninstall registration and the installation manifest were committed.", transactionToken)
                .ConfigureAwait(false);
            Transition(ApplicationState.Committed, SetupStage.Committed, "Installation complete", "DSH Desktop is installed and the local DSH Web UI passed its health check.", isTerminal: true);
            _result = SetupResult.Success();
            return _result;
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return await FailAndRollbackAsync(
                "Installation was stopped before it could be committed.",
                "Review the installation log, resolve any reported prerequisite issue, then run the installer again.",
                wasCancelled: true).ConfigureAwait(false);
        }
        catch (SetupOperationException exception)
        {
            return await FailAndRollbackAsync(exception.Message, exception.Remediation, wasCancelled: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return await FailAndRollbackAsync(
                $"DSH Desktop could not finish installation: {exception.Message}",
                "Open the installation log, correct the reported problem, then run the installer again.",
                wasCancelled: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The failed-installation page calls this only after the user has copied
    /// diagnostics or opened the log directory. It removes the temporary log
    /// retention allowed by the rollback policy.
    /// </summary>
    public Task FinalizeFailedInstallationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _result is { Succeeded: false }
            ? _dataCleaner.DeleteRetainedInstallationLogsAsync(cancellationToken)
            : Task.CompletedTask;
    }

    private async Task LogPreflightAsync(EnvironmentDiagnosticReport report, CancellationToken cancellationToken)
    {
        foreach (var check in report.Checks)
        {
            var message = $"{check.Code}: {check.Detail}";
            if (check.Severity == DiagnosticSeverity.Error)
            {
                await _log.ErrorAsync(AppLogStream.Installation, "setup-preflight-error", message, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (check.Severity == DiagnosticSeverity.Warning)
            {
                await _log.WarningAsync(AppLogStream.Installation, "setup-preflight-warning", message, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _log.InformationAsync(AppLogStream.Installation, "setup-preflight-info", message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<SetupResult> FailAndRollbackAsync(string summary, string? remediation, bool wasCancelled)
    {
        var rollbackErrors = new List<string>();
        TryTransitionToStopping();
        Publish(SetupStage.Stopping, "Stopping installation", "Stopping only the DSH process tree created by this transaction.");

        try
        {
            await _dshSupervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            rollbackErrors.Add($"DSH stop: {exception.Message}");
        }

        if (_startupRegistrationAttempted)
        {
            await CompensateAsync(
                () => _platformServices.UnregisterStartupAsync(_paths.ProductId, CancellationToken.None),
                "startup registration",
                rollbackErrors).ConfigureAwait(false);
        }

        if (_installationRegistrationAttempted)
        {
            await CompensateAsync(
                () => _platformServices.UnregisterInstallationAsync(_paths.ProductId, CancellationToken.None),
                "uninstall registration",
                rollbackErrors).ConfigureAwait(false);
        }

        if (_stateMachine.Snapshot.State == ApplicationState.Stopping)
        {
            Transition(ApplicationState.RollingBack, SetupStage.RollingBack, "Rolling back installation", "Removing only files and registrations created by this installation attempt.");
        }

        if (_manifest is not null)
        {
            try
            {
                await _dataCleaner.CleanAsync(_manifest, preserveInstallationLogs: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                rollbackErrors.Add($"product data cleanup: {exception.Message}");
            }
        }

        if (_deploymentResult is not null)
        {
            try
            {
                await _deployment.RollbackAsync(_deploymentResult, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                rollbackErrors.Add($"client deployment cleanup: {exception.Message}");
            }
        }

        if (_stateMachine.Snapshot.State == ApplicationState.RollingBack)
        {
            Transition(ApplicationState.Failed, SetupStage.Failed, "Installation did not complete", summary, isTerminal: true);
        }

        if (rollbackErrors.Count > 0)
        {
            summary = $"{summary} Rollback needs attention: {string.Join("; ", rollbackErrors)}";
            remediation = "Review the retained installation log before retrying. Only the listed product paths may need manual cleanup.";
        }

        await _log.ErrorAsync(AppLogStream.Installation, "setup-failed", summary, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        _result = SetupResult.Failure(summary, remediation, wasCancelled);
        return _result;
    }

    private static void EnsureSucceeded(PlatformOperationResult result, string prefix)
    {
        if (!result.Succeeded)
        {
            throw new SetupOperationException($"{prefix} {result.Error}", "Check your current-user permissions, then run the installer again.");
        }
    }

    private void EnsureNoExistingProductData()
    {
        if (File.Exists(_paths.InstallManifestPath))
        {
            throw new SetupOperationException(
                "DSH Desktop is already installed or its previous installation state has not been removed.",
                "Use the system uninstall entry before installing again; the installer will not overwrite an existing product state.");
        }

        var existingManagedData = new[]
        {
            _paths.StateDirectory,
            _paths.NpmCacheDirectory,
            _paths.DshHomeDirectory,
            _paths.LauncherWorkingDirectory,
            _paths.WebViewDataDirectory
        }.FirstOrDefault(Directory.Exists);
        if (existingManagedData is not null)
        {
            throw new SetupOperationException(
                "Existing DSH Desktop product data was found, so this installation cannot safely overwrite it.",
                "Use the system uninstall entry or inspect the retained installation log before retrying.");
        }
    }

    private async Task CompensateAsync(
        Func<Task<PlatformOperationResult>> action,
        string resource,
        ICollection<string> rollbackErrors)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            if (!result.Succeeded)
            {
                rollbackErrors.Add($"{resource}: {result.Error}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            rollbackErrors.Add($"{resource}: {exception.Message}");
        }
    }

    private void TryTransitionToStopping()
    {
        var current = _stateMachine.Snapshot.State;
        if (current is ApplicationState.Preflight or ApplicationState.DeployingClient or ApplicationState.ProvisioningDsh or ApplicationState.WaitingForWebUi or ApplicationState.Registering)
        {
            Transition(ApplicationState.Stopping, SetupStage.Stopping, "Stopping installation", "Cancelling the uncommitted installation transaction.");
        }
    }

    private void Transition(ApplicationState state, SetupStage stage, string title, string detail, bool isTerminal = false)
    {
        _stateMachine.TransitionTo(state, detail);
        Publish(stage, title, detail, isTerminal);
        _ = _log.InformationAsync(AppLogStream.Installation, $"setup-{stage.ToString().ToLowerInvariant()}", detail);
    }

    private void Publish(SetupStage stage, string title, string detail, bool isTerminal = false) =>
        ProgressChanged?.Invoke(this, new SetupProgress(stage, title, detail, isTerminal));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _stopSource.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class SetupOperationException(string message, string remediation) : Exception(message)
    {
        public string Remediation { get; } = remediation;
    }
}
