using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshNgDesktop.Core;

/// <summary>
/// Persisted diagnostic data for a DSH instance created by this application.
/// The record is never an ownership grant: only the live supervisor owns a
/// process and may stop it.
/// </summary>
public sealed record DshRuntimeState(
    int Port,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string InstanceId);

public sealed class DshRuntimeStateStore
{
    private readonly AppPaths _paths;

    public DshRuntimeStateStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<DshRuntimeState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.RuntimeStatePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_paths.RuntimeStatePath);
        return await JsonSerializer.DeserializeAsync(stream, DshRuntimeStateJsonContext.Default.DshRuntimeState, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(DshRuntimeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_paths.StateDirectory);

        var temporaryPath = $"{_paths.RuntimeStatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, DshRuntimeStateJsonContext.Default.DshRuntimeState, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _paths.RuntimeStatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_paths.RuntimeStatePath))
        {
            File.Delete(_paths.RuntimeStatePath);
        }

        return Task.CompletedTask;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DshRuntimeState))]
internal sealed partial class DshRuntimeStateJsonContext : JsonSerializerContext;
