using AiVideoEditor.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// <see cref="IExportProgressDialog"/> as a modal window over the main window (like <see cref="AvaloniaDialogService"/>,
/// built in code): stage, progress bar, "done / total" and Cancel. A timer pulls the latest progress
/// (<see cref="ExportProgressViewModel.Refresh"/>) on the UI thread. The title-bar close button and Alt+F4 only
/// request cancellation; the window closes when the export has finished (<see cref="Close"/>).
/// </summary>
public sealed class AvaloniaExportProgressDialog : IExportProgressDialog
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private Window? _window;
    private DispatcherTimer? _timer;
    private bool _closing;

    /// <summary>The open window (tests).</summary>
    internal Window? Window => _window;

    public void Show(ExportProgressViewModel progress)
    {
        if (_window is not null) throw new InvalidOperationException("The export progress window is already shown.");
        _closing = false;

        var window = new Window
        {
            Title = "Exporting",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = progress
        };

        var stage = new TextBlock { FontWeight = FontWeight.SemiBold };
        stage.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.StageText)));
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        bar.Bind(RangeBase.ValueProperty, new Binding(nameof(ExportProgressViewModel.Percent)));
        var detail = new TextBlock();
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.DetailText)));
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(ExportProgressViewModel.CancelCommand)));

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = progress.OutputPath, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                stage,
                bar,
                detail,
                cancel
            }
        };

        window.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;            // only the export's end closes the window
            progress.Cancel();
        };

        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => progress.Refresh());
        _timer.Start();
        _window = window;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            _ = window.ShowDialog(owner);
        else
            window.Show();
    }

    public void Close()
    {
        _timer?.Stop();
        _timer = null;
        if (_window is null) return;
        _closing = true;
        _window.Close();
        _window = null;
    }
}
