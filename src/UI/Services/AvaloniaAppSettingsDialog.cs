using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiVideoEditor.UI.Services;

/// <summary>D057 §3.8: the application's settings window, modal over the main window, opened at its section.</summary>
public sealed class AvaloniaAppSettingsDialog : IAppSettingsDialog
{
    public async Task ShowAsync(AppSettingsViewModel settings)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;

        var window = new Window
        {
            Title = Strings.AppSettings_Title,
            Width = 520,
            Height = 620,
            MinWidth = 420,
            MinHeight = 360,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new AppSettingsView { DataContext = settings }
        };

        void Close(object? sender, EventArgs e) => window.Close();
        settings.CloseRequested += Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            settings.CloseRequested -= Close;
        }
    }
}
