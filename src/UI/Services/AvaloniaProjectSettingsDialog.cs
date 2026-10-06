using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// <see cref="IProjectSettingsDialog"/> as a small modal window over the main window (like <see cref="AvaloniaDialogService"/>)
/// hosting <see cref="ProjectSettingsView"/>. It closes when the view model asks (Apply succeeded, Cancel); the title-bar
/// close button and Esc are a Cancel — nothing changes.
/// </summary>
public sealed class AvaloniaProjectSettingsDialog : IProjectSettingsDialog
{
    public async Task ShowAsync(ProjectSettingsViewModel settings)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;

        var window = new Window
        {
            Title = "Project Settings",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ProjectSettingsView { DataContext = settings }
        };

        void Close(object? sender, bool applied) => window.Close();
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
