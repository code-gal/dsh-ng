using DshNgDesktop.Dsh;
using DshNgDesktop.Infrastructure;

namespace DshNgDesktop.Core;

/// <summary>
/// Keeps the process supervisor and log alive for the full desktop lifetime.
/// A setup-created runtime can be adopted without starting a second DSH tree.
/// </summary>
public sealed class DesktopRuntime : IAsyncDisposable
{
    private readonly bool _ownsSupervisor;
    private readonly bool _ownsLog;
    private bool _disposed;

    public DesktopRuntime(
        AppPaths paths,
        ApplicationStateMachine stateMachine,
        DshSupervisor supervisor,
        AppLog log,
        bool ownsSupervisor,
        bool ownsLog)
    {
        Paths = paths;
        Supervisor = supervisor;
        Log = log;
        Coordinator = new ApplicationCoordinator(stateMachine, supervisor, log);
        _ownsSupervisor = ownsSupervisor;
        _ownsLog = ownsLog;
    }

    public AppPaths Paths { get; }

    public DshSupervisor Supervisor { get; }

    public AppLog Log { get; }

    public ApplicationCoordinator Coordinator { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        if (_ownsSupervisor)
        {
            await Supervisor.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsLog)
        {
            await Log.DisposeAsync().ConfigureAwait(false);
        }
    }
}
