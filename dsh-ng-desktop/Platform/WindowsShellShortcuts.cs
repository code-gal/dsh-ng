using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DshNgDesktop.Platform;

/// <summary>
/// Creates per-user Shell Link files without invoking PowerShell, cmd.exe or
/// Windows Script Host. The manual COM vtable calls stay compatible with the
/// Native AOT release contract and are isolated to this Windows boundary.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsShellShortcuts
{
    private const uint ClassContextInProcessServer = 0x1;
    private const uint CoInitializeMultithreaded = 0x0;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterfaceId = new("0000010B-0000-0000-C000-000000000046");

    public static PlatformOperationResult Register(ShortcutRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        try
        {
            ValidateDisplayName(registration.DisplayName);
            var executablePath = Path.GetFullPath(registration.ExecutablePath);
            var workingDirectory = Path.GetFullPath(registration.WorkingDirectory);
            if (!File.Exists(executablePath))
            {
                return PlatformOperationResult.Failure($"The shortcut target does not exist: {executablePath}");
            }

            var shortcutPaths = GetShortcutPaths(registration.DisplayName);
            var temporaryPaths = shortcutPaths
                .Select(path => $"{path}.{Guid.NewGuid():N}.tmp")
                .ToArray();

            try
            {
                for (var index = 0; index < shortcutPaths.Length; index++)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(shortcutPaths[index])!);
                    CreateShortcut(
                        temporaryPaths[index],
                        executablePath,
                        workingDirectory,
                        registration.Description,
                        executablePath);
                }

                for (var index = 0; index < shortcutPaths.Length; index++)
                {
                    File.Move(temporaryPaths[index], shortcutPaths[index], overwrite: true);
                }
            }
            finally
            {
                foreach (var temporaryPath in temporaryPaths)
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return PlatformOperationResult.Failure(exception.Message);
        }
    }

    public static PlatformOperationResult Unregister(string displayName)
    {
        try
        {
            ValidateDisplayName(displayName);
            foreach (var shortcutPath in GetShortcutPaths(displayName))
            {
                File.Delete(shortcutPath);
            }

            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return PlatformOperationResult.Failure(exception.Message);
        }
    }

    private static string[] GetShortcutPaths(string displayName)
    {
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(desktopDirectory) || string.IsNullOrWhiteSpace(programsDirectory))
        {
            throw new InvalidOperationException("The current-user Desktop or Start Menu directory is unavailable.");
        }

        var shortcutFileName = $"{displayName}.lnk";
        return
        [
            Path.Combine(desktopDirectory, shortcutFileName),
            Path.Combine(programsDirectory, shortcutFileName)
        ];
    }

    private static void ValidateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The shortcut display name contains invalid file-name characters.", nameof(displayName));
        }
    }

    private static unsafe void CreateShortcut(
        string shortcutPath,
        string executablePath,
        string workingDirectory,
        string description,
        string iconPath)
    {
        var initializationResult = CoInitializeEx(0, CoInitializeMultithreaded);
        var uninitialize = initializationResult >= 0;
        if (initializationResult < 0 && initializationResult != RpcChangedMode)
        {
            ThrowForHResult("COM initialization", initializationResult);
        }

        nint shellLink = 0;
        nint persistFile = 0;
        try
        {
            var shellLinkClassId = ShellLinkClassId;
            var shellLinkInterfaceId = ShellLinkInterfaceId;
            var result = CoCreateInstance(
                in shellLinkClassId,
                0,
                ClassContextInProcessServer,
                in shellLinkInterfaceId,
                out shellLink);
            ThrowForHResult("Shell Link creation", result);

            var shellLinkVtable = *(nint**)shellLink;
            fixed (char* executable = executablePath)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, int>)shellLinkVtable[20])(shellLink, executable);
            }
            ThrowForHResult("Shell Link target", result);

            fixed (char* directory = workingDirectory)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, int>)shellLinkVtable[9])(shellLink, directory);
            }
            ThrowForHResult("Shell Link working directory", result);

            fixed (char* descriptionPointer = description)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, int>)shellLinkVtable[7])(shellLink, descriptionPointer);
            }
            ThrowForHResult("Shell Link description", result);

            fixed (char* icon = iconPath)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)shellLinkVtable[17])(shellLink, icon, 0);
            }
            ThrowForHResult("Shell Link icon", result);

            var persistFileInterfaceId = PersistFileInterfaceId;
            result = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)shellLinkVtable[0])(
                shellLink,
                &persistFileInterfaceId,
                &persistFile);
            ThrowForHResult("Shell Link persistence interface", result);

            var persistFileVtable = *(nint**)persistFile;
            fixed (char* fileName = shortcutPath)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)persistFileVtable[6])(
                    persistFile,
                    fileName,
                    1);
            }
            ThrowForHResult("Shell Link save", result);
        }
        finally
        {
            ReleaseComInterface(persistFile);
            ReleaseComInterface(shellLink);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static unsafe void ReleaseComInterface(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        var vtable = *(nint**)instance;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(instance);
    }

    private static void ThrowForHResult(string operation, int hResult)
    {
        if (hResult < 0)
        {
            throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hResult:X8}.");
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outerUnknown,
        uint classContext,
        in Guid interfaceId,
        out nint instance);
}
