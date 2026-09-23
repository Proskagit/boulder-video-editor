using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// <see cref="IDialogService"/> as a small modal window over the main window: a message and a
/// row of buttons. Built in code so it needs no extra XAML or dependencies.
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<int?> AskAsync(DialogRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return null;

        var dialog = new Window
        {
            Title = request.Title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        for (var i = 0; i < request.Buttons.Count; i++)
        {
            var index = i;
            var button = new Button { Content = request.Buttons[i], MinWidth = 90, IsDefault = i == 0 };
            button.Click += (_, _) => dialog.Close(index);
            buttons.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };

        return await dialog.ShowDialog<int?>(owner);
    }
}
