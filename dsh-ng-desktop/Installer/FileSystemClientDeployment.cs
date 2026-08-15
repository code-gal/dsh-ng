using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

/// <summary>
/// Copies a complete installer payload through a sibling staging directory so
/// an incomplete copy can never become the installed client. An explicit
/// repair replaces the old client through a sibling backup so rollback can
/// restore it without touching DSH data.
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
            throw new InvalidOperationException("请求的安装目录不是产品精确受管的安装根目录。");
        }

        if (!Directory.Exists(payload))
        {
            throw new DirectoryNotFoundException("安装器负载目录不存在。");
        }

        if (PathsEqual(payload, installRoot) || IsWithin(installRoot, payload))
        {
            throw new InvalidOperationException("安装器负载目录必须与目标安装目录分离。");
        }

        if (File.Exists(installRoot))
        {
            throw new IOException("DSH Desktop 安装目标被同名文件占用。");
        }

        var replaceExistingInstallRoot = Directory.Exists(installRoot);
        if (replaceExistingInstallRoot && !request.ReplaceExistingInstallRoot)
        {
            throw new IOException("DSH Desktop 安装目录已存在，请先选择旧数据处理方式。");
        }

        var parent = Directory.GetParent(installRoot)?.FullName
            ?? throw new InvalidOperationException("目标安装目录没有父目录。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(installRoot)}.{Guid.NewGuid():N}.installing");
        var backup = replaceExistingInstallRoot
            ? Path.Combine(parent, $".{Path.GetFileName(installRoot)}.{Guid.NewGuid():N}.replaced")
            : null;
        var existingInstallRootMoved = false;

        try
        {
            await CopyDirectoryAsync(payload, staging, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (backup is not null)
            {
                Directory.Move(installRoot, backup);
                existingInstallRootMoved = true;
            }

            Directory.Move(staging, installRoot);
            return new ClientDeploymentResult(installRoot, CreatedInstallRoot: true, backup);
        }
        catch
        {
            DeleteStagingDirectory(staging);
            if (existingInstallRootMoved && backup is not null && !Directory.Exists(installRoot) && Directory.Exists(backup))
            {
                Directory.Move(backup, installRoot);
            }

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

        if (deployment.ReplacedInstallRootBackup is not null)
        {
            var backup = Path.GetFullPath(deployment.ReplacedInstallRootBackup);
            EnsureReplacementBackupPath(backup, installRoot);
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, installRoot);
            }
        }

        return Task.CompletedTask;
    }

    public Task CommitAsync(ClientDeploymentResult deployment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        cancellationToken.ThrowIfCancellationRequested();
        if (deployment.ReplacedInstallRootBackup is null)
        {
            return Task.CompletedTask;
        }

        var installRoot = Path.GetFullPath(deployment.InstallRoot);
        var backup = Path.GetFullPath(deployment.ReplacedInstallRootBackup);
        EnsureReplacementBackupPath(backup, installRoot);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void EnsureReplacementBackupPath(string backup, string installRoot)
    {
        var parent = Directory.GetParent(installRoot)?.FullName
            ?? throw new InvalidOperationException("目标安装目录没有父目录。");
        var expectedPrefix = $".{Path.GetFileName(installRoot)}.";
        var name = Path.GetFileName(backup);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(backup), parent, comparison) ||
            !name.StartsWith(expectedPrefix, comparison) ||
            !name.EndsWith(".replaced", comparison))
        {
            throw new InvalidOperationException("客户端替换备份不在允许的安装父目录中。");
        }
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
