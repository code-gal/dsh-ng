using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DshNgDesktop.Platform;

public static class PlatformServices
{
    public static IPlatformServices CreateDefault() => OperatingSystem.IsWindows()
        ? new WindowsPlatformServices()
        : OperatingSystem.IsMacOS()
            ? new MacOSPlatformServices()
            : new UnsupportedPlatformServices();
}

internal abstract class PlatformServicesBase : IPlatformServices
{
    protected PlatformServicesBase(PlatformKind kind)
    {
        Kind = kind;
    }

    public PlatformKind Kind { get; }

    public virtual IPlatformProcessGroup CreateProcessGroup() => new DeferredPlatformProcessGroup(Kind);

    public virtual Task<PlatformOperationResult> RegisterStartupAsync(StartupRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} startup registration is not available in this build."));

    public virtual Task<PlatformOperationResult> UnregisterStartupAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} startup registration is not available in this build."));

    public virtual Task<StartupRegistrationState> GetStartupRegistrationStateAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(StartupRegistrationState.Unknown);

    public virtual Task<PlatformOperationResult> RegisterInstallationAsync(InstallationRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} installation registration is not available in this build."));

    public virtual Task<PlatformOperationResult> UnregisterInstallationAsync(string productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{Kind} installation registration is not available in this build."));
}

/// <summary>
/// Windows registration uses only the current-user registry hives. Process
/// groups deliberately remain a M2 implementation because they must be bound
/// to the exact child process created by DshSupervisor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatformServices : PlatformServicesBase
{
    private const string StartupKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string UninstallKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

    public WindowsPlatformServices()
        : base(PlatformKind.Windows)
    {
    }

    public override IPlatformProcessGroup CreateProcessGroup() => new WindowsJobObjectProcessGroup();

    public override Task<PlatformOperationResult> RegisterStartupAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true);
            if (key is null)
            {
                return Task.FromResult(PlatformOperationResult.Failure("The current-user startup registry key is unavailable."));
            }

            key.SetValue(registration.ProductId, BuildCommand(registration.ExecutablePath, registration.Arguments), RegistryValueKind.String);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<PlatformOperationResult> UnregisterStartupAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true);
            key?.DeleteValue(productId, throwOnMissingValue: false);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<StartupRegistrationState> GetStartupRegistrationStateAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
            return Task.FromResult(key?.GetValue(productId) is null
                ? StartupRegistrationState.NotRegistered
                : StartupRegistrationState.Registered);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(StartupRegistrationState.Unknown);
        }
    }

    public override Task<PlatformOperationResult> RegisterInstallationAsync(InstallationRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{UninstallKeyPath}\\{registration.ProductId}", writable: true);
            if (key is null)
            {
                return Task.FromResult(PlatformOperationResult.Failure("The current-user uninstall registry key is unavailable."));
            }

            key.SetValue("DisplayName", registration.DisplayName, RegistryValueKind.String);
            key.SetValue("InstallLocation", registration.InstallRoot, RegistryValueKind.String);
            key.SetValue("UninstallString", registration.UninstallCommand, RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    public override Task<PlatformOperationResult> UnregisterInstallationAsync(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            key?.DeleteSubKeyTree(productId, throwOnMissingSubKey: false);
            return Task.FromResult(PlatformOperationResult.Success());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Task.FromResult(PlatformOperationResult.Failure(exception.Message));
        }
    }

    private static string BuildCommand(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var quotedExecutable = $"\"{executablePath.Replace("\"", "\\\"")}\"";
        return arguments.Count == 0
            ? quotedExecutable
            : $"{quotedExecutable} {string.Join(" ", arguments.Select(argument => $"\"{argument.Replace("\"", "\\\"")}\""))}";
    }
}

/// <summary>
/// macOS calls are intentionally kept at this one boundary. Service Management
/// registration and process-group interop are supplied with the installer and
/// supervisor milestones, without changing any shared coordinator contracts.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSPlatformServices : PlatformServicesBase
{
    public MacOSPlatformServices()
        : base(PlatformKind.MacOS)
    {
    }

    public override IPlatformProcessGroup CreateProcessGroup() => new MacOSProcessGroup();
}

internal sealed class UnsupportedPlatformServices : PlatformServicesBase
{
    public UnsupportedPlatformServices()
        : base(PlatformKind.Unsupported)
    {
    }
}

internal sealed class DeferredPlatformProcessGroup(PlatformKind kind) : IPlatformProcessGroup
{
    private static readonly string Message = "Process-group binding is supplied by the DSH supervisor milestone.";

    public Task<PlatformOperationResult> AddProcessAsync(int processId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public Task<PlatformOperationResult> RequestGracefulStopAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public Task<PlatformOperationResult> TerminateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PlatformOperationResult.Failure($"{kind}: {Message}"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A Job Object gives Windows a reliable ownership boundary for the npx child
/// and every descendant it creates. The job is configured to terminate on host
/// exit so a crash cannot leave a product-owned DSH tree running indefinitely.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObjectProcessGroup : IPlatformProcessGroup
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle? _job;
    private readonly string? _creationError;
    private int _rootProcessId;
    private bool _disposed;

    public WindowsJobObjectProcessGroup()
    {
        var job = WindowsJobNative.CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            _creationError = $"CreateJobObject failed with Win32 error {Marshal.GetLastPInvokeError()}.";
            job.Dispose();
            return;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!WindowsJobNative.SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            _creationError = $"SetInformationJobObject failed with Win32 error {Marshal.GetLastPInvokeError()}.";
            job.Dispose();
            return;
        }

        _job = job;
    }

    public Task<PlatformOperationResult> AddProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _job is null)
        {
            return Task.FromResult(PlatformOperationResult.Failure(_creationError ?? "The DSH Job Object is unavailable."));
        }

        using var process = WindowsJobNative.OpenProcess(ProcessSetQuota | ProcessTerminate, false, processId);
        if (process.IsInvalid)
        {
            return Task.FromResult(PlatformOperationResult.Failure($"The created DSH process could not be opened for Job Object assignment (Win32 error {Marshal.GetLastPInvokeError()})."));
        }

        if (!WindowsJobNative.AssignProcessToJobObject(_job, process))
        {
            return Task.FromResult(PlatformOperationResult.Failure($"The created DSH process could not be assigned to its Job Object (Win32 error {Marshal.GetLastPInvokeError()})."));
        }

        _rootProcessId = processId;
        return Task.FromResult(PlatformOperationResult.Success());
    }

    public Task<PlatformOperationResult> RequestGracefulStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_rootProcessId <= 0)
        {
            return Task.FromResult(PlatformOperationResult.Failure("The DSH Job Object has no owned root process."));
        }

        return Task.FromResult(WindowsConsoleControl.TrySendCtrlBreak(_rootProcessId, out var error)
            ? PlatformOperationResult.Success()
            : PlatformOperationResult.Failure(error));
    }

    public Task<PlatformOperationResult> TerminateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _job is null)
        {
            return Task.FromResult(PlatformOperationResult.Failure(_creationError ?? "The DSH Job Object is unavailable."));
        }

        return Task.FromResult(WindowsJobNative.TerminateJobObject(_job, 1)
            ? PlatformOperationResult.Success()
            : PlatformOperationResult.Failure($"TerminateJobObject failed with Win32 error {Marshal.GetLastPInvokeError()}."));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _job?.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static partial class WindowsJobNative
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateJobObjectW(nint jobAttributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetInformationJobObject(
            SafeFileHandle job,
            uint informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeFileHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}

[SupportedOSPlatform("windows")]
internal static partial class WindowsConsoleControl
{
    private const uint CtrlBreakEvent = 1;

    public static bool TrySendCtrlBreak(int processId, out string error)
    {
        if (!AttachConsole((uint)processId))
        {
            error = $"The owned DSH console could not be attached for graceful termination (Win32 error {Marshal.GetLastPInvokeError()}).";
            return false;
        }

        try
        {
            SetConsoleCtrlHandler(0, true);
            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, 0))
            {
                error = $"A Ctrl+Break signal could not be sent to the owned DSH console (Win32 error {Marshal.GetLastPInvokeError()}).";
                return false;
            }

            error = string.Empty;
            return true;
        }
        finally
        {
            SetConsoleCtrlHandler(0, false);
            FreeConsole();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint controlType, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
}

/// <summary>
/// macOS descendants inherit the dedicated process group created for the npx
/// root. Signals are therefore limited to the group created by this client.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOSProcessGroup : IPlatformProcessGroup
{
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private int _processGroupId;
    private bool _disposed;

    public Task<PlatformOperationResult> AddProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return Task.FromResult(PlatformOperationResult.Failure("The DSH process group has already been disposed."));
        }

        if (MacOSProcessNative.SetProcessGroup(processId, processId) != 0)
        {
            return Task.FromResult(PlatformOperationResult.Failure($"The created DSH process could not be placed in a separate process group (errno {Marshal.GetLastPInvokeError()})."));
        }

        _processGroupId = processId;
        return Task.FromResult(PlatformOperationResult.Success());
    }

    public Task<PlatformOperationResult> RequestGracefulStopAsync(CancellationToken cancellationToken = default) =>
        SendSignalAsync(SigTerm, cancellationToken);

    public Task<PlatformOperationResult> TerminateAsync(CancellationToken cancellationToken = default) =>
        SendSignalAsync(SigKill, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private Task<PlatformOperationResult> SendSignalAsync(int signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _processGroupId <= 0)
        {
            return Task.FromResult(PlatformOperationResult.Failure("The DSH process group has no owned root process."));
        }

        return Task.FromResult(MacOSProcessNative.Kill(-_processGroupId, signal) == 0
            ? PlatformOperationResult.Success()
            : PlatformOperationResult.Failure($"The owned DSH process group could not be signalled (errno {Marshal.GetLastPInvokeError()})."));
    }

    private static partial class MacOSProcessNative
    {
        [LibraryImport("libSystem.B.dylib", EntryPoint = "setpgid", SetLastError = true)]
        internal static partial int SetProcessGroup(int processId, int processGroupId);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
        internal static partial int Kill(int processIdOrGroup, int signal);
    }
}
