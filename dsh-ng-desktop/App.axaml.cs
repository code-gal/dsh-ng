using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DshNgDesktop.Core;
using DshNgDesktop.Installer;
using DshNgDesktop.Views;

namespace DshNgDesktop;

internal sealed class App : Application
{
    public ApplicationStateMachine StateMachine { get; } = new();

    private SetupRuntime? _setupRuntime;
    private DesktopRuntime? _desktopRuntime;
    private MainWindow? _mainWindow;
    private bool _shutdownRequested;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureTrayIcon();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += Desktop_OnExit;

            var bootstrap = SetupApplicationBootstrap.Current;
            if (!bootstrap.ForceSetup && SetupLocations.IsCurrentProcessInstalled(bootstrap.Paths))
            {
                StartDesktopHost(desktop, bootstrap, setupRuntime: null, bootstrap.Background);
            }
            else
            {
                var setupRuntime = bootstrap.CreateRuntime(StateMachine);
                _setupRuntime = setupRuntime;
                var setupWindow = new SetupWindow(setupRuntime);
                setupWindow.InstallationCommitted += (_, _) =>
                {
                    if (bootstrap.InstallerSession)
                    {
                        return;
                    }

                    StartDesktopHost(desktop, bootstrap, setupRuntime, background: false);
                };
                setupWindow.Closed += (_, _) =>
                {
                    if (_desktopRuntime is null)
                    {
                        bootstrap.StartInstalledClientAfterExit = bootstrap.InstallerSession &&
                            setupRuntime.Coordinator.Result is { Succeeded: true };
                        desktop.Shutdown();
                    }
                };
                desktop.MainWindow = setupWindow;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal void ShowMainWindow()
    {
        if (_mainWindow is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (desktop.MainWindow is null)
        {
            desktop.MainWindow = _mainWindow;
        }

        _mainWindow.ShowAndActivate();
    }

    private void StartDesktopHost(
        IClassicDesktopStyleApplicationLifetime desktop,
        SetupApplicationBootstrap bootstrap,
        SetupRuntime? setupRuntime,
        bool background)
    {
        if (_desktopRuntime is not null)
        {
            return;
        }

        var setupWindow = desktop.MainWindow as SetupWindow;
        _desktopRuntime = bootstrap.CreateDesktopRuntime(StateMachine, setupRuntime);
        _desktopRuntime.Coordinator.SnapshotChanged += Coordinator_OnSnapshotChanged;
        _mainWindow = new MainWindow(_desktopRuntime);
        SetTrayVisibility(true);
        SetTrayToolTip("DSH Desktop — 正在启动 DSH");

        if (!background)
        {
            desktop.MainWindow = _mainWindow;
            _mainWindow.ShowAndActivate();
        }

        if (setupWindow is not null)
        {
            setupWindow.Close();
        }

        _ = StartDesktopRuntimeAsync(background);
    }

    private async Task StartDesktopRuntimeAsync(bool background)
    {
        if (_desktopRuntime is null)
        {
            return;
        }

        await _desktopRuntime.Coordinator.StartAsync().ConfigureAwait(false);
        if (!background)
        {
            return;
        }

        // A background login launch deliberately keeps MainWindow unassigned
        // until the user invokes the tray command or a second instance asks
        // to activate it. The explicit desktop shutdown mode keeps this host
        // and its tray icon alive without a visible window.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mainWindow?.IsVisible == true)
            {
                _mainWindow.Hide();
            }
        });
    }

    private void DesktopTrayIcon_OnClicked(object? sender, EventArgs eventArgs) => ShowMainWindow();

    private void OpenDshMenuItem_OnClick(object? sender, EventArgs eventArgs) => ShowMainWindow();

    private async void ExitMenuItem_OnClick(object? sender, EventArgs eventArgs) => await RequestExitAsync().ConfigureAwait(true);

    private async Task RequestExitAsync()
    {
        await RequestShutdownAsync().ConfigureAwait(true);
    }

    internal bool TryRequestUninstall()
    {
        // The installer process owns a pre-commit transaction rather than an
        // installed runtime. It must finish its own rollback path, so an
        // external uninstall request is rejected until a desktop host exists.
        if (_desktopRuntime is null)
        {
            return false;
        }

        _ = RequestShutdownAsync();
        return true;
    }

    private async Task RequestShutdownAsync()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        _mainWindow?.DestroyWebViewForExit();
        SetTrayVisibility(false);
        if (_desktopRuntime is not null)
        {
            await _desktopRuntime.Coordinator.StopAsync().ConfigureAwait(true);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private async void Desktop_OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs eventArgs)
    {
        if (!_shutdownRequested)
        {
            _shutdownRequested = true;
            _mainWindow?.DestroyWebViewForExit();
            SetTrayVisibility(false);
            if (_desktopRuntime is not null)
            {
                await _desktopRuntime.Coordinator.StopAsync().ConfigureAwait(true);
            }
        }

        if (_desktopRuntime is not null)
        {
            _desktopRuntime.Coordinator.SnapshotChanged -= Coordinator_OnSnapshotChanged;
            await _desktopRuntime.DisposeAsync().ConfigureAwait(true);
        }

        if (_setupRuntime is not null)
        {
            await _setupRuntime.DisposeAsync().ConfigureAwait(true);
        }
    }

    private void SetTrayVisibility(bool visible)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null)
        {
            return;
        }

        foreach (var icon in icons)
        {
            icon.IsVisible = visible;
        }
    }

    private void Coordinator_OnSnapshotChanged(object? sender, DesktopRuntimeSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => SetTrayToolTip(DescribeTrayState(snapshot)));
    }

    private void SetTrayToolTip(string text)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null)
        {
            return;
        }

        foreach (var icon in icons)
        {
            icon.ToolTipText = text;
        }
    }

    private static string DescribeTrayState(DesktopRuntimeSnapshot snapshot)
    {
        var activity = snapshot.Status switch
        {
            DesktopRuntimeStatus.Starting => snapshot.ActivityTitle ?? "正在启动 DSH",
            DesktopRuntimeStatus.Ready => "DSH 已就绪",
            DesktopRuntimeStatus.Faulted => snapshot.ActivityTitle ?? "DSH 出现故障",
            DesktopRuntimeStatus.Stopping => "正在停止 DSH",
            DesktopRuntimeStatus.Stopped => "DSH 已停止",
            _ => "正在启动 DSH"
        };
        return $"DSH Desktop — {activity}";
    }

    private void ConfigureTrayIcon()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null)
        {
            return;
        }

        foreach (var icon in icons)
        {
            icon.Clicked += DesktopTrayIcon_OnClicked;
            var openDsh = new NativeMenuItem { Header = "打开 DSH" };
            openDsh.Click += OpenDshMenuItem_OnClick;
            var exit = new NativeMenuItem { Header = "退出" };
            exit.Click += ExitMenuItem_OnClick;
            icon.Menu = new NativeMenu
            {
                Items =
                {
                    openDsh,
                    new NativeMenuItemSeparator(),
                    exit
                }
            };
        }
    }
}
