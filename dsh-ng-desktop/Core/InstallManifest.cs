using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshNgDesktop.Core;

public sealed record ManagedPathRecord(ManagedPathKind Kind, string Path);

/// <summary>
/// A persisted allow-list for uninstall.  Cleanup must validate this manifest
/// against the current product paths before it can remove any directory.
/// </summary>
public sealed record InstallManifest(
    int SchemaVersion,
    string ProductId,
    DateTimeOffset CreatedAtUtc,
    List<ManagedPathRecord> ManagedPaths)
{
    public const int CurrentSchemaVersion = 1;

    public static InstallManifest Create(AppPaths paths) => new(
        CurrentSchemaVersion,
        paths.ProductId,
        DateTimeOffset.UtcNow,
        paths.ManagedPaths.Select(item => new ManagedPathRecord(item.Key, item.Value)).ToList());

    public async Task SaveAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        ValidateAgainst(paths);
        Directory.CreateDirectory(paths.StateDirectory);

        var json = JsonSerializer.Serialize(this, InstallManifestJsonContext.Default.InstallManifest);
        await File.WriteAllTextAsync(paths.InstallManifestPath, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<InstallManifest?> LoadAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.InstallManifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(paths.InstallManifestPath);
        return await JsonSerializer.DeserializeAsync(stream, InstallManifestJsonContext.Default.InstallManifest, cancellationToken)
            .ConfigureAwait(false);
    }

    public void ValidateAgainst(AppPaths paths)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported install manifest schema '{SchemaVersion}'.");
        }

        if (!string.Equals(ProductId, paths.ProductId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The install manifest belongs to a different product.");
        }

        if (ManagedPaths.Count != paths.ManagedPaths.Count)
        {
            throw new InvalidDataException("The install manifest does not contain the complete managed-path allow-list.");
        }

        var seenKinds = new HashSet<ManagedPathKind>();
        foreach (var managedPath in ManagedPaths)
        {
            if (!Enum.IsDefined(managedPath.Kind) ||
                !seenKinds.Add(managedPath.Kind) ||
                !paths.IsExactManagedPath(managedPath.Kind, managedPath.Path))
            {
                throw new InvalidDataException("The install manifest contains an unowned or duplicate cleanup path.");
            }
        }
    }

    public bool CanDelete(ManagedPathKind kind, string candidatePath, AppPaths paths)
    {
        try
        {
            ValidateAgainst(paths);
            return ManagedPaths.Any(item => item.Kind == kind && paths.IsExactManagedPath(kind, candidatePath));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallManifest))]
internal sealed partial class InstallManifestJsonContext : JsonSerializerContext;
