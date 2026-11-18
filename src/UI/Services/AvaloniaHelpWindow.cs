using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace AiVideoEditor.UI.Services;

/// <summary>D068 §3: opens the in-app help (the toolbar's «?», F1).</summary>
public interface IHelpWindow
{
    void Show();
}

/// <summary>D068 §3: the help is one non-modal window over the main window — read while working; a second «?» / F1 brings
/// the open one to the front. Esc closes it.</summary>
public sealed class AvaloniaHelpWindow : IHelpWindow
{
    private Window? _window;

    public void Show()
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;

        var window = new Window
        {
            Title = Strings.Help_Title,
            Width = 980,
            Height = 680,
            MinWidth = 640,
            MinHeight = 400,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
            Content = new HelpView { DataContext = new HelpViewModel() }
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) window.Close();
        };
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show(owner);
    }
}
