using DshNgDesktop.Core;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Installer;

/// <summary>
/// Performs the product-owned part of a system uninstall after the installed
/// executable has handed off to a temporary helper. Runtime IPC and WebView
/// teardown remain a later desktop-lifecycle concern; this class never claims
/// a process merely because a stale runtime-state file exists.
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
                return PlatformOperationResult.Failure("The DSH Desktop installation manifest was not found. No files were removed.");
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

        var installation = await _platformServices.UnregisterInstallationAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
        if (!installation.Succeeded)
        {
            return PlatformOperationResult.Failure($"The DSH Desktop uninstall registration could not be removed: {installation.Error}");
        }

        try
        {
            await _dataCleaner.CleanAsync(manifest, preserveInstallationLogs: false, includeInstallRoot: true, cancellationToken).ConfigureAwait(false);
            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return PlatformOperationResult.Failure($"Only part of the verified DSH Desktop data could be removed: {exception.Message}");
        }
    }
}
