using AiVideoEditor.UI.Localization;
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
/// <see cref="IExportProgressDialog"/> as a window owned by the main window (built in code, like
/// <see cref="AvaloniaDialogService"/>): which version of the project is exported, the output, the stage, the progress bar of
/// the whole export, "done / total", the speed and time left, the encoder, «Свернуть» and «Отменить экспорт». A timer pulls the
/// latest progress (<see cref="ExportProgressViewModel.Refresh"/>) on the UI thread from <see cref="Show"/> to
/// <see cref="Close"/> — also while the window is hidden, for the main window's compact indicator.
/// <para>
/// D060 §5: not modal (<see cref="Window.Show(Window)"/>, owned, so it stays above the editor without blocking it). The
/// title-bar close button, Alt+F4 and «Свернуть» hide it; the export goes on. It closes when the export has finished.
/// </para>
/// </summary>
public sealed class AvaloniaExportProgressDialog : IExportProgressDialog
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private Window? _window;
    private DispatcherTimer? _timer;
    private bool _closing;

    /// <summary>The window of the running export, shown or hidden (tests).</summary>
    internal Window? Window => _window;

    public void Show(ExportProgressViewModel progress)
    {
        if (_window is not null) throw new InvalidOperationException("The export progress window is already shown.");
        _closing = false;

        var window = new Window
        {
            Title = Strings.Export_WindowTitle,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = progress
        };

        var stage = new TextBlock { FontWeight = FontWeight.SemiBold };
        stage.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.StageText)));
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        bar.Bind(RangeBase.ValueProperty, new Binding(nameof(ExportProgressViewModel.OverallPercent)));
        var detail = new TextBlock();
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.DetailText)));
        // D059 §4: the speed and the time left; the encoder actually used and a fallback's reason.
        var speed = new TextBlock { Opacity = 0.8, FontSize = 12 };
        speed.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.SpeedText)));
        var encoder = new TextBlock { Opacity = 0.8, FontSize = 12 };
        encoder.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.EncoderText)));
        var fallback = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E0A040")) };
        fallback.Bind(TextBlock.TextProperty, new Binding(nameof(ExportProgressViewModel.Fallback)) { StringFormat = Strings.Export_FallbackLine });
        fallback.Bind(Visual.IsVisibleProperty, new Binding(nameof(ExportProgressViewModel.Fallback)) { Converter = Avalonia.Data.Converters.ObjectConverters.IsNotNull });

        var hide = new Button { Name = "HideButton", Content = Strings.Export_Hide, MinWidth = 90 };
        ToolTip.SetTip(hide, Strings.Export_HideTip);
        hide.Click += (_, _) => window.Hide();
        var cancel = new Button { Name = "CancelExportButton", Content = Strings.Export_CancelExport, MinWidth = 90 };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(ExportProgressViewModel.CancelCommand)));

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = progress.SnapshotText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.9,
                    IsVisible = progress.SnapshotText.Length > 0 },
                new TextBlock { Text = progress.OutputPath, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                stage,
                bar,
                detail,
                speed,
                encoder,
                fallback,
                new TextBlock { Text = Strings.Export_BackgroundNote, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { hide, cancel }
                }
            }
        };

        window.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;            // D060 §5: closing hides it — the export goes on; only its end closes the window
            window.Hide();
        };

        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => progress.Refresh());
        _timer.Start();
        _window = window;
        ShowWindow(window);
    }

    public void Activate()
    {
        if (_window is not { } window) return;
        if (!window.IsVisible) ShowWindow(window);
        window.Activate();
    }

    private static void ShowWindow(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            window.Show(owner);
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
