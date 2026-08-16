using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshDesktop.SetupHost;

/// <summary>
/// A transport-only native host. It never participates in the product install
/// transaction: it validates one embedded publish closure, expands it into a
/// private staging directory, waits for the Avalonia installer process and
/// removes that directory before starting the committed desktop client.
/// </summary>
internal static class Program
{
    private const string PayloadArchiveResource = "DshDesktop.SetupHost.PayloadArchive";
    private const string PayloadManifestResource = "DshDesktop.SetupHost.PayloadManifest";
    private const int InstallerFailureExitCode = 1;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("DSH Desktop 安装器只能在 Windows 上运行。", InstallerFailureExitCode);
        }

        string? stagingDirectory = null;
        try
        {
            var manifest = ReadPayloadManifest();
            ValidateManifest(manifest);
            EnsureDotNetPrerequisite(manifest);

            stagingDirectory = CreateStagingDirectory();
            ExtractAndVerifyPayload(manifest, stagingDirectory);

            var installerExitCode = RunInstaller(manifest, stagingDirectory);
            if (installerExitCode != 0)
            {
                return installerExitCode;
            }

            DeleteStagingDirectory(stagingDirectory);
            stagingDirectory = null;
            return StartInstalledClient(manifest);
        }
        catch (SetupHostException exception)
        {
            return Fail(exception.Message, InstallerFailureExitCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return Fail($"DSH Desktop 安装器无法继续：{exception.Message}", InstallerFailureExitCode);
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try
                {
                    DeleteStagingDirectory(stagingDirectory);
                }
                catch
                {
                    // A user-visible primary error has already been reported.
                    // Never let a cleanup exception hide the installer result.
                }
            }
        }
    }

    private static SetupPayloadManifest ReadPayloadManifest()
    {
        using var stream = GetRequiredEmbeddedResource(PayloadManifestResource);
        var manifest = JsonSerializer.Deserialize(stream, SetupPayloadJsonContext.Default.SetupPayloadManifest);
        return manifest ?? throw new SetupHostException("安装器内嵌的负载清单为空或无法读取。");
    }

    private static Stream GetRequiredEmbeddedResource(string resourceName)
    {
        var stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
        return stream ?? throw new SetupHostException("安装器内嵌的客户端负载不完整。");
    }

    private static void ValidateManifest(SetupPayloadManifest manifest)
    {
        if (manifest.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.MainExecutableRelativePath) ||
            manifest.Files.Count == 0 ||
            manifest.RequiredLaunchFiles.Count == 0)
        {
            throw new SetupHostException("安装器内嵌的客户端负载清单无效。");
        }

        if (manifest.RequiresDotNetDesktopRuntime && manifest.RequiredDotNetDesktopMajorVersion <= 0)
        {
            throw new SetupHostException("安装器内嵌的 .NET Runtime 前置条件无效。");
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.RelativePath);
            if (string.IsNullOrEmpty(relativePath) || file.Length < 0 || !IsSha256(file.Sha256) || !files.Add(relativePath))
            {
                throw new SetupHostException("安装器内嵌的客户端负载清单包含重复或无效文件。");
            }
        }

        var mainExecutable = NormalizeRelativePath(manifest.MainExecutableRelativePath);
        if (!files.Contains(mainExecutable))
        {
            throw new SetupHostException("安装器负载缺少主客户端程序。");
        }

        foreach (var requiredFile in manifest.RequiredLaunchFiles)
        {
            if (!files.Contains(NormalizeRelativePath(requiredFile)))
            {
                throw new SetupHostException("安装器负载缺少声明的客户端运行库。");
            }
        }
    }

    private static void EnsureDotNetPrerequisite(SetupPayloadManifest manifest)
    {
        if (!manifest.RequiresDotNetDesktopRuntime)
        {
            return;
        }

        if (!HasRequiredDesktopRuntime(manifest.RequiredDotNetDesktopMajorVersion))
        {
            throw new SetupHostException(
                $"此 DSH Desktop 安装包需要 .NET {manifest.RequiredDotNetDesktopMajorVersion} Desktop Runtime。请先安装匹配版本的 Windows Desktop Runtime，然后重新运行安装器。");
        }
    }

    private static bool HasRequiredDesktopRuntime(int requiredMajorVersion)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }

            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return false;
            }

            var expectedPrefix = $"Microsoft.WindowsDesktop.App {requiredMajorVersion}.";
            return outputTask.GetAwaiter().GetResult()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => line.StartsWith(expectedPrefix, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CreateStagingDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "DshDesktop.SetupHost");
        Directory.CreateDirectory(parent);
        EnsureNotReparsePoint(parent, "临时负载父目录");

        var staging = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        EnsureNotReparsePoint(staging, "临时负载目录");
        return staging;
    }

    private static void ExtractAndVerifyPayload(SetupPayloadManifest manifest, string stagingDirectory)
    {
        var expectedFiles = manifest.Files.ToDictionary(
            file => NormalizeRelativePath(file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var archiveStream = GetRequiredEmbeddedResource(PayloadArchiveResource);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                _ = NormalizeRelativePath(entry.FullName);
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.FullName);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            if (!expectedFiles.TryGetValue(relativePath, out var expectedFile) ||
                !extractedFiles.Add(relativePath) ||
                entry.Length != expectedFile.Length)
            {
                throw new SetupHostException("安装器负载与内嵌清单不匹配，已拒绝启动安装。\n");
            }

            var destination = GetSafeExtractionPath(stagingDirectory, relativePath);
            EnsureSafeParentDirectories(stagingDirectory, destination);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        if (extractedFiles.Count != expectedFiles.Count || !expectedFiles.Keys.All(extractedFiles.Contains))
        {
            throw new SetupHostException("安装器负载缺少客户端文件，已拒绝启动安装。");
        }

        foreach (var expected in expectedFiles)
        {
            var target = GetSafeExtractionPath(stagingDirectory, expected.Key);
            if (!File.Exists(target))
            {
                throw new SetupHostException("安装器负载缺少客户端文件，已拒绝启动安装。");
            }

            var info = new FileInfo(target);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != expected.Value.Length)
            {
                throw new SetupHostException("安装器负载文件无效，已拒绝启动安装。");
            }

            using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, expected.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupHostException("安装器负载文件校验失败，已拒绝启动安装。");
            }
        }

        foreach (var requiredFile in manifest.RequiredLaunchFiles)
        {
            var target = GetSafeExtractionPath(stagingDirectory, NormalizeRelativePath(requiredFile));
            if (!File.Exists(target))
            {
                throw new SetupHostException("安装器负载缺少主程序或声明的运行库，已拒绝启动安装。");
            }
        }
    }

    private static int RunInstaller(SetupPayloadManifest manifest, string stagingDirectory)
    {
        var executable = GetSafeExtractionPath(stagingDirectory, NormalizeRelativePath(manifest.MainExecutableRelativePath));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = stagingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "--install",
                    "--installer-session",
                    "--payload",
                    stagingDirectory
                }
            });
            if (process is null)
            {
                throw new SetupHostException("无法启动 DSH Desktop 原生安装引导。");
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SetupHostException($"无法启动 DSH Desktop 原生安装引导：{exception.Message}");
        }
    }

    private static int StartInstalledClient(SetupPayloadManifest manifest)
    {
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "DSH Desktop");
        var executable = Path.Combine(installRoot, NormalizeRelativePath(manifest.MainExecutableRelativePath));
        if (!File.Exists(executable))
        {
            return Fail("DSH Desktop 已完成安装，但找不到受管安装目录中的客户端程序。", InstallerFailureExitCode);
        }

        try
        {
            if (Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = installRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }) is null)
            {
                return Fail("DSH Desktop 已完成安装，但无法启动已安装客户端。", InstallerFailureExitCode);
            }

            return 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return Fail($"DSH Desktop 已完成安装，但无法启动已安装客户端：{exception.Message}", InstallerFailureExitCode);
        }
    }

    private static string GetSafeExtractionPath(string stagingDirectory, string relativePath)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupHostException("安装器负载包含越界路径，已拒绝安装。\n");
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            normalized.Split('/', StringSplitOptions.None).Any(part => part is "" or "." or ".."))
        {
            throw new SetupHostException("安装器负载包含不安全路径，已拒绝安装。");
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void EnsureSafeParentDirectories(string stagingDirectory, string destination)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var current = Path.GetDirectoryName(destination)
            ?? throw new SetupHostException("安装器负载目标路径无效。");
        var pending = new Stack<string>();
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            pending.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new SetupHostException("安装器负载路径超出临时目录。");
        }

        while (pending.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            EnsureNotReparsePoint(directory, "安装器负载目录");
        }
    }

    private static void EnsureNotReparsePoint(string path, string displayName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupHostException($"{displayName}不能是符号链接或重解析点。");
        }
    }

    private static void DeleteStagingDirectory(string stagingDirectory)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return;
        }

        DeleteDirectoryContents(stagingDirectory);
        Directory.Delete(stagingDirectory, recursive: false);
    }

    private static void DeleteDirectoryContents(string directory)
    {
        EnsureNotReparsePoint(directory, "临时负载目录");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteDirectoryContents(entry);
                Directory.Delete(entry, recursive: false);
                continue;
            }

            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            (character is >= '0' and <= '9') ||
            (character is >= 'a' and <= 'f') ||
            (character is >= 'A' and <= 'F'));

    private static int Fail(string message, int exitCode)
    {
        NativeDialog.ShowError(message);
        return exitCode;
    }
}

internal sealed class SetupHostException(string message) : Exception(message);

internal sealed class SetupPayloadManifest
{
    public int SchemaVersion { get; init; }

    public bool RequiresDotNetDesktopRuntime { get; init; }

    public int RequiredDotNetDesktopMajorVersion { get; init; }

    public string MainExecutableRelativePath { get; init; } = string.Empty;

    public List<string> RequiredLaunchFiles { get; init; } = [];

    public List<SetupPayloadFile> Files { get; init; } = [];
}

internal sealed class SetupPayloadFile
{
    public string RelativePath { get; init; } = string.Empty;

    public long Length { get; init; }

    public string Sha256 { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SetupPayloadManifest))]
internal sealed partial class SetupPayloadJsonContext : JsonSerializerContext;

internal static partial class NativeDialog
{
    private const uint MbIconError = 0x00000010;
    private const uint MbOk = 0;

    public static void ShowError(string message) =>
        _ = MessageBoxW(0, message, "DSH Desktop 安装器", MbOk | MbIconError);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint windowHandle, string text, string caption, uint type);
}
