using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DshNgDesktop.Dsh;

/// <summary>
/// Starts npx with posix_spawn's process-group attribute already applied to
/// the child. This avoids the EACCES race caused by trying to repair process
/// ownership after an already-running child has begun exec'ing its script.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacOSDshProcessLauncher : IDshProcessLauncher
{
    private const short PosixSpawnSetProcessGroup = 0x0002;
    private const int SigTerm = 15;
    private const int SigKill = 9;

    public Task<IDshProcessHandle> LaunchAsync(
        DshProcessLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"The private DSH working directory does not exist: {request.WorkingDirectory}");
        }

        var arguments = request.Arguments ??
        [
            "--yes",
            "@deepseek-ai/dsh",
            "web",
            "--host",
            "127.0.0.1",
            "--port",
            request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ];
        var argv = new List<string>(arguments.Count + 1) { request.NpxExecutable };
        argv.AddRange(arguments);
        var environment = BuildEnvironment(request);

        var stdout = new PipeDescriptors();
        var stderr = new PipeDescriptors();
        nint fileActions = 0;
        nint attributes = 0;
        var spawnedProcessId = 0;
        var succeeded = false;
        FileStream? stdoutStream = null;
        FileStream? stderrStream = null;
        try
        {
            EnsureNativeSuccess(NativePipe.Pipe(ref stdout), "pipe(stdout)");
            EnsureNativeSuccess(NativePipe.Pipe(ref stderr), "pipe(stderr)");
            EnsureNativeSuccess(NativePipe.FileActionsInit(out fileActions), "posix_spawn_file_actions_init");
            EnsureNativeSuccess(NativePipe.FileActionsAddChdir(ref fileActions, request.WorkingDirectory), "posix_spawn_file_actions_addchdir_np");
            EnsureNativeSuccess(NativePipe.FileActionsAddDup2(ref fileActions, stdout.Write, 1), "posix_spawn_file_actions_adddup2(stdout)");
            EnsureNativeSuccess(NativePipe.FileActionsAddClose(ref fileActions, stdout.Read), "posix_spawn_file_actions_addclose(stdout)");
            if (stdout.Write != 1)
            {
                EnsureNativeSuccess(NativePipe.FileActionsAddClose(ref fileActions, stdout.Write), "posix_spawn_file_actions_addclose(stdout-write)");
            }

            EnsureNativeSuccess(NativePipe.FileActionsAddDup2(ref fileActions, stderr.Write, 2), "posix_spawn_file_actions_adddup2(stderr)");
            EnsureNativeSuccess(NativePipe.FileActionsAddClose(ref fileActions, stderr.Read), "posix_spawn_file_actions_addclose(stderr)");
            if (stderr.Write != 2)
            {
                EnsureNativeSuccess(NativePipe.FileActionsAddClose(ref fileActions, stderr.Write), "posix_spawn_file_actions_addclose(stderr-write)");
            }

            EnsureNativeSuccess(NativePipe.AttributesInit(out attributes), "posix_spawnattr_init");
            EnsureNativeSuccess(NativePipe.AttributesSetFlags(ref attributes, PosixSpawnSetProcessGroup), "posix_spawnattr_setflags");
            EnsureNativeSuccess(NativePipe.AttributesSetProcessGroup(ref attributes, 0), "posix_spawnattr_setpgroup");

            using var nativeArguments = NativeStringVector.Create(argv);
            using var nativeEnvironment = NativeStringVector.Create(environment.Select(pair => $"{pair.Key}={pair.Value}"));
            EnsureNativeSuccess(
                NativePipe.PosixSpawn(
                    out spawnedProcessId,
                    request.NpxExecutable,
                    ref fileActions,
                    ref attributes,
                    nativeArguments.Pointer,
                    nativeEnvironment.Pointer),
                "posix_spawn");

            CloseDescriptor(ref stdout.Write);
            CloseDescriptor(ref stderr.Write);
            stdoutStream = CreateReadStream(ref stdout.Read);
            stderrStream = CreateReadStream(ref stderr.Read);
            var handle = new MacOSDshProcessHandle(
                spawnedProcessId,
                stdoutStream,
                stderrStream,
                request.OutputReceived);
            stdoutStream = null;
            stderrStream = null;
            succeeded = true;
            return Task.FromResult<IDshProcessHandle>(handle);
        }
        catch
        {
            if (spawnedProcessId > 0 && !succeeded)
            {
                NativePipe.Kill(spawnedProcessId, SigKill);
                NativePipe.WaitPid(spawnedProcessId, out _, 0);
            }

            throw;
        }
        finally
        {
            stdoutStream?.Dispose();
            stderrStream?.Dispose();
            CloseDescriptor(ref stdout.Read);
            CloseDescriptor(ref stdout.Write);
            CloseDescriptor(ref stderr.Read);
            CloseDescriptor(ref stderr.Write);
            if (fileActions != 0)
            {
                NativePipe.FileActionsDestroy(ref fileActions);
            }

            if (attributes != 0)
            {
                NativePipe.AttributesDestroy(ref attributes);
            }
        }
    }

    private static Dictionary<string, string> BuildEnvironment(DshProcessLaunchRequest request)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "HOME", "PATH", "TMPDIR", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "TERM" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[name] = value;
            }
        }

        foreach (var pair in request.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        var executableDirectory = Path.GetDirectoryName(request.NpxExecutable);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var currentPath = environment.TryGetValue("PATH", out var path) ? path : "/usr/bin:/bin";
            environment["PATH"] = string.Join(Path.PathSeparator, executableDirectory, currentPath);
        }

        return environment;
    }

    private static FileStream CreateReadStream(ref int descriptor)
    {
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        descriptor = -1;
        // Unix pipe descriptors are synchronous; isAsync would require a
        // Windows-style overlapped handle and throws ArgumentException.
        return new FileStream(handle, FileAccess.Read, 4_096, isAsync: false);
    }

    private static void CloseDescriptor(ref int descriptor)
    {
        if (descriptor >= 0)
        {
            NativePipe.Close(descriptor);
            descriptor = -1;
        }
    }

    private static void EnsureNativeSuccess(int result, string operation)
    {
        if (result != 0)
        {
            throw new IOException($"{operation} failed with errno {result}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipeDescriptors
    {
        public int Read;
        public int Write;
    }

    private sealed class NativeStringVector : IDisposable
    {
        private readonly List<nint> _strings;

        private NativeStringVector(nint pointer, List<nint> strings)
        {
            Pointer = pointer;
            _strings = strings;
        }

        public nint Pointer { get; }

        public static NativeStringVector Create(IEnumerable<string> values)
        {
            var strings = new List<nint>();
            nint vector = 0;
            try
            {
                var items = values.ToArray();
                vector = Marshal.AllocHGlobal(IntPtr.Size * (items.Length + 1));
                for (var index = 0; index < items.Length; index++)
                {
                    var value = Marshal.StringToCoTaskMemUTF8(items[index]);
                    strings.Add(value);
                    Marshal.WriteIntPtr(vector, index * IntPtr.Size, value);
                }

                Marshal.WriteIntPtr(vector, items.Length * IntPtr.Size, 0);
                return new NativeStringVector(vector, strings);
            }
            catch
            {
                foreach (var value in strings)
                {
                    Marshal.FreeCoTaskMem(value);
                }

                if (vector != 0)
                {
                    Marshal.FreeHGlobal(vector);
                }

                throw;
            }
        }

        public void Dispose()
        {
            foreach (var value in _strings)
            {
                Marshal.FreeCoTaskMem(value);
            }

            Marshal.FreeHGlobal(Pointer);
        }
    }

    private static partial class NativePipe
    {
        // Darwin's posix_spawn API family takes posix_spawn_file_actions_t */
        // and posix_spawnattr_t * (the address of the opaque handle), not the
        // handle by value. Passing the handle itself makes libSystem
        // dereference it as void** and crash the process.
        [LibraryImport("libSystem.B.dylib", EntryPoint = "pipe", SetLastError = true)]
        internal static partial int Pipe(ref PipeDescriptors descriptors);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int descriptor);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_init", SetLastError = true)]
        internal static partial int FileActionsInit(out nint actions);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_destroy", SetLastError = true)]
        internal static partial int FileActionsDestroy(ref nint actions);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_adddup2", SetLastError = true)]
        internal static partial int FileActionsAddDup2(ref nint actions, int fileDescriptor, int newFileDescriptor);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_addclose", SetLastError = true)]
        internal static partial int FileActionsAddClose(ref nint actions, int fileDescriptor);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int FileActionsAddChdir(ref nint actions, string path);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawnattr_init", SetLastError = true)]
        internal static partial int AttributesInit(out nint attributes);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawnattr_destroy", SetLastError = true)]
        internal static partial int AttributesDestroy(ref nint attributes);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawnattr_setflags", SetLastError = true)]
        internal static partial int AttributesSetFlags(ref nint attributes, short flags);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawnattr_setpgroup", SetLastError = true)]
        internal static partial int AttributesSetProcessGroup(ref nint attributes, int processGroup);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "posix_spawn", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int PosixSpawn(
            out int processId,
            string path,
            ref nint fileActions,
            ref nint attributes,
            nint arguments,
            nint environment);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
        internal static partial int Kill(int processId, int signal);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "waitpid", SetLastError = true)]
        internal static partial int WaitPid(int processId, out int status, int options);
    }

    private sealed class MacOSDshProcessHandle : IDshProcessHandle
    {
        private readonly int _processId;
        private readonly FileStream _stdout;
        private readonly FileStream _stderr;
        private readonly Action<string, bool> _outputReceived;
        private readonly Task _waitTask;
        private int _hasExited;
        private int? _exitCode;
        private int _disposed;

        public MacOSDshProcessHandle(
            int processId,
            FileStream stdout,
            FileStream stderr,
            Action<string, bool> outputReceived)
        {
            _processId = processId;
            _stdout = stdout;
            _stderr = stderr;
            _outputReceived = outputReceived;
            StartedAtUtc = DateTimeOffset.UtcNow;
            _ = PumpOutputAsync(_stdout, isError: false);
            _ = PumpOutputAsync(_stderr, isError: true);
            _waitTask = Task.Run(WaitForNativeExit);
        }

        public int ProcessId => _processId;

        public DateTimeOffset StartedAtUtc { get; }

        public bool HasExited => Volatile.Read(ref _hasExited) == 1;

        public int? ExitCode => _exitCode;

        public event EventHandler? Exited;

        public Task<bool> RequestGracefulStopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HasExited || NativePipe.Kill(_processId, SigTerm) == 0);
        }

        public Task ForceStopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExited)
            {
                NativePipe.Kill(_processId, SigKill);
            }

            return Task.CompletedTask;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _waitTask.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stdout.Dispose();
                _stderr.Dispose();
            }

            if (HasExited)
            {
                await _waitTask.ConfigureAwait(false);
            }
        }

        private void WaitForNativeExit()
        {
            var result = NativePipe.WaitPid(_processId, out var status, 0);
            _exitCode = result < 0 ? 1 : DecodeExitCode(status);
            Volatile.Write(ref _hasExited, 1);
            Exited?.Invoke(this, EventArgs.Empty);
        }

        private async Task PumpOutputAsync(FileStream stream, bool isError)
        {
            try
            {
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _outputReceived(line, isError);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The supervisor owns shutdown; closing the read side is a
                // normal consequence of force-stopping the process group.
            }
        }

        private static int DecodeExitCode(int status)
        {
            var signal = status & 0x7f;
            return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
        }
    }
}
