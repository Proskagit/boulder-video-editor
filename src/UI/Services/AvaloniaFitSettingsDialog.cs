using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiVideoEditor.UI.Services;

/// <summary><see cref="IFitSettingsDialog"/> as a small modal window over the main window (like
/// <see cref="AvaloniaProjectSettingsDialog"/>) hosting <see cref="FitSettingsView"/>; it closes on Close, Esc or the
/// title bar — each change has been written already.</summary>
public sealed class AvaloniaFitSettingsDialog : IFitSettingsDialog
{
    public async Task ShowAsync(FitSettingsViewModel settings)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;

        var window = new Window
        {
            Title = Strings.FitSettings_Title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new FitSettingsView { DataContext = settings }
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
