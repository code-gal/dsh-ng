using System.Diagnostics;
using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

public static class SetupLocations
{
    /// <summary>
    /// Resolves the current-user target used by packaged installers. Package
    /// tooling can provide an explicit target for an isolated test install;
    /// all later cleanup is still constrained by the resulting AppPaths.
    /// </summary>
    public static string GetDefaultInstallRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "Applications", "DSH Desktop.app");
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "Programs", AppPaths.DefaultProductDirectoryName);
    }

    public static bool IsCurrentProcessInstalled(AppPaths paths)
    {
        if (!paths.IsPathOwnedByProduct(AppContext.BaseDirectory))
        {
            return false;
        }

        try
        {
            var manifest = InstallManifest.LoadAsync(paths).GetAwaiter().GetResult();
            manifest?.ValidateAgainst(paths);
            return manifest is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static ExistingProductDataState InspectExistingProductData(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var manifestExists = File.Exists(paths.InstallManifestPath) || Directory.Exists(paths.InstallManifestPath);
        if (manifestExists)
        {
            try
            {
                var manifest = InstallManifest.LoadAsync(paths).GetAwaiter().GetResult();
                if (manifest is null)
                {
                    return ExistingProductDataState.UnverifiedManifest;
                }

                manifest.ValidateAgainst(paths);
                return ExistingProductDataState.VerifiedInstallation;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                return ExistingProductDataState.UnverifiedManifest;
            }
        }

        return paths.ManagedPaths
            .Where(item => item.Key != ManagedPathKind.Logs)
            .Any(item => Directory.Exists(item.Value) || File.Exists(item.Value))
                ? ExistingProductDataState.InterruptedInstallation
                : ExistingProductDataState.None;
    }

    /// <summary>
    /// Opens the deployed client directory when it exists. Before deployment,
    /// opens the closest existing parent rather than creating the target while
    /// a transaction is still deciding whether it can commit.
    /// </summary>
    public static InstallationLocationOpenResult OpenInstallLocation(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var location = FindExistingDirectory(paths.InstallRoot);
        if (location is null)
        {
            return InstallationLocationOpenResult.Failure("找不到安装目标的现有父目录。");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = location,
                UseShellExecute = true
            });
            return process is null
                ? InstallationLocationOpenResult.Failure("操作系统未能启动文件浏览器。")
                : InstallationLocationOpenResult.Success(location);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return InstallationLocationOpenResult.Failure(exception.Message);
        }
    }

    private static string? FindExistingDirectory(string target)
    {
        var candidate = new DirectoryInfo(target);
        while (candidate is not null)
        {
            if (candidate.Exists)
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return null;
    }
}

public sealed record InstallationLocationOpenResult(bool Succeeded, string? OpenedPath, string? Error)
{
    public static InstallationLocationOpenResult Success(string openedPath) => new(true, openedPath, null);

    public static InstallationLocationOpenResult Failure(string error) => new(false, null, error);
}
