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
    private readonly List<Task> _pendingProgressLogs = [];
    private int _runStarted;
    private bool _disposed;
    private ExistingDataHandling _existingDataHandling = ExistingDataHandling.RequireUserChoice;
    private bool _replaceExistingInstallRoot;
    private bool _preserveExistingDataOnRollback;
    private bool _retainExistingRegistrations;
    private bool _startupRegistrationAttempted;
    private bool _shortcutRegistrationAttempted;
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

    public void SelectExistingDataHandling(ExistingDataHandling handling)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _runStarted) != 0)
        {
            throw new InvalidOperationException("必须在安装开始前选择旧数据的处理方式。");
        }

        _existingDataHandling = handling;
    }

    public void RequestStop() => _stopSource.Cancel();

    public async Task<SetupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("安装事务只能运行一次。");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSource.Token);
        var transactionToken = linkedCancellation.Token;
        try
        {
            Transition(ApplicationState.Preflight, SetupStage.Preflight, "正在检查此计算机", "正在检查 Node.js、npx、存储空间和桌面组件前置条件。");
            var preflight = await _doctor.RunInstallerPreflightAsync(transactionToken).ConfigureAwait(false);
            await LogPreflightAsync(preflight, transactionToken).ConfigureAwait(false);
            await FlushProgressLogsAsync().ConfigureAwait(false);
            var blockingChecks = preflight.Checks.Where(check => check.Severity == DiagnosticSeverity.Error).ToList();
            if (blockingChecks.Count > 0)
            {
                var firstFailure = blockingChecks[0];
                return await FailAndRollbackAsync(
                    firstFailure.Detail,
                    firstFailure.Remediation,
                    wasCancelled: false).ConfigureAwait(false);
            }

            await PrepareExistingProductDataAsync(transactionToken).ConfigureAwait(false);

            Transition(ApplicationState.DeployingClient, SetupStage.DeployingClient, "正在安装 DSH Desktop", "正在将客户端复制到当前用户的安装目录。");
            _deploymentResult = await _deployment.DeployAsync(
                new ClientDeploymentRequest(_options.PayloadDirectory, _paths.InstallRoot, _replaceExistingInstallRoot),
                transactionToken).ConfigureAwait(false);
            _manifest = InstallManifest.Create(_paths);
            await _log.InformationAsync(AppLogStream.Installation, "setup-client-deployed", "客户端负载已复制到本次事务拥有的安装目录。", transactionToken)
                .ConfigureAwait(false);

            Transition(ApplicationState.ProvisioningDsh, SetupStage.ProvisioningDsh, "正在准备 DSH", "正在使用产品私有缓存和 DSH 主目录启动 npx。首次供应会持续等待至完成、进程退出或您手动停止。");
            // DshSupervisor validates both the npx launch and the Web UI. The
            // state changes before this call make the native progress view show
            // the two meaningful user-facing phases without faking download %.
            Transition(ApplicationState.WaitingForWebUi, SetupStage.WaitingForWebUi, "正在检查 DSH 更新", "npx 正在检查 DSH 的本地缓存和可用版本；网络较慢时可继续等待，随时可以停止安装。");
            var started = await StartDshForInstallationAsync(transactionToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                return await FailAndRollbackAsync(started.Detail, started.Remediation, wasCancelled: started.Failure == DshStartFailure.Cancelled)
                    .ConfigureAwait(false);
            }

            Transition(ApplicationState.Registering, SetupStage.Registering, "正在注册 DSH Desktop", "正在注册当前用户开机启动、桌面/开始菜单快捷方式和系统卸载入口。");
            if (!_retainExistingRegistrations)
            {
                _startupRegistrationAttempted = true;
                var startup = await _platformServices.RegisterStartupAsync(
                    new StartupRegistration(_paths.ProductId, _options.InstalledExecutablePath, ["--background"]),
                    transactionToken).ConfigureAwait(false);
                EnsureSucceeded(startup, "无法注册当前用户开机启动项。");
            }

            _shortcutRegistrationAttempted = true;
            var shortcuts = await _platformServices.RegisterShortcutsAsync(
                new ShortcutRegistration(
                    _options.DisplayName,
                    _options.InstalledExecutablePath,
                    _paths.InstallRoot,
                    "打开 DSH Desktop"),
                transactionToken).ConfigureAwait(false);
            EnsureSucceeded(shortcuts, "无法创建当前用户桌面和开始菜单快捷方式。");

            if (!_retainExistingRegistrations)
            {
                _installationRegistrationAttempted = true;
                var installation = await _platformServices.RegisterInstallationAsync(
                    new InstallationRegistration(_paths.ProductId, _options.DisplayName, _paths.InstallRoot, _options.UninstallCommand),
                    transactionToken).ConfigureAwait(false);
                EnsureSucceeded(installation, "无法注册系统卸载入口。");
            }

            await _manifest.SaveAsync(_paths, transactionToken).ConfigureAwait(false);
            await CompleteClientReplacementAsync(CancellationToken.None).ConfigureAwait(false);
            await _log.InformationAsync(AppLogStream.Installation, "setup-registered", "已提交开机启动、桌面/开始菜单快捷方式、卸载注册和安装清单。", transactionToken)
                .ConfigureAwait(false);
            Transition(ApplicationState.Committed, SetupStage.Committed, "安装完成", "DSH Desktop 已安装完成，本地 DSH Web 界面健康检查已通过。", isTerminal: true);
            await FlushProgressLogsAsync().ConfigureAwait(false);
            _result = SetupResult.Success();
            return _result;
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return await FailAndRollbackAsync(
                "安装在提交前已停止。",
                "请查看安装日志，解决报告的前置条件问题后重新运行安装器。",
                wasCancelled: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return await FailAndRollbackAsync(
                $"安装操作被意外取消：{exception.Message}",
                "请查看安装和运行日志，确认网络与 DSH 前置条件后重新运行安装器。",
                wasCancelled: false).ConfigureAwait(false);
        }
        catch (SetupOperationException exception)
        {
            return await FailAndRollbackAsync(exception.Message, exception.Remediation, wasCancelled: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return await FailAndRollbackAsync(
                $"DSH Desktop 无法完成安装：{exception.Message}",
                "请打开安装日志，处理报告的问题后重新运行安装器。",
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
        Publish(SetupStage.Stopping, "正在停止安装", "仅停止本次事务创建的 DSH 进程树。");

        try
        {
            await _dshSupervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            rollbackErrors.Add($"停止 DSH：{exception.Message}");
        }

        if (_installationRegistrationAttempted)
        {
            await CompensateAsync(
                () => _platformServices.UnregisterInstallationAsync(_paths.ProductId, CancellationToken.None),
                "系统卸载入口",
                rollbackErrors).ConfigureAwait(false);
        }

        if (_shortcutRegistrationAttempted && !_retainExistingRegistrations)
        {
            await CompensateAsync(
                () => _platformServices.UnregisterShortcutsAsync(_options.DisplayName, CancellationToken.None),
                "桌面和开始菜单快捷方式",
                rollbackErrors).ConfigureAwait(false);
        }

        if (_startupRegistrationAttempted)
        {
            await CompensateAsync(
                () => _platformServices.UnregisterStartupAsync(_paths.ProductId, CancellationToken.None),
                "开机启动项",
                rollbackErrors).ConfigureAwait(false);
        }

        if (_stateMachine.Snapshot.State == ApplicationState.Stopping)
        {
            Transition(ApplicationState.RollingBack, SetupStage.RollingBack, "正在回滚安装", "仅移除本次安装创建的文件和注册项。");
        }

        await FlushProgressLogsAsync().ConfigureAwait(false);

        if (_manifest is not null && !_preserveExistingDataOnRollback)
        {
            try
            {
                await _dataCleaner.CleanAsync(_manifest, preserveInstallationLogs: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                rollbackErrors.Add($"产品数据清理：{exception.Message}");
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
                rollbackErrors.Add($"客户端文件清理：{exception.Message}");
            }
        }

        if (_stateMachine.Snapshot.State == ApplicationState.RollingBack)
        {
            Transition(ApplicationState.Failed, SetupStage.Failed, "安装未完成", summary, isTerminal: true);
        }

        if (rollbackErrors.Count > 0)
        {
            summary = $"{summary} 回滚仍需处理：{string.Join("；", rollbackErrors)}";
            remediation = "重试前请查看保留的安装日志。可能仅需要手动处理所列出的产品路径。";
        }

        await _log.ErrorAsync(AppLogStream.Installation, "setup-failed", summary, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await FlushProgressLogsAsync().ConfigureAwait(false);
        _result = SetupResult.Failure(summary, remediation, wasCancelled);
        return _result;
    }

    private static void EnsureSucceeded(PlatformOperationResult result, string prefix)
    {
        if (!result.Succeeded)
        {
            throw new SetupOperationException($"{prefix} {result.Error}", "请检查当前用户权限后重新运行安装器。");
        }
    }

    private async Task PrepareExistingProductDataAsync(CancellationToken cancellationToken)
    {
        var state = SetupLocations.InspectExistingProductData(_paths);
        if (state == ExistingProductDataState.None)
        {
            return;
        }

        if (_existingDataHandling == ExistingDataHandling.RequireUserChoice)
        {
            throw new SetupOperationException(
                "检测到已有 DSH Desktop 数据，请先选择覆盖安装或全新安装。",
                "覆盖安装会保留 DSH 数据；全新安装会删除全部产品数据后重新安装。");
        }

        if (state == ExistingProductDataState.UnverifiedManifest &&
            _existingDataHandling != ExistingDataHandling.FreshInstall)
        {
            throw new SetupOperationException(
                "现有安装清单无法验证，不能安全地覆盖安装。",
                "请选择全新安装，或打开安装位置和日志后手动检查。");
        }

        if (_existingDataHandling == ExistingDataHandling.ReplaceClientPreservingData)
        {
            if (File.Exists(_paths.InstallRoot))
            {
                throw new SetupOperationException(
                    "安装目标路径被同名文件占用，无法安全覆盖。",
                    "打开安装位置，移走该同名文件后重试。");
            }

            _replaceExistingInstallRoot = Directory.Exists(_paths.InstallRoot);
            _preserveExistingDataOnRollback = true;
            _retainExistingRegistrations = state == ExistingProductDataState.VerifiedInstallation;
            await _log.InformationAsync(
                    AppLogStream.Installation,
                    "setup-preserve-existing-data",
                    "用户选择覆盖安装：将替换客户端文件并保留 DSH 配置、会话、插件、缓存和日志。",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var startupRemoval = await _platformServices.UnregisterStartupAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
            EnsureRecoveryRemovalSucceeded(startupRemoval, "开机启动项");
            var shortcutRemoval = await _platformServices.UnregisterShortcutsAsync(_options.DisplayName, cancellationToken).ConfigureAwait(false);
            EnsureRecoveryRemovalSucceeded(shortcutRemoval, "桌面和开始菜单快捷方式");
            var installationRemoval = await _platformServices.UnregisterInstallationAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
            EnsureRecoveryRemovalSucceeded(installationRemoval, "系统卸载入口");
            await _dataCleaner.CleanAsync(
                    InstallManifest.Create(_paths),
                    preserveInstallationLogs: true,
                    includeInstallRoot: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Security.SecurityException)
        {
            throw new SetupOperationException(
                $"无法安全清理旧的 DSH Desktop 产品数据：{exception.Message}",
                "请打开安装位置和日志，处理所列产品路径后重试。");
        }

        await _log.WarningAsync(
                AppLogStream.Installation,
                "setup-fresh-installation-selected",
                "用户选择全新安装：已清理旧的产品受管路径。",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CompleteClientReplacementAsync(CancellationToken cancellationToken)
    {
        if (_deploymentResult is null)
        {
            return;
        }

        try
        {
            await _deployment.CommitAsync(_deploymentResult, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _log.WarningAsync(
                    AppLogStream.Installation,
                    "setup-replacement-backup-retained",
                    $"新客户端已提交，但旧客户端备份尚未删除：{exception.Message}",
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void EnsureRecoveryRemovalSucceeded(PlatformOperationResult result, string resource)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"无法删除旧的 {resource}：{result.Error}");
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
            Transition(ApplicationState.Stopping, SetupStage.Stopping, "正在停止安装", "正在取消尚未提交的安装事务。");
        }
    }

    private void Transition(ApplicationState state, SetupStage stage, string title, string detail, bool isTerminal = false)
    {
        _stateMachine.TransitionTo(state, detail);
        Publish(stage, title, detail, isTerminal);
        _pendingProgressLogs.Add(_log.InformationAsync(AppLogStream.Installation, $"setup-{stage.ToString().ToLowerInvariant()}", detail));
    }

    private async Task FlushProgressLogsAsync()
    {
        if (_pendingProgressLogs.Count == 0)
        {
            return;
        }

        var pending = _pendingProgressLogs.ToArray();
        _pendingProgressLogs.Clear();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task<DshStartResult> StartDshForInstallationAsync(CancellationToken cancellationToken)
    {
        if (_dshSupervisor is not IDshInstallationProgressSource installationProgressSource)
        {
            return await _dshSupervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        EventHandler<DshInstallationProgress> handler = (_, progress) =>
            Publish(
                SetupStage.WaitingForWebUi,
                progress.Title,
                progress.Detail,
                isHeartbeat: progress.IsHeartbeat);
        installationProgressSource.InstallationProgressChanged += handler;
        try
        {
            return await installationProgressSource.StartForInstallationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installationProgressSource.InstallationProgressChanged -= handler;
        }
    }

    private void Publish(SetupStage stage, string title, string detail, bool isTerminal = false, bool isHeartbeat = false) =>
        ProgressChanged?.Invoke(this, new SetupProgress(stage, title, detail, isTerminal, isHeartbeat));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await FlushProgressLogsAsync().ConfigureAwait(false);
            _stopSource.Dispose();
        }
    }

    private sealed class SetupOperationException(string message, string remediation) : Exception(message)
    {
        public string Remediation { get; } = remediation;
    }
}
