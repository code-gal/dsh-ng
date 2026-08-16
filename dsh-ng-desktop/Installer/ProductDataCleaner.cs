using System.Security;
using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

/// <summary>
/// Removes only manifest-authorized product data. Deletion walks each exact
/// product root itself, so package-cache links are never followed outside the
/// ownership boundary and read-only dependency files do not block recovery.
/// </summary>
public sealed class ProductDataCleaner
{
    private const int MaximumDeleteAttempts = 3;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly AppPaths _paths;

    public ProductDataCleaner(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task CleanAsync(
        InstallManifest manifest,
        bool preserveInstallationLogs,
        bool includeInstallRoot = false,
        bool preserveState = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.ValidateAgainst(_paths);

        var records = manifest.ManagedPaths
            .Where(record => includeInstallRoot || record.Kind != ManagedPathKind.InstallRoot)
            .Where(record => !preserveInstallationLogs || record.Kind != ManagedPathKind.Logs)
            .Where(record => !preserveState || record.Kind != ManagedPathKind.State)
            .OrderByDescending(record => record.Path.Length)
            .ToList();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifest.CanDelete(record.Kind, record.Path, _paths))
            {
                throw new InvalidDataException("The installation manifest refused a cleanup path.");
            }

            await DeleteProductDirectoryAsync(record.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteRetainedInstallationLogsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_paths.IsExactManagedPath(ManagedPathKind.Logs, _paths.LogsDirectory))
        {
            throw new InvalidOperationException("The installation log directory is not product-managed.");
        }

        await DeleteProductDirectoryAsync(_paths.LogsDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteProductDirectoryAsync(string managedRoot, CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            if (!TryGetAttributes(managedRoot, out attributes))
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new ProductDataCleanupException(managedRoot, managedRoot, exception);
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ProductDataCleanupException(
                managedRoot,
                managedRoot,
                new IOException("A product-managed directory was unexpectedly replaced by a file."));
        }

        await DeleteDirectoryAsync(managedRoot, managedRoot, attributes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteDirectoryAsync(
        string managedRoot,
        string directory,
        FileAttributes attributes,
        CancellationToken cancellationToken)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            await DeleteDirectoryEntryAsync(managedRoot, directory, isReparsePoint: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new ProductDataCleanupException(managedRoot, directory, exception);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes entryAttributes;
            try
            {
                if (!TryGetAttributes(entry, out entryAttributes))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw new ProductDataCleanupException(managedRoot, entry, exception);
            }

            if (entryAttributes.HasFlag(FileAttributes.Directory))
            {
                await DeleteDirectoryAsync(managedRoot, entry, entryAttributes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteFileAsync(
                        managedRoot,
                        entry,
                        entryAttributes.HasFlag(FileAttributes.ReparsePoint),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await DeleteDirectoryEntryAsync(managedRoot, directory, isReparsePoint: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteFileAsync(
        string managedRoot,
        string path,
        bool isReparsePoint,
        CancellationToken cancellationToken)
    {
        await ExecuteDeleteWithRetryAsync(
                managedRoot,
                path,
                () =>
                {
                    if (!isReparsePoint)
                    {
                        ClearReadOnlyAttribute(path);
                    }

                    File.Delete(path);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteDirectoryEntryAsync(
        string managedRoot,
        string path,
        bool isReparsePoint,
        CancellationToken cancellationToken)
    {
        await ExecuteDeleteWithRetryAsync(
                managedRoot,
                path,
                () =>
                {
                    if (!isReparsePoint)
                    {
                        ClearReadOnlyAttribute(path);
                    }

                    Directory.Delete(path, recursive: false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteDeleteWithRetryAsync(
        string managedRoot,
        string path,
        Action delete,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumDeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                delete();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                lastFailure = exception;
                if (attempt < MaximumDeleteAttempts)
                {
                    await Task.Delay(DeleteRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new ProductDataCleanupException(managedRoot, path, lastFailure!);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}

public sealed class ProductDataCleanupException(string managedRoot, string failedPath, Exception innerException)
    : IOException($"无法删除受管目录“{managedRoot}”中的“{failedPath}”：{innerException.Message}", innerException)
{
    public string ManagedRoot { get; } = managedRoot;

    public string FailedPath { get; } = failedPath;
}
