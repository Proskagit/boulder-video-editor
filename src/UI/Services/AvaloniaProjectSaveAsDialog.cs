using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// <see cref="IProjectSaveAsDialog"/> as a small modal window over the main window (as <see cref="AvaloniaProjectSettingsDialog"/>)
/// hosting <see cref="SaveAsView"/>. «Сохранить» / Enter accepts, «Отмена» / Esc / the title bar's close button cancel.
/// </summary>
public sealed class AvaloniaProjectSaveAsDialog : IProjectSaveAsDialog
{
    public async Task ShowAsync(SaveAsViewModel saveAs)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;

        var view = new SaveAsView { DataContext = saveAs };
        var window = new Window
        {
            Title = Strings.SaveAs_Title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = view
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { saveAs.CancelCommand.Execute(null); e.Handled = true; }
        };
        window.Opened += (_, _) => view.FocusName();

        void Close(object? sender, bool accepted) => window.Close();
        saveAs.CloseRequested += Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            saveAs.CloseRequested -= Close;
        }
    }
}
