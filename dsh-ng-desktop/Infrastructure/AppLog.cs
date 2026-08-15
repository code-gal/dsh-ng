using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DshNgDesktop.Core;

namespace DshNgDesktop.Infrastructure;

public enum AppLogStream
{
    Installation,
    Runtime
}

public enum AppLogLevel
{
    Trace,
    Information,
    Warning,
    Error
}

public sealed record AppLogSettings(long MaximumFileBytes = 1_048_576, int RetainedFileCount = 5)
{
    public void Validate()
    {
        if (MaximumFileBytes < 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes), "A log file must retain at least 4 KiB.");
        }

        if (RetainedFileCount is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedFileCount), "Retained log files must be between 1 and 20.");
        }
    }
}

public sealed record AppLogEntry(
    DateTimeOffset TimestampUtc,
    AppLogLevel Level,
    string EventName,
    string Message,
    string? Exception);

public sealed record LogFolderOpenResult(bool Succeeded, string? Error);

/// <summary>
/// Writes independent installation and runtime logs. Every free-form value is
/// redacted before it reaches disk or a copyable diagnostic report.
/// </summary>
public sealed class AppLog : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly AppLogSettings _settings;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AppLog(AppPaths paths, AppLogSettings? settings = null)
    {
        _paths = paths;
        _settings = settings ?? new AppLogSettings();
        _settings.Validate();
    }

    public string InstallationLogPath => Path.Combine(_paths.LogsDirectory, "installation.log");

    public string RuntimeLogPath => Path.Combine(_paths.LogsDirectory, "runtime.log");

    public Task TraceAsync(AppLogStream stream, string eventName, string message, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, AppLogLevel.Trace, eventName, message, null, cancellationToken);

    public Task InformationAsync(AppLogStream stream, string eventName, string message, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, AppLogLevel.Information, eventName, message, null, cancellationToken);

    public Task WarningAsync(AppLogStream stream, string eventName, string message, Exception? exception = null, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, AppLogLevel.Warning, eventName, message, exception, cancellationToken);

    public Task ErrorAsync(AppLogStream stream, string eventName, string message, Exception? exception = null, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, AppLogLevel.Error, eventName, message, exception, cancellationToken);

    public async Task WriteAsync(
        AppLogStream stream,
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new AppLogEntry(
            DateTimeOffset.UtcNow,
            level,
            SensitiveDataRedactor.Redact(eventName),
            SensitiveDataRedactor.Redact(message),
            exception is null ? null : SensitiveDataRedactor.Redact(exception.ToString()));
        var line = JsonSerializer.Serialize(entry, AppLogJsonContext.Default.AppLogEntry);
        var filePath = stream == AppLogStream.Installation ? InstallationLogPath : RuntimeLogPath;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            RotateIfNeeded(filePath, Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
            await File.AppendAllTextAsync(filePath, line + Environment.NewLine, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public string CreateCopyableDiagnosticText(ApplicationStateSnapshot? state, IEnumerable<string>? additionalFacts = null)
    {
        var facts = new StringBuilder()
            .AppendLine("DSH Desktop diagnostic information")
            .Append("Generated (UTC): ").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
            .Append("OS: ").AppendLine(Environment.OSVersion.VersionString)
            .Append("Architecture: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
            .Append("Installation log: ").AppendLine(InstallationLogPath)
            .Append("Runtime log: ").AppendLine(RuntimeLogPath);

        if (state is not null)
        {
            facts.Append("Application state: ").AppendLine(state.State.ToString())
                .Append("State sequence: ").AppendLine(state.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("State reason: ").AppendLine(state.Reason);
        }

        if (additionalFacts is not null)
        {
            foreach (var fact in additionalFacts)
            {
                facts.Append("Detail: ").AppendLine(fact);
            }
        }

        return SensitiveDataRedactor.Redact(facts.ToString());
    }

    public LogFolderOpenResult OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _paths.LogsDirectory,
                UseShellExecute = true
            });
            return new LogFolderOpenResult(true, null);
        }
        catch (Exception exception)
        {
            return new LogFolderOpenResult(false, SensitiveDataRedactor.Redact(exception.Message));
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RotateIfNeeded(string activeFilePath, int incomingByteCount)
    {
        if (!File.Exists(activeFilePath))
        {
            return;
        }

        var currentLength = new FileInfo(activeFilePath).Length;
        if (currentLength + incomingByteCount <= _settings.MaximumFileBytes)
        {
            return;
        }

        var oldest = $"{activeFilePath}.{_settings.RetainedFileCount}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _settings.RetainedFileCount - 1; index >= 1; index--)
        {
            var source = $"{activeFilePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{activeFilePath}.{index + 1}");
            }
        }

        File.Move(activeFilePath, $"{activeFilePath}.1");
    }
}

internal static partial class SensitiveDataRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = NamedSecretRegex().Replace(value, "${prefix}[REDACTED]");
        return BearerTokenRegex().Replace(redacted, "Bearer [REDACTED]");
    }

    [GeneratedRegex(@"(?ix)(?<prefix>\b(?:api[-_]?key|access[-_]?token|refresh[-_]?token|authorization|password|secret)\b\s*[:=]\s*)(?:bearer\s+)?[^\s,;""']+")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"(?ix)\bbearer\s+[a-z0-9\-._~+/]+=*")]
    private static partial Regex BearerTokenRegex();
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppLogEntry))]
internal sealed partial class AppLogJsonContext : JsonSerializerContext;
