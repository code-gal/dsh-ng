using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
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
/// Owns one per-user instance lease and a local named-pipe listener. Windows
/// uses a named mutex; macOS uses an exclusive file lease. A later launch can
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
    private readonly string _macInstanceLeasePath;
    private readonly string _macMaintenanceLeasePath;
    private Mutex? _mutex;
    private Semaphore? _maintenanceSemaphore;
    private FileStream? _macInstanceLease;
    private FileStream? _macMaintenanceLease;
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
        // Do not use Path.GetTempPath() here. macOS assigns TMPDIR per login
        // session, so Finder, launchd and the pkg bootstrap could otherwise
        // coordinate through different lease files for the same user.
        var leaseDirectory = OperatingSystem.IsMacOS()
            ? Path.Combine("/tmp", "dsh-ng-desktop-locks", nameHash)
            : Path.Combine(Path.GetTempPath(), "dsh-ng-desktop-locks");
        _macInstanceLeasePath = Path.Combine(leaseDirectory, $"{nameHash}.instance.lock");
        _macMaintenanceLeasePath = Path.Combine(leaseDirectory, $"{nameHash}.maintenance.lock");
    }

    public event EventHandler<InstanceCommandRequestedEventArgs>? ActivationRequested;

    public event EventHandler<InstanceCommandRequestedEventArgs>? UninstallRequested;

    public event EventHandler<InstanceCommandRequestedEventArgs>? InstallMaintenanceRequested;

    public bool IsPrimary => _isPrimary;

    public bool TryAcquirePrimary()
    {
        if (_mutex is not null || _macInstanceLease is not null || _isPrimary)
        {
            throw new InvalidOperationException("This coordinator has already attempted instance acquisition.");
        }

        if (OperatingSystem.IsMacOS())
        {
            // A primary launch must pass through the same maintenance gate as
            // an installer. Otherwise a third client can claim the instance
            // lease after the old client exits but before replacement commits.
            using var maintenanceGate = TryAcquireFileLease(_macMaintenanceLeasePath, TimeSpan.Zero);
            if (maintenanceGate is null)
            {
                return false;
            }

            _macInstanceLease = TryAcquireFileLease(_macInstanceLeasePath, TimeSpan.Zero);
            if (_macInstanceLease is null)
            {
                return false;
            }

            StartPrimaryListener();
            return true;
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

            StartPrimaryListener();
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

        if (OperatingSystem.IsMacOS())
        {
            _macMaintenanceLease = await AcquireFileLeaseAsync(_macMaintenanceLeasePath, timeout, cancellationToken).ConfigureAwait(false);
            if (_macMaintenanceLease is null)
            {
                return InstallMaintenanceAcquisition.Failure("安装维护锁在限定时间内不可用，可能已有其他安装事务正在运行。");
            }

            Interlocked.Exchange(ref _maintenanceGateHeld, 1);
        }
        else
        {
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
        }

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
    /// Attempts to own the same per-user instance lease used by the desktop host. An
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

        if (OperatingSystem.IsMacOS())
        {
            _macMaintenanceLease = TryAcquireFileLease(_macMaintenanceLeasePath, timeout);
            if (_macMaintenanceLease is null)
            {
                return false;
            }

            Interlocked.Exchange(ref _maintenanceGateHeld, 1);
            try
            {
                _macInstanceLease = TryAcquireFileLease(_macInstanceLeasePath, timeout);
                if (_macInstanceLease is not null)
                {
                    return true;
                }

                ReleaseMaintenanceGate();
                return false;
            }
            catch
            {
                ReleaseMaintenanceGate();
                throw;
            }
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
        _macInstanceLease?.Dispose();
        _macInstanceLease = null;
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

    private void StartPrimaryListener()
    {
        _isPrimary = true;
        _listenerCancellation = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(_listenerCancellation.Token));
    }

    private Semaphore GetMaintenanceSemaphore()
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("macOS uses file-based instance and maintenance leases.");
        }

        return _maintenanceSemaphore ??= new Semaphore(1, 1, _maintenanceName);
    }

    private bool IsPrimaryMutexAvailable()
    {
        if (OperatingSystem.IsMacOS())
        {
            using var lease = TryAcquireFileLease(_macInstanceLeasePath, TimeSpan.Zero);
            return lease is not null;
        }

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
            if (OperatingSystem.IsMacOS())
            {
                _macMaintenanceLease?.Dispose();
                _macMaintenanceLease = null;
            }
            else
            {
                _maintenanceSemaphore?.Release();
            }
        }
    }

    private static FileStream? TryAcquireFileLease(string path, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (OperatingSystem.IsMacOS())
                {
                    EnsureMacLeaseDirectoryPermissions(Path.GetDirectoryName(path)!);
                }
                // On macOS FileShare.None is the supported cross-process
                // exclusive lease. FileStream.Lock is explicitly unsupported
                // by the platform annotations and cannot be used here.
                var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                if (OperatingSystem.IsMacOS())
                {
                    EnsureMacLeaseFilePermissions(path);
                }
                return lease;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                if (timeout == TimeSpan.Zero)
                {
                    return null;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
            catch (IOException)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private static void EnsureMacLeaseDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("macos")]
    private static void EnsureMacLeaseFilePermissions(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<FileStream?> AcquireFileLeaseAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return TryAcquireFileLease(path, timeout);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = TryAcquireFileLease(path, TimeSpan.Zero);
            if (lease is not null)
            {
                return lease;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        return null;
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
