using DshNgDesktop.Dsh;
using DshNgDesktop.Infrastructure;

namespace DshNgDesktop.Core;

public enum DesktopRuntimeStatus
{
    Starting,
    Ready,
    Faulted,
    Stopping,
    Stopped
}

public sealed record DesktopRuntimeSnapshot(
    DesktopRuntimeStatus Status,
    Uri? WebUiUri,
    string Detail,
    string? Remediation = null,
    string? ActivityTitle = null);

/// <summary>
/// Owns the installed application's runtime state. UI code observes its
/// snapshots but never creates or stops the DSH process directly.
/// </summary>
public sealed class ApplicationCoordinator : IAsyncDisposable
{
    private readonly ApplicationStateMachine _stateMachine;
    private readonly DshSupervisor _supervisor;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public ApplicationCoordinator(
        ApplicationStateMachine stateMachine,
        DshSupervisor supervisor,
        AppLog log)
    {
        _stateMachine = stateMachine;
        _supervisor = supervisor;
        _log = log;
        _supervisor.StateChanged += Supervisor_OnStateChanged;
    }

    public event EventHandler<DesktopRuntimeSnapshot>? SnapshotChanged;

    public DesktopRuntimeSnapshot Snapshot { get; private set; } = new(
        DesktopRuntimeStatus.Stopped,
        null,
        "DSH Desktop 尚未启动本地服务。");

    public ApplicationStateSnapshot ApplicationState => _stateMachine.Snapshot;

    public Task<DesktopRuntimeSnapshot> StartAsync(CancellationToken cancellationToken = default) =>
        StartCoreAsync(isRetry: false, cancellationToken);

    public Task<DesktopRuntimeSnapshot> RetryAsync(CancellationToken cancellationToken = default) =>
        StartCoreAsync(isRetry: true, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.Status == DesktopRuntimeStatus.Stopped)
            {
                return;
            }

            TransitionToStopping("Stopping the DSH runtime before the desktop host exits.");
            Publish(new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Stopping,
                null,
                "Stopping the local DSH runtime."));
            await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Stopped, "DSH Desktop stopped.", out _);
            Publish(new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Stopped,
                null,
                "The local DSH runtime has stopped."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Failed, $"DSH Desktop could not stop: {exception.Message}", out _);
            await _log.ErrorAsync(AppLogStream.Runtime, "desktop-stop-failed", exception.Message, exception, CancellationToken.None)
                .ConfigureAwait(false);
            Publish(new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Faulted,
                null,
                $"DSH Desktop could not stop cleanly: {exception.Message}",
                "Inspect the runtime log before retrying or uninstalling.",
                "DSH 停止失败"));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<DesktopRuntimeSnapshot> StartCoreAsync(bool isRetry, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.Status == DesktopRuntimeStatus.Ready && _supervisor.Snapshot.RuntimeState is { } existingRuntime)
            {
                return Snapshot with { WebUiUri = CreateWebUiUri(existingRuntime) };
            }

            TransitionToStarting(isRetry
                ? "用户请求重试 DSH 启动。"
                : "正在启动本地 DSH 服务。");
            Publish(new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Starting,
                null,
                isRetry ? "正在重试 DSH 启动。" : "正在启动本地 DSH 服务。",
                ActivityTitle: "正在启动 DSH"));

            var result = await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.RuntimeState is { } runtimeState)
            {
                _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Ready, "DSH Web UI passed its health check.", out _);
                var ready = new DesktopRuntimeSnapshot(
                    DesktopRuntimeStatus.Ready,
                    CreateWebUiUri(runtimeState),
                    "DSH Web 界面已就绪。");
                Publish(ready);
                return ready;
            }

            var failure = new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Faulted,
                null,
                result.Detail,
                result.Remediation ?? "Inspect the runtime log and retry.",
                result.ActivityTitle ?? "DSH 启动失败");
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Failed, result.Detail, out _);
            Publish(failure);
            return failure;
        }
        catch (OperationCanceledException)
        {
            var cancelled = new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Faulted,
                null,
                "DSH 启动已取消。",
                "请在准备好后重试启动 DSH Desktop。",
                "DSH 启动已取消");
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Failed, cancelled.Detail, out _);
            Publish(cancelled);
            return cancelled;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void Supervisor_OnStateChanged(object? sender, DshSupervisorSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        if (snapshot.Status == DshSupervisorStatus.Starting && Snapshot.Status == DesktopRuntimeStatus.Starting)
        {
            Publish(new DesktopRuntimeSnapshot(
                DesktopRuntimeStatus.Starting,
                null,
                snapshot.Detail ?? "正在启动本地 DSH 服务。",
                ActivityTitle: snapshot.ActivityTitle ?? "正在启动 DSH"));
            return;
        }

        if (snapshot.Status != DshSupervisorStatus.Faulted)
        {
            return;
        }

        var detail = snapshot.Detail ?? "DSH exited unexpectedly.";
        _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Failed, detail, out _);
        Publish(new DesktopRuntimeSnapshot(
            DesktopRuntimeStatus.Faulted,
            null,
            detail,
            "Inspect the runtime log and retry. DSH Desktop did not restart DSH automatically.",
            snapshot.ActivityTitle ?? "DSH 运行异常"));
    }

    private void TransitionToStarting(string reason)
    {
        var state = _stateMachine.Snapshot.State;
        if (state != global::DshNgDesktop.Core.ApplicationState.Starting)
        {
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Starting, reason, out _);
        }
    }

    private void TransitionToStopping(string reason)
    {
        var state = _stateMachine.Snapshot.State;
        if (state != global::DshNgDesktop.Core.ApplicationState.Stopping)
        {
            _stateMachine.TryTransitionTo(global::DshNgDesktop.Core.ApplicationState.Stopping, reason, out _);
        }
    }

    private void Publish(DesktopRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static Uri CreateWebUiUri(DshRuntimeState runtimeState) =>
        new($"http://127.0.0.1:{runtimeState.Port}/", UriKind.Absolute);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _supervisor.StateChanged -= Supervisor_OnStateChanged;
            _operationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
