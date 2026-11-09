using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiVideoEditor.UI.Services;

/// <summary>Shows the export dialog (<see cref="ExportDialogViewModel"/>); tests use a scripted one.</summary>
public interface IExportDialog
{
    /// <summary>The user's choice, or null when the dialog was cancelled or closed.</summary>
    Task<ExportDialogResult?> ShowAsync(ExportDialogViewModel dialog);
}

/// <summary>The export dialog as a modal window over the main window.</summary>
public sealed class AvaloniaExportDialog : IExportDialog
{
    public async Task<ExportDialogResult?> ShowAsync(ExportDialogViewModel dialog)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return null;
        var window = new Window
        {
            Title = Strings.ExportDialog_Title, Width = 520, Height = 440, MinWidth = 400, MinHeight = 340, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ExportDialogView { DataContext = dialog }
        };
        ExportDialogResult? result = null;
        void Close(object? sender, ExportDialogResult? chosen)
        {
            result = chosen;
            window.Close();
        }
        dialog.Closed += Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            dialog.Closed -= Close;
        }
        return result;
    }
}
