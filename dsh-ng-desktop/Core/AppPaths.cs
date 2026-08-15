using System.Runtime.InteropServices;

namespace DshNgDesktop.Core;

public enum ManagedPathKind
{
    InstallRoot,
    State,
    Logs,
    NpmCache,
    DshHome,
    LauncherWorkingDirectory,
    WebViewData
}

/// <summary>
/// The single source of truth for all files that belong to this product.
/// Workspace paths are deliberately absent: they are never product-owned data.
/// </summary>
public sealed class AppPaths
{
    public const string DefaultProductId = "DeepSeekHarness.DshNgDesktop";
    public const string DefaultProductDirectoryName = "DSH Desktop";

    private readonly IReadOnlyList<KeyValuePair<ManagedPathKind, string>> _managedPaths;
    private readonly StringComparison _pathComparison;

    private AppPaths(
        string productId,
        string installRoot,
        string applicationDataRoot,
        string stateDirectory,
        string logsDirectory,
        string npmCacheDirectory,
        string dshHomeDirectory,
        string launcherWorkingDirectory,
        string webViewDataDirectory)
    {
        ProductId = productId;
        InstallRoot = Normalize(installRoot);
        ApplicationDataRoot = Normalize(applicationDataRoot);
        StateDirectory = Normalize(stateDirectory);
        LogsDirectory = Normalize(logsDirectory);
        NpmCacheDirectory = Normalize(npmCacheDirectory);
        DshHomeDirectory = Normalize(dshHomeDirectory);
        LauncherWorkingDirectory = Normalize(launcherWorkingDirectory);
        WebViewDataDirectory = Normalize(webViewDataDirectory);
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        _managedPaths =
        [
            new(ManagedPathKind.InstallRoot, InstallRoot),
            new(ManagedPathKind.State, StateDirectory),
            new(ManagedPathKind.Logs, LogsDirectory),
            new(ManagedPathKind.NpmCache, NpmCacheDirectory),
            new(ManagedPathKind.DshHome, DshHomeDirectory),
            new(ManagedPathKind.LauncherWorkingDirectory, LauncherWorkingDirectory),
            new(ManagedPathKind.WebViewData, WebViewDataDirectory)
        ];
    }

    public string ProductId { get; }

    public string InstallRoot { get; }

    public string ApplicationDataRoot { get; }

    public string StateDirectory { get; }

    public string LogsDirectory { get; }

    public string NpmCacheDirectory { get; }

    public string DshHomeDirectory { get; }

    public string LauncherWorkingDirectory { get; }

    public string WebViewDataDirectory { get; }

    public string InstallManifestPath => Path.Combine(StateDirectory, "install-manifest.json");

    public string RuntimeStatePath => Path.Combine(StateDirectory, "runtime-state.json");

    public IReadOnlyList<KeyValuePair<ManagedPathKind, string>> ManagedPaths => _managedPaths;

    public static AppPaths CreateDefault(string? installRoot = null)
    {
        var resolvedInstallRoot = installRoot ?? AppContext.BaseDirectory;

        if (OperatingSystem.IsMacOS())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var applicationSupport = Path.Combine(userProfile, "Library", "Application Support", DefaultProductDirectoryName);
            var caches = Path.Combine(userProfile, "Library", "Caches", DefaultProductDirectoryName);

            return Create(
                DefaultProductId,
                resolvedInstallRoot,
                applicationSupport,
                Path.Combine(applicationSupport, "state"),
                Path.Combine(applicationSupport, "logs"),
                Path.Combine(caches, "runtime", "npm-cache"),
                Path.Combine(applicationSupport, "dsh-home"),
                Path.Combine(caches, "runtime", "launcher-cwd"),
                Path.Combine(caches, "webview"));
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var productRoot = Path.Combine(localApplicationData, DefaultProductDirectoryName);

        return Create(
            DefaultProductId,
            resolvedInstallRoot,
            productRoot,
            Path.Combine(productRoot, "state"),
            Path.Combine(productRoot, "logs"),
            Path.Combine(productRoot, "runtime", "npm-cache"),
            Path.Combine(productRoot, "dsh-home"),
            Path.Combine(productRoot, "runtime", "launcher-cwd"),
            Path.Combine(productRoot, "webview"));
    }

    public static AppPaths Create(
        string productId,
        string installRoot,
        string applicationDataRoot,
        string stateDirectory,
        string logsDirectory,
        string npmCacheDirectory,
        string dshHomeDirectory,
        string launcherWorkingDirectory,
        string webViewDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        return new AppPaths(
            productId,
            installRoot,
            applicationDataRoot,
            stateDirectory,
            logsDirectory,
            npmCacheDirectory,
            dshHomeDirectory,
            launcherWorkingDirectory,
            webViewDataDirectory);
    }

    public string GetPath(ManagedPathKind kind) => kind switch
    {
        ManagedPathKind.InstallRoot => InstallRoot,
        ManagedPathKind.State => StateDirectory,
        ManagedPathKind.Logs => LogsDirectory,
        ManagedPathKind.NpmCache => NpmCacheDirectory,
        ManagedPathKind.DshHome => DshHomeDirectory,
        ManagedPathKind.LauncherWorkingDirectory => LauncherWorkingDirectory,
        ManagedPathKind.WebViewData => WebViewDataDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public bool IsPathOwnedByProduct(string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var normalizedCandidate = Normalize(candidatePath);
        return _managedPaths.Any(item => IsWithin(item.Value, normalizedCandidate));
    }

    public bool IsExactManagedPath(ManagedPathKind kind, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        return string.Equals(GetPath(kind), Normalize(candidatePath), _pathComparison);
    }

    private bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !relative.Equals("..", _pathComparison) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", _pathComparison));
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
