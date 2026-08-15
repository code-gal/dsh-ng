using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

/// <summary>
/// Removes only manifest-authorized product data. Installation files are left
/// to the deployment boundary, which knows whether this transaction created
/// the target directory.
/// </summary>
public sealed class ProductDataCleaner
{
    private readonly AppPaths _paths;

    public ProductDataCleaner(AppPaths paths)
    {
        _paths = paths;
    }

    public Task CleanAsync(
        InstallManifest manifest,
        bool preserveInstallationLogs,
        bool includeInstallRoot = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.ValidateAgainst(_paths);

        var records = manifest.ManagedPaths
            .Where(record => includeInstallRoot || record.Kind != ManagedPathKind.InstallRoot)
            .Where(record => !preserveInstallationLogs || record.Kind != ManagedPathKind.Logs)
            .OrderByDescending(record => record.Path.Length)
            .ToList();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifest.CanDelete(record.Kind, record.Path, _paths))
            {
                throw new InvalidDataException("The installation manifest refused a cleanup path.");
            }

            if (Directory.Exists(record.Path))
            {
                Directory.Delete(record.Path, recursive: true);
            }
            else if (File.Exists(record.Path))
            {
                throw new IOException("A product-managed directory was unexpectedly replaced by a file.");
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteRetainedInstallationLogsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_paths.IsExactManagedPath(ManagedPathKind.Logs, _paths.LogsDirectory))
        {
            throw new InvalidOperationException("The installation log directory is not product-managed.");
        }

        if (Directory.Exists(_paths.LogsDirectory))
        {
            Directory.Delete(_paths.LogsDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }
}
