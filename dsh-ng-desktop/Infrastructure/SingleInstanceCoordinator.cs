using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DshNgDesktop.Installer;

namespace DshNgDesktop.Infrastructure;

public sealed class InstanceCommandRequestedEventArgs(string command) : EventArgs
{
    public string Command { get; } = command;

    /// <summary>
    /// Shutdown handlers set this only after they have scheduled the owned
    /// runtime for shutdown. Activation commands are always accepted.
    /// </summary>
    public bool Accepted { get; set; }
}

public sealed record InstanceCommandRequestResult(bool Delivered, bool Accepted, string? Error = null);

/// <summary>
/// Owns one per-user mutex and a local named-pipe listener. A later launch can
/// request activation, but cannot create a second DSH supervisor.
/// </summary>
public sealed class SingleInstanceCoordinator : IAsyncDisposable, IInstallMaintenanceCoordinator
{
    private const string ActivationCommand = "activate";
    private const string UninstallCommand = "uninstall";
    private const string InstallMaintenanceCommand = "install-maintenance";
    private const string AcceptedResponse = "accepted";
    private const string RejectedResponse = "rejected";
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly string _maintenanceName;
    private Mutex? _mutex;
    private Semaphore? _maintenanceSemaphore;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _isPrimary;
    private int _maintenanceGateHeld;

    public SingleInstanceCoordinator(string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        var nameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{productId}:{Environment.UserName}")))[..24];
        _mutexName = $"dsh-ng-desktop-{nameHash}-instance";
        _pipeName = $"dsh-ng-desktop-{nameHash}-activation";
        _maintenanceName = $"dsh-ng-desktop-{nameHash}-maintenance";
    }

    public event EventHandler<InstanceCommandRequestedEventArgs>? ActivationRequested;

    public event EventHandler<InstanceCommandRequestedEventArgs>? UninstallRequested;

    public event EventHandler<InstanceCommandRequestedEventArgs>? InstallMaintenanceRequested;

    public bool IsPrimary => _isPrimary;

    public bool TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            throw new InvalidOperationException("This coordinator has already attempted instance acquisition.");
        }

        var gate = GetMaintenanceSemaphore();
        if (!gate.WaitOne(0))
        {
            return false;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _isPrimary = true;
            _listenerCancellation = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenAsync(_listenerCancellation.Token));
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RequestActivationAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestCommandAsync(ActivationCommand, cancellationToken).ConfigureAwait(false);
        return result.Delivered && result.Accepted;
    }

    /// <summary>
    /// Delivers an uninstall request to the existing instance. The response
    /// only confirms that the host accepted responsibility for shutdown; the
    /// uninstall helper must still acquire the instance mutex before deleting
    /// any product files.
    /// </summary>
    public Task<InstanceCommandRequestResult> RequestUninstallAsync(CancellationToken cancellationToken = default) =>
        RequestCommandAsync(UninstallCommand, cancellationToken);

    public Task<InstanceCommandRequestResult> RequestInstallMaintenanceAsync(CancellationToken cancellationToken = default) =>
        RequestCommandAsync(InstallMaintenanceCommand, cancellationToken);

    public async Task<InstallMaintenanceAcquisition> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var gate = GetMaintenanceSemaphore();
        var gateDeadline = DateTimeOffset.UtcNow + timeout;
        var acquired = false;
        while (DateTimeOffset.UtcNow < gateDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate.WaitOne(0))
            {
                acquired = true;
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        if (!acquired)
        {
            return InstallMaintenanceAcquisition.Failure("安装维护锁在限定时间内不可用，可能已有其他安装事务正在运行。");
        }

        Interlocked.Exchange(ref _maintenanceGateHeld, 1);
        try
        {
            var request = await RequestInstallMaintenanceAsync(cancellationToken).ConfigureAwait(false);
            if (request.Delivered && !request.Accepted)
            {
                ReleaseMaintenanceGate();
                return InstallMaintenanceAcquisition.Failure(request.Error ?? "正在运行的客户端拒绝安装维护请求。");
            }

            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsPrimaryMutexAvailable())
                {
                    return InstallMaintenanceAcquisition.Success(new MaintenanceLease(this));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            ReleaseMaintenanceGate();
            return InstallMaintenanceAcquisition.Failure("正在运行的客户端未在限定时间内退出。");
        }
        catch
        {
            ReleaseMaintenanceGate();
            throw;
        }
    }

    /// <summary>
    /// Attempts to own the same per-user mutex used by the desktop host. An
    /// uninstall helper holds this lease from the point at which no running
    /// host remains until cleanup ends, preventing a fresh client from opening
    /// files while they are being removed.
    /// </summary>
    public bool TryAcquireUninstallLock(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var gate = GetMaintenanceSemaphore();
        if (!gate.WaitOne(timeout))
        {
            return false;
        }

        Interlocked.Exchange(ref _maintenanceGateHeld, 1);
        _mutex ??= new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            var acquired = _mutex.WaitOne(timeout);
            if (!acquired)
            {
                ReleaseMaintenanceGate();
            }

            return acquired;
        }
        catch (AbandonedMutexException)
        {
            // The previous client died without releasing its handle. The OS
            // has transferred ownership, so cleanup may safely continue.
            return true;
        }
        catch
        {
            ReleaseMaintenanceGate();
            throw;
        }
    }

    private async Task<InstanceCommandRequestResult> RequestCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(1_500, cancellationToken).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(command);
            await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            var response = new byte[32];
            var bytesRead = await client.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var accepted = bytesRead > 0 && string.Equals(
                Encoding.UTF8.GetString(response, 0, bytesRead),
                AcceptedResponse,
                StringComparison.Ordinal);
            return new InstanceCommandRequestResult(true, accepted, accepted ? null : "The running DSH Desktop instance rejected the command.");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return new InstanceCommandRequestResult(false, false, exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listenerCancellation?.Cancel();

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the host lifetime ends.
            }
        }

        _listenerCancellation?.Dispose();
        // Closing the final handle releases the named mutex. Do not call
        // ReleaseMutex here: async host shutdown is permitted to continue on
        // a different thread than the one that acquired the mutex.
        _mutex?.Dispose();
        ReleaseMaintenanceGate();
        _maintenanceSemaphore?.Dispose();
        _isPrimary = false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[32];
                var bytesRead = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                var command = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var accepted = DispatchCommand(command);
                var response = Encoding.UTF8.GetBytes(accepted ? AcceptedResponse : RejectedResponse);
                await server.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // The peer disconnected before delivering a command. Continue listening.
            }
        }
    }

    private bool DispatchCommand(string command)
    {
        var args = new InstanceCommandRequestedEventArgs(command);
        if (string.Equals(command, ActivationCommand, StringComparison.Ordinal))
        {
            ActivationRequested?.Invoke(this, args);
            return true;
        }

        if (string.Equals(command, UninstallCommand, StringComparison.Ordinal) && UninstallRequested is { } uninstallRequested)
        {
            uninstallRequested.Invoke(this, args);
            return args.Accepted;
        }

        if (string.Equals(command, InstallMaintenanceCommand, StringComparison.Ordinal) && InstallMaintenanceRequested is { } installMaintenanceRequested)
        {
            installMaintenanceRequested.Invoke(this, args);
            return args.Accepted;
        }

        return false;
    }

    private Semaphore GetMaintenanceSemaphore() =>
        _maintenanceSemaphore ??= new Semaphore(1, 1, _maintenanceName);

    private bool IsPrimaryMutexAvailable()
    {
        using var probe = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            if (!probe.WaitOne(0))
            {
                return false;
            }

            probe.ReleaseMutex();
            return true;
        }
        catch (AbandonedMutexException)
        {
            try
            {
                probe.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The abandoned mutex has already been made available.
            }

            return true;
        }
    }

    private void ReleaseMaintenanceGate()
    {
        if (Interlocked.Exchange(ref _maintenanceGateHeld, 0) == 1)
        {
            _maintenanceSemaphore?.Release();
        }
    }

    private sealed class MaintenanceLease(SingleInstanceCoordinator owner) : IAsyncDisposable
    {
        private SingleInstanceCoordinator? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseMaintenanceGate();
            return ValueTask.CompletedTask;
        }
    }
}
