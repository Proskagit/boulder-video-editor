using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiVideoEditor.UI.Views.Panels;

/// <summary>
/// Drives <see cref="PreviewViewModel.Tick"/> from a UI-thread timer while the view is attached.
/// The composition itself is drawn by <see cref="Rendering.CompositionView"/>, bound to the view
/// model's layers and canvas. Everything here runs on the UI thread; decoding happens in the
/// playback service.
/// </summary>
public partial class PreviewView : UserControl
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);

    private readonly DispatcherTimer _timer;
    private PreviewViewModel? _viewModel;

    public PreviewView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, (_, _) => _viewModel?.Tick());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as PreviewViewModel;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
