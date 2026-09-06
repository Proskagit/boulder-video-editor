using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AiVideoEditor.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AiVideoEditor.App;

public partial class App : Avalonia.Application
{
    /// <summary>Root DI container, set once by <see cref="Program"/> before the Avalonia
    /// lifetime starts. Views are resolved through it so every window/control gets its
    /// dependencies via constructor injection instead of service-locating ad hoc.</summary>
    public static IServiceProvider Services { get; set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
