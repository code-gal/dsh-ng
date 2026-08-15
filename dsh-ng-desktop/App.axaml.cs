using Avalonia;
using Avalonia.Markup.Xaml;

namespace DshNgDesktop;

// 以 SetupWithoutStarting 方式承载，不使用经典桌面生命周期。
internal sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
