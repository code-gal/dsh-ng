using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DshNgDesktop.Infrastructure;

public sealed class ActivationRequestedEventArgs(string command) : EventArgs
{
    public string Command { get; } = command;
}

/// <summary>
/// Owns one per-user mutex and a local named-pipe listener. A later launch can
/// request activation, but cannot create a second DSH supervisor.
/// </summary>
public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string ActivationCommand = "activate";
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _isPrimary;

    public SingleInstanceCoordinator(string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        var nameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{productId}:{Environment.UserName}")))[..24];
        _mutexName = $"dsh-ng-desktop-{nameHash}-instance";
        _pipeName = $"dsh-ng-desktop-{nameHash}-activation";
    }

    public event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;

    public bool IsPrimary => _isPrimary;

    public bool TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            throw new InvalidOperationException("This coordinator has already attempted instance acquisition.");
        }

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

    public async Task<bool> RequestActivationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(1_500, cancellationToken).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(ActivationCommand);
            await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
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
        _isPrimary = false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[32];
                var bytesRead = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                var command = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (string.Equals(command, ActivationCommand, StringComparison.Ordinal))
                {
                    ActivationRequested?.Invoke(this, new ActivationRequestedEventArgs(command));
                }
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
}
