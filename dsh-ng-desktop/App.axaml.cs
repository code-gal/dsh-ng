using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DshNgDesktop.Core;
using DshNgDesktop.Installer;
using DshNgDesktop.Views;

namespace DshNgDesktop;

internal sealed class App : Application
{
    public ApplicationStateMachine StateMachine { get; } = new();

    private SetupRuntime? _setupRuntime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var bootstrap = SetupApplicationBootstrap.Current;
            if (!bootstrap.ForceSetup && SetupLocations.IsCurrentProcessInstalled(bootstrap.Paths))
            {
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                _setupRuntime = bootstrap.CreateRuntime(StateMachine);
                desktop.MainWindow = new SetupWindow(_setupRuntime);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
