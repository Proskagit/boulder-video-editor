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
        SourceBar.FrameRequested += (_, frame) => _viewModel?.SeekSource(frame);
        // D069 §2: the empty timeline's hint wraps to the canvas's picture, not to the whole Preview.
        Composition.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == Rendering.CompositionView.CanvasProperty)
                EmptyHint.MaxWidth = EmptyHintWidth(Composition.Bounds.Size, Composition.Canvas);
        };
    }

    /// <summary>D069 §2: the widest the empty hint may be — the canvas fitted into <paramref name="area"/> (letterboxed, as
    /// the composition draws it) less a margin, at most 420 px and at least 120.</summary>
    internal static double EmptyHintWidth(Size area, Core.Composition.FrameSize canvas)
    {
        if (area.Width <= 0 || area.Height <= 0 || canvas.Width <= 0 || canvas.Height <= 0) return 420;
        var fitted = Math.Min(area.Width, area.Height * canvas.Width / canvas.Height);
        return Math.Clamp(fitted - 24, 120, 420);
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
