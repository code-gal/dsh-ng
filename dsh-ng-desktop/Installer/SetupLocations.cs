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
}
