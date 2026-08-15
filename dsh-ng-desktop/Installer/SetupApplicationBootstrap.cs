using DshNgDesktop.Core;
using DshNgDesktop.Diagnostics;
using DshNgDesktop.Dsh;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Installer;

/// <summary>
/// Keeps process argument parsing at the host edge and builds the explicit
/// setup dependency graph without a reflection-based service container.
/// </summary>
internal sealed record SetupApplicationBootstrap(AppPaths Paths, string? PayloadDirectory, bool ForceSetup)
{
    private static SetupApplicationBootstrap? _current;

    public static SetupApplicationBootstrap Current => _current
        ?? throw new InvalidOperationException("The desktop bootstrap was not configured.");

    public static void Configure(SetupApplicationBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        _current = bootstrap;
    }

    public SetupRuntime CreateRuntime(ApplicationStateMachine stateMachine)
    {
        var platformServices = PlatformServices.CreateDefault();
        var log = new AppLog(Paths);
        var supervisor = new DshSupervisor(Paths, platformServices, log);
        var coordinator = new SetupCoordinator(
            Paths,
            stateMachine,
            new EnvironmentDoctor(Paths, platformServices),
            new FileSystemClientDeployment(Paths),
            supervisor,
            platformServices,
            log,
            new ProductDataCleaner(Paths),
            SetupCoordinatorOptions.CreateDefault(Paths, PayloadDirectory));
        return new SetupRuntime(log, supervisor, coordinator);
    }
}

public sealed class SetupRuntime : IAsyncDisposable
{
    public SetupRuntime(AppLog log, DshSupervisor supervisor, SetupCoordinator coordinator)
    {
        Log = log;
        Supervisor = supervisor;
        Coordinator = coordinator;
    }

    public AppLog Log { get; }

    // Held for the application lifetime so a committed install keeps its
    // verified DSH process ownership boundary alive until the host exits.
    public DshSupervisor Supervisor { get; }

    public SetupCoordinator Coordinator { get; }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        await Log.DisposeAsync().ConfigureAwait(false);
    }
}
