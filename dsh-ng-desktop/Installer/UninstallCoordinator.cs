using DshNgDesktop.Core;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Installer;

/// <summary>
/// Performs the product-owned part of a system uninstall after the installed
/// executable has handed off to a temporary helper and the running instance
/// has released its single-instance mutex. This class never claims a process
/// merely because a stale runtime-state file exists.
/// </summary>
public sealed class UninstallCoordinator
{
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platformServices;
    private readonly ProductDataCleaner _dataCleaner;

    public UninstallCoordinator(AppPaths paths, IPlatformServices platformServices, ProductDataCleaner dataCleaner)
    {
        _paths = paths;
        _platformServices = platformServices;
        _dataCleaner = dataCleaner;
    }

    public async Task<PlatformOperationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        InstallManifest? manifest;
        try
        {
            manifest = await InstallManifest.LoadAsync(_paths, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return HasAnyManagedPath()
                    ? PlatformOperationResult.Failure("The DSH Desktop installation manifest was not found. No files were removed.")
                    : PlatformOperationResult.Success();
            }

            manifest.ValidateAgainst(_paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return PlatformOperationResult.Failure($"The DSH Desktop installation manifest could not be verified: {exception.Message}");
        }

        var startup = await _platformServices.UnregisterStartupAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
        if (!startup.Succeeded)
        {
            return PlatformOperationResult.Failure($"The DSH Desktop login startup entry could not be removed: {startup.Error}");
        }

        var shortcuts = await _platformServices.UnregisterShortcutsAsync("DSH Desktop", cancellationToken).ConfigureAwait(false);
        if (!shortcuts.Succeeded)
        {
            return PlatformOperationResult.Failure($"The DSH Desktop shortcuts could not be removed: {shortcuts.Error}");
        }

        var installation = await _platformServices.UnregisterInstallationAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
        if (!installation.Succeeded)
        {
            return PlatformOperationResult.Failure($"The DSH Desktop uninstall registration could not be removed: {installation.Error}");
        }

        try
        {
            // Keep the verified manifest until every other owned path has
            // gone. If an install-root lock or another deletion fails, the
            // next uninstall can validate this same allow-list and resume
            // without guessing at paths from names.
            await _dataCleaner.CleanAsync(
                    manifest,
                    preserveInstallationLogs: false,
                    includeInstallRoot: true,
                    preserveState: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await _dataCleaner.CleanAsync(
                    manifest,
                    preserveInstallationLogs: false,
                    includeInstallRoot: false,
                    preserveState: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return PlatformOperationResult.Failure($"Only part of the verified DSH Desktop data could be removed: {exception.Message}");
        }
    }

    private bool HasAnyManagedPath() => _paths.ManagedPaths.Any(path =>
        Directory.Exists(path.Value) || File.Exists(path.Value));
}
