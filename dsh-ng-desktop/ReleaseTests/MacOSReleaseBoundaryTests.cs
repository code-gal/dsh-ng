using DshNgDesktop.Core;
using DshNgDesktop.Dsh;
using DshNgDesktop.Infrastructure;
using DshNgDesktop.Installer;
using DshNgDesktop.Platform;
using Xunit;

namespace DshNgDesktop.ReleaseTests;

/// <summary>
/// Release-only macOS boundary tests. They stay under source control so the
/// GitHub arm64 release job cannot silently omit platform acceptance checks.
/// </summary>
public sealed class MacOSReleaseBoundaryTests
{
    [Fact]
    public async Task MaintenanceLease_PreventsAnotherPrimaryDuringReplacement()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var productId = $"ReleaseTests.Maintenance.{Guid.NewGuid():N}";
        await using var installer = new SingleInstanceCoordinator(productId);
        var acquisition = await installer.AcquireAsync(TimeSpan.FromSeconds(2));
        Assert.True(acquisition.Succeeded, acquisition.Error);

        await using var blockedClient = new SingleInstanceCoordinator(productId);
        Assert.False(blockedClient.TryAcquirePrimary());

        await acquisition.Lease!.DisposeAsync();
        await using var nextClient = new SingleInstanceCoordinator(productId);
        Assert.True(nextClient.TryAcquirePrimary());
    }

    [Fact]
    public async Task Launcher_UsesPrivateWorkingDirectoryAndPreExecProcessGroup()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "dsh-desktop-release-tests", Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(root, "private-launcher-cwd");
        Directory.CreateDirectory(workingDirectory);
        var currentDirectory = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var launcher = new MacOSDshProcessLauncher();
            await using var process = await launcher.LaunchAsync(new DshProcessLaunchRequest(
                "/bin/sh",
                0,
                workingDirectory,
                new Dictionary<string, string>(),
                (line, _) => currentDirectory.TrySetResult(line),
                ["-c", "pwd; sleep 30"]));
            await using var group = PlatformServices.CreateDefault().CreateProcessGroup();

            Assert.Equal(workingDirectory, await currentDirectory.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var ownership = await group.AddProcessAsync(process.ProcessId);
            Assert.True(ownership.Succeeded, ownership.Error);

            var termination = await group.TerminateAsync();
            Assert.True(termination.Succeeded, termination.Error);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Validator_UsesResolvedNodeForEnvBasedNpxShebang()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "dsh-desktop-release-tests", Guid.NewGuid().ToString("N"));
        var binaries = Path.Combine(root, "bin");
        Directory.CreateDirectory(binaries);
        var node = Path.Combine(binaries, "node");
        var npx = Path.Combine(binaries, "npx");
        await File.WriteAllTextAsync(node, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo v22.0.0; exit 0; fi\nif [ \"$2\" = \"--version\" ]; then echo 10.0.0; exit 0; fi\nexit 1\n");
        await File.WriteAllTextAsync(npx, "#!/usr/bin/env node\n");
        var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(node, executableMode);
        File.SetUnixFileMode(npx, executableMode);

        try
        {
            var validator = new SystemDshExecutableValidator(
                TimeSpan.FromSeconds(5),
                new FixedResolver(node, npx));

            var result = await validator.ValidateAsync();

            Assert.True(result.Succeeded, result.FirstFailure.Error);
            Assert.Equal(Path.GetFullPath(npx), result.Npx.ExecutablePath);
            Assert.Contains(binaries, result.Npx.ExecutionPath, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Deployment_PreservesMacOSAppExecutableModes()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "dsh-desktop-release-tests", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "payload", "DSH Desktop.app");
        var installRoot = Path.Combine(root, "installed", "DSH Desktop.app");
        var executable = Path.Combine(payload, "Contents", "MacOS", "DshNgDesktop");
        var uninstall = Path.Combine(payload, "Contents", "Resources", "Uninstall DSH Desktop.command");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(uninstall)!);
        await File.WriteAllTextAsync(executable, "client");
        await File.WriteAllTextAsync(uninstall, "uninstall");
        var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(executable, executableMode);
        File.SetUnixFileMode(uninstall, executableMode);

        try
        {
            var deployment = new FileSystemClientDeployment(CreatePaths(root, installRoot));
            var result = await deployment.DeployAsync(new ClientDeploymentRequest(payload, installRoot));

            Assert.Equal(executableMode, File.GetUnixFileMode(Path.Combine(installRoot, "Contents", "MacOS", "DshNgDesktop")));
            Assert.Equal(executableMode, File.GetUnixFileMode(Path.Combine(installRoot, "Contents", "Resources", "Uninstall DSH Desktop.command")));
            await deployment.RollbackAsync(result);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FixedResolver(string node, string npx) : INodeExecutableResolver
    {
        public NodeExecutableResolution? Resolve(string command) => command switch
        {
            "node" => new NodeExecutableResolution(command, node, "release-test"),
            "npx" => new NodeExecutableResolution(command, npx, "release-test"),
            _ => null
        };
    }

    private static AppPaths CreatePaths(string root, string installRoot) => AppPaths.Create(
        "DshDesktop.ReleaseTests",
        installRoot,
        Path.Combine(root, "data"),
        Path.Combine(root, "data", "state"),
        Path.Combine(root, "data", "logs"),
        Path.Combine(root, "data", "runtime", "npm-cache"),
        Path.Combine(root, "data", "dsh-home"),
        Path.Combine(root, "data", "runtime", "launcher-cwd"),
        Path.Combine(root, "data", "webview"));
}
