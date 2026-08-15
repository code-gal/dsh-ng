using DshNgDesktop.Core;
using DshNgDesktop.Infrastructure;
using Xunit;

namespace DshNgDesktop.Tests;

public sealed class M1FoundationTests
{
    [Fact]
    public void StateMachine_AllowsInstallRollbackAndRejectsInvalidTransitions()
    {
        var stateMachine = new ApplicationStateMachine();

        stateMachine.TransitionTo(ApplicationState.Preflight, "Checking prerequisites");
        stateMachine.TransitionTo(ApplicationState.DeployingClient, "Deploying client files");
        stateMachine.TransitionTo(ApplicationState.Stopping, "User requested cancellation");
        stateMachine.TransitionTo(ApplicationState.RollingBack, "Removing transaction resources");
        var failed = stateMachine.TransitionTo(ApplicationState.Failed, "Installation cancelled");

        Assert.Equal(ApplicationState.Failed, failed.State);
        Assert.Equal(5, failed.Sequence);
        Assert.Throws<InvalidApplicationStateTransitionException>(
            () => stateMachine.TransitionTo(ApplicationState.Ready, "Cannot skip startup"));
    }

    [Fact]
    public void InstallManifest_OnlyAuthorizesTheExactOwnedPaths()
    {
        var paths = AppPaths.Create(
            "Test.Product",
            "C:\\test-product\\install",
            "C:\\test-product\\data",
            "C:\\test-product\\data\\state",
            "C:\\test-product\\data\\logs",
            "C:\\test-product\\data\\runtime\\npm-cache",
            "C:\\test-product\\data\\dsh-home",
            "C:\\test-product\\data\\runtime\\launcher-cwd",
            "C:\\test-product\\data\\webview");
        var manifest = InstallManifest.Create(paths);

        manifest.ValidateAgainst(paths);

        Assert.True(manifest.CanDelete(ManagedPathKind.DshHome, paths.DshHomeDirectory, paths));
        Assert.False(manifest.CanDelete(ManagedPathKind.DshHome, "C:\\test-product\\workspace", paths));
        Assert.Throws<InvalidDataException>(() => new InstallManifest(
            InstallManifest.CurrentSchemaVersion,
            paths.ProductId,
            DateTimeOffset.UtcNow,
            [new ManagedPathRecord((ManagedPathKind)999, "C:\\test-product\\unknown")]).ValidateAgainst(paths));
    }

    [Fact]
    public async Task AppLog_RedactsSecretsAndRotatesWithinItsDedicatedDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "m1-log-test", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Create(
            "Test.Product",
            Path.Combine(root, "install"),
            Path.Combine(root, "data"),
            Path.Combine(root, "data", "state"),
            Path.Combine(root, "data", "logs"),
            Path.Combine(root, "data", "npm-cache"),
            Path.Combine(root, "data", "dsh-home"),
            Path.Combine(root, "data", "launcher-cwd"),
            Path.Combine(root, "data", "webview"));

        try
        {
            await using var log = new AppLog(paths, new AppLogSettings(MaximumFileBytes: 4_096, RetainedFileCount: 2));
            var message = $"api_key=super-secret {new string('x', 3_800)}";
            await log.InformationAsync(AppLogStream.Runtime, "runtime-start", message);
            await log.InformationAsync(AppLogStream.Runtime, "runtime-start", message);

            var content = await File.ReadAllTextAsync(log.RuntimeLogPath);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", content, StringComparison.Ordinal);
            Assert.True(File.Exists($"{log.RuntimeLogPath}.1"));
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
    public async Task SingleInstanceCoordinator_ActivatesThePrimaryInstance()
    {
        var productId = $"Test.Product.{Guid.NewGuid():N}";
        var primary = new SingleInstanceCoordinator(productId);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => activated.TrySetResult();

        try
        {
            Assert.True(primary.TryAcquirePrimary());

            await using var secondary = new SingleInstanceCoordinator(productId);
            Assert.False(secondary.TryAcquirePrimary());
            Assert.True(await secondary.RequestActivationAsync());
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await primary.DisposeAsync();
        }

        await using var replacement = new SingleInstanceCoordinator(productId);
        Assert.True(replacement.TryAcquirePrimary());
    }
}
