namespace DshNgDesktop.Core;

/// <summary>
/// The explicit lifecycle shared by the installer and the desktop host.
/// A state is never inferred from a collection of booleans.
/// </summary>
public enum ApplicationState
{
    NotInstalled,
    Preflight,
    DeployingClient,
    ProvisioningDsh,
    WaitingForWebUi,
    Registering,
    Committed,
    Starting,
    Ready,
    Stopping,
    RollingBack,
    Failed,
    Stopped
}

public sealed record ApplicationStateSnapshot(
    ApplicationState State,
    long Sequence,
    DateTimeOffset ChangedAtUtc,
    string Reason);

public sealed class InvalidApplicationStateTransitionException : InvalidOperationException
{
    public InvalidApplicationStateTransitionException(ApplicationState current, ApplicationState requested)
        : base($"The application cannot transition from '{current}' to '{requested}'.")
    {
        Current = current;
        Requested = requested;
    }

    public ApplicationState Current { get; }

    public ApplicationState Requested { get; }
}

/// <summary>
/// Serializes lifecycle changes and rejects transitions which would violate the
/// install transaction or the normal runtime shutdown path.
/// </summary>
public sealed class ApplicationStateMachine
{
    private readonly object _gate = new();
    private ApplicationStateSnapshot _snapshot;

    public ApplicationStateMachine(ApplicationState initialState = ApplicationState.NotInstalled)
    {
        _snapshot = new ApplicationStateSnapshot(initialState, 0, DateTimeOffset.UtcNow, "Initial state");
    }

    public event EventHandler<ApplicationStateSnapshot>? StateChanged;

    public ApplicationStateSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public bool CanTransitionTo(ApplicationState next)
    {
        lock (_gate)
        {
            return IsAllowed(_snapshot.State, next);
        }
    }

    public ApplicationStateSnapshot TransitionTo(ApplicationState next, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        ApplicationStateSnapshot updated;
        lock (_gate)
        {
            if (!IsAllowed(_snapshot.State, next))
            {
                throw new InvalidApplicationStateTransitionException(_snapshot.State, next);
            }

            updated = new ApplicationStateSnapshot(next, _snapshot.Sequence + 1, DateTimeOffset.UtcNow, reason);
            _snapshot = updated;
        }

        StateChanged?.Invoke(this, updated);
        return updated;
    }

    public bool TryTransitionTo(ApplicationState next, string reason, out ApplicationStateSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            if (!IsAllowed(_snapshot.State, next))
            {
                snapshot = _snapshot;
                return false;
            }

            snapshot = new ApplicationStateSnapshot(next, _snapshot.Sequence + 1, DateTimeOffset.UtcNow, reason);
            _snapshot = snapshot;
        }

        StateChanged?.Invoke(this, snapshot);
        return true;
    }

    private static bool IsAllowed(ApplicationState current, ApplicationState next) => current switch
    {
        ApplicationState.NotInstalled => next is ApplicationState.Preflight or ApplicationState.Starting,
        ApplicationState.Preflight => next is ApplicationState.DeployingClient or ApplicationState.Stopping,
        ApplicationState.DeployingClient => next is ApplicationState.ProvisioningDsh or ApplicationState.Stopping,
        ApplicationState.ProvisioningDsh => next is ApplicationState.WaitingForWebUi or ApplicationState.Stopping,
        ApplicationState.WaitingForWebUi => next is ApplicationState.Registering or ApplicationState.Stopping,
        ApplicationState.Registering => next is ApplicationState.Committed or ApplicationState.Stopping,
        ApplicationState.Committed => next is ApplicationState.Starting or ApplicationState.Stopping,
        ApplicationState.Starting => next is ApplicationState.Ready or ApplicationState.Stopping or ApplicationState.Failed,
        ApplicationState.Ready => next is ApplicationState.Stopping or ApplicationState.Failed,
        ApplicationState.Stopping => next is ApplicationState.RollingBack or ApplicationState.Stopped or ApplicationState.Failed,
        ApplicationState.RollingBack => next is ApplicationState.Failed or ApplicationState.NotInstalled,
        ApplicationState.Failed => next is ApplicationState.Preflight or ApplicationState.Starting or ApplicationState.Stopping,
        ApplicationState.Stopped => next is ApplicationState.Preflight or ApplicationState.Starting or ApplicationState.NotInstalled,
        _ => false
    };
}
