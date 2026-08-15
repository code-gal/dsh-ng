using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshNgDesktop.Core;
using DshNgDesktop.Dsh;
using DshNgDesktop.Platform;

namespace DshNgDesktop.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record DiagnosticCheckResult(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Detail,
    string? Remediation);

public sealed record EnvironmentDiagnosticReport(
    DateTimeOffset CreatedAtUtc,
    string OperatingSystem,
    string Architecture,
    List<DiagnosticCheckResult> Checks)
{
    public bool HasErrors => Checks.Any(check => check.Severity == DiagnosticSeverity.Error);
}

public interface IInstallerPreflight
{
    Task<EnvironmentDiagnosticReport> RunInstallerPreflightAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A reusable, side-effect-free inspection of the local prerequisites. It does
/// not install Node, create product directories, alter startup registration or
/// delete caches; callers choose any repair action separately.
/// </summary>
public sealed class EnvironmentDoctor : IInstallerPreflight
{
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platformServices;

    public EnvironmentDoctor(AppPaths paths, IPlatformServices platformServices)
    {
        _paths = paths;
        _platformServices = platformServices;
    }

    public async Task<EnvironmentDiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheckResult>();
        AddPlatformCheck(checks);
        await AddExecutableChecksAsync(checks, cancellationToken).ConfigureAwait(false);
        AddStorageCheck(checks);
        AddLoopbackPortCheck(checks);
        AddWebViewPrerequisiteCheck(checks);
        await AddDshHealthCheckAsync(checks, cancellationToken).ConfigureAwait(false);
        await AddStartupCheckAsync(checks, cancellationToken).ConfigureAwait(false);

        return new EnvironmentDiagnosticReport(
            DateTimeOffset.UtcNow,
            Environment.OSVersion.VersionString,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            checks);
    }

    /// <summary>
    /// Performs the non-mutating checks which can block a new installation.
    /// A previous DSH runtime or login registration is intentionally excluded:
    /// neither can exist on a fresh transaction and neither should turn a
    /// recoverable install into a false prerequisite failure.
    /// </summary>
    public async Task<EnvironmentDiagnosticReport> RunInstallerPreflightAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheckResult>();
        AddPlatformCheck(checks);
        await AddExecutableChecksAsync(checks, cancellationToken).ConfigureAwait(false);
        AddStorageCheck(checks);
        AddLoopbackPortCheck(checks);
        AddWebViewPrerequisiteCheck(checks);

        return new EnvironmentDiagnosticReport(
            DateTimeOffset.UtcNow,
            Environment.OSVersion.VersionString,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            checks);
    }

    private void AddPlatformCheck(List<DiagnosticCheckResult> checks)
    {
        if (_platformServices.Kind is PlatformKind.Windows or PlatformKind.MacOS)
        {
            checks.Add(Pass("platform.supported", "Platform", $"{_platformServices.Kind} is a supported desktop platform."));
            return;
        }

        checks.Add(Error("platform.unsupported", "Platform", "This operating system is not a supported desktop target.", "Run DSH Desktop on Windows or macOS."));
    }

    private static async Task AddExecutableChecksAsync(List<DiagnosticCheckResult> checks, CancellationToken cancellationToken)
    {
        var validation = await new SystemDshExecutableValidator(TimeSpan.FromSeconds(5)).ValidateAsync(cancellationToken)
            .ConfigureAwait(false);
        AddExecutableCheck(checks, validation.Node, "Node.js");
        AddExecutableCheck(checks, validation.Npx, "npx");
    }

    private static void AddExecutableCheck(List<DiagnosticCheckResult> checks, DshCommandProbeResult result, string displayName)
    {
        if (result.Succeeded)
        {
            checks.Add(Pass($"{result.Command}.available", displayName, $"{displayName} is available on PATH ({result.Version})."));
            return;
        }

        checks.Add(Error($"{result.Command}.unavailable", displayName, result.Error ?? $"{displayName} could not be validated.", result.Remediation));
    }

    private void AddStorageCheck(List<DiagnosticCheckResult> checks)
    {
        var parent = FindExistingParent(_paths.ApplicationDataRoot);
        if (parent is null)
        {
            checks.Add(Error(
                "storage.parent-unavailable",
                "Application data location",
                "No existing parent directory could be found for the product data location.",
                "Choose a user profile with a valid local application-data directory."));
            return;
        }

        try
        {
            var attributes = File.GetAttributes(parent);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                checks.Add(Error(
                    "storage.parent-read-only",
                    "Application data location",
                    "The existing parent directory is marked read-only.",
                    "Remove the read-only restriction or select a writable user profile."));
                return;
            }

            checks.Add(new DiagnosticCheckResult(
                "storage.parent-accessible",
                DiagnosticSeverity.Information,
                "Application data location",
                "The nearest existing product-data parent is accessible. No product directory was created during this check.",
                null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(Error(
                "storage.parent-inaccessible",
                "Application data location",
                exception.Message,
                "Ensure the current user can access its local application-data directory."));
        }
    }

    private static void AddLoopbackPortCheck(List<DiagnosticCheckResult> checks)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 3080);
            listener.Start();
            checks.Add(Pass("port.3080-available", "Default DSH port", "Loopback port 3080 is currently available."));
        }
        catch (SocketException)
        {
            checks.Add(new DiagnosticCheckResult(
                "port.3080-in-use",
                DiagnosticSeverity.Warning,
                "Default DSH port",
                "Loopback port 3080 is already in use. DSH Desktop will select another private loopback port when needed.",
                null));
        }
    }

    private static void AddWebViewPrerequisiteCheck(List<DiagnosticCheckResult> checks)
    {
        if (OperatingSystem.IsMacOS())
        {
            checks.Add(Pass("webview.wkwebview", "WebView prerequisite", "WKWebView is supplied by macOS."));
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hasRuntimeDirectory =
                Directory.Exists(Path.Combine(programFilesX86, "Microsoft", "EdgeWebView", "Application")) ||
                Directory.Exists(Path.Combine(localAppData, "Microsoft", "EdgeWebView", "Application"));

            checks.Add(hasRuntimeDirectory
                ? Pass("webview.webview2", "WebView prerequisite", "A WebView2 runtime directory was found.")
                : new DiagnosticCheckResult(
                    "webview.webview2-not-detected",
                    DiagnosticSeverity.Warning,
                    "WebView prerequisite",
                    "A WebView2 runtime directory was not detected. Windows normally supplies it, but the DSH view cannot be created until it is available.",
                    "Install or repair Microsoft Edge WebView2 Runtime before running DSH Desktop."));
            return;
        }

        checks.Add(new DiagnosticCheckResult(
            "webview.not-assessed",
            DiagnosticSeverity.Warning,
            "WebView prerequisite",
            "The WebView prerequisite cannot be assessed on this platform.",
            null));
    }

    private async Task AddDshHealthCheckAsync(List<DiagnosticCheckResult> checks, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.RuntimeStatePath))
        {
            checks.Add(new DiagnosticCheckResult(
                "dsh.not-running",
                DiagnosticSeverity.Information,
                "DSH Web UI",
                "No persisted DSH runtime state exists, so no local Web UI health check was attempted.",
                null));
            return;
        }

        DshRuntimeState? runtimeState;
        try
        {
            runtimeState = await new DshRuntimeStateStore(_paths).LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            checks.Add(Error(
                "dsh.runtime-state-invalid",
                "DSH Web UI",
                $"The persisted runtime state cannot be read: {exception.Message}",
                "Stop DSH Desktop and start it again. Do not delete DSH_HOME as part of diagnosis."));
            return;
        }

        if (runtimeState is null || runtimeState.Port is < 1 or > 65535)
        {
            checks.Add(Error(
                "dsh.runtime-state-invalid",
                "DSH Web UI",
                "The persisted runtime state does not contain a valid loopback port.",
                "Stop DSH Desktop and start it again."));
            return;
        }

        try
        {
            using var probe = new DshHttpHealthProbe(TimeSpan.FromSeconds(2));
            var result = await probe.ProbeAsync(new Uri($"http://127.0.0.1:{runtimeState.Port}/", UriKind.Absolute), cancellationToken).ConfigureAwait(false);
            checks.Add(result.IsHealthy
                ? Pass("dsh.web-ui-verified", "DSH Web UI", "The persisted DSH loopback endpoint passed DSH Web UI identity validation.")
                : Error("dsh.http-unhealthy", "DSH Web UI", result.Detail, "Restart DSH Desktop and inspect the runtime log."));
        }
        catch (HttpRequestException exception)
        {
            checks.Add(Error("dsh.http-unreachable", "DSH Web UI", exception.Message, "Restart DSH Desktop and inspect the runtime log."));
        }
        catch (TaskCanceledException)
        {
            checks.Add(Error("dsh.http-timeout", "DSH Web UI", "The persisted DSH loopback endpoint did not respond in time.", "Restart DSH Desktop and inspect the runtime log."));
        }
    }

    private async Task AddStartupCheckAsync(List<DiagnosticCheckResult> checks, CancellationToken cancellationToken)
    {
        var state = await _platformServices.GetStartupRegistrationStateAsync(_paths.ProductId, cancellationToken).ConfigureAwait(false);
        checks.Add(state switch
        {
            StartupRegistrationState.Registered => Pass("startup.registered", "Login startup", "The product startup registration is present."),
            StartupRegistrationState.NotRegistered => new DiagnosticCheckResult(
                "startup.not-registered",
                DiagnosticSeverity.Information,
                "Login startup",
                "The product startup registration is not present.",
                null),
            _ => new DiagnosticCheckResult(
                "startup.unknown",
                DiagnosticSeverity.Warning,
                "Login startup",
                "The startup registration state could not be read on this platform.",
                null)
        });
    }

    private static DiagnosticCheckResult Pass(string code, string title, string detail) =>
        new(code, DiagnosticSeverity.Information, title, detail, null);

    private static DiagnosticCheckResult Error(string code, string title, string detail, string remediation) =>
        new(code, DiagnosticSeverity.Error, title, detail, remediation);

    private static string? FindExistingParent(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null && !current.Exists)
        {
            current = current.Parent;
        }

        return current?.FullName;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EnvironmentDiagnosticReport))]
public sealed partial class EnvironmentDiagnosticJsonContext : JsonSerializerContext;
