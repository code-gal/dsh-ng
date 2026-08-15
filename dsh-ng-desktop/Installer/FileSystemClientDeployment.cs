using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

/// <summary>
/// Copies a complete installer payload through a sibling staging directory so
/// an incomplete copy can never become the installed client. The implementation
/// deliberately refuses upgrades: replacing an already committed client is a
/// separate transaction, not a best-effort file overwrite.
/// </summary>
public sealed class FileSystemClientDeployment : IClientDeployment
{
    private readonly AppPaths _paths;

    public FileSystemClientDeployment(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<ClientDeploymentResult> DeployAsync(ClientDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = Path.GetFullPath(request.PayloadDirectory);
        var installRoot = Path.GetFullPath(request.InstallRoot);

        if (!_paths.IsExactManagedPath(ManagedPathKind.InstallRoot, installRoot))
        {
            throw new InvalidOperationException("The requested installation directory is not the exact product-managed install root.");
        }

        if (!Directory.Exists(payload))
        {
            throw new DirectoryNotFoundException("The installer payload directory does not exist.");
        }

        if (PathsEqual(payload, installRoot) || IsWithin(installRoot, payload))
        {
            throw new InvalidOperationException("The installer payload must be separate from the target installation directory.");
        }

        if (Directory.Exists(installRoot) || File.Exists(installRoot))
        {
            throw new IOException("The DSH Desktop installation directory already exists. Uninstall the existing product before installing again.");
        }

        var parent = Directory.GetParent(installRoot)?.FullName
            ?? throw new InvalidOperationException("The target installation directory has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(installRoot)}.{Guid.NewGuid():N}.installing");

        try
        {
            await CopyDirectoryAsync(payload, staging, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, installRoot);
            return new ClientDeploymentResult(installRoot, CreatedInstallRoot: true);
        }
        catch
        {
            DeleteStagingDirectory(staging);
            throw;
        }
    }

    public Task RollbackAsync(ClientDeploymentResult deployment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        cancellationToken.ThrowIfCancellationRequested();
        var installRoot = Path.GetFullPath(deployment.InstallRoot);
        if (!deployment.CreatedInstallRoot || !_paths.IsExactManagedPath(ManagedPathKind.InstallRoot, installRoot))
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(installRoot))
        {
            Directory.Delete(installRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The installer payload root cannot be a symbolic link or junction.");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The installer payload cannot contain symbolic links or junctions.");
            }

            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The installer payload cannot contain symbolic links.");
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void DeleteStagingDirectory(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original install root was never touched. The coordinator
            // records the failed deployment so the user can remove this
            // explicitly named staging directory after reading the log.
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !relative.Equals("..", comparison) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", comparison));
    }
}
