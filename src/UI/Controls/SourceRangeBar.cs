using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// The Source viewer's bar (Phase 16, D031): the asset's whole frames left to right, the source In / Out range as a band,
/// the frame shown as a line. A press or a drag asks for that frame (<see cref="FrameRequested"/>); the view model seeks.
/// Drawing only — no state of its own.
/// </summary>
public sealed class SourceRangeBar : Control
{
    public static readonly StyledProperty<long> FramesProperty = AvaloniaProperty.Register<SourceRangeBar, long>(nameof(Frames));
    public static readonly StyledProperty<long> FrameProperty = AvaloniaProperty.Register<SourceRangeBar, long>(nameof(Frame));
    public static readonly StyledProperty<long> InFrameProperty = AvaloniaProperty.Register<SourceRangeBar, long>(nameof(InFrame), -1);
    public static readonly StyledProperty<long> OutFrameProperty = AvaloniaProperty.Register<SourceRangeBar, long>(nameof(OutFrame), -1);

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush Band = new SolidColorBrush(Color.Parse("#4F7FB8"));
    private static readonly IPen Head = new Pen(new SolidColorBrush(Color.Parse("#E05A47")), 2);

    static SourceRangeBar()
    {
        AffectsRender<SourceRangeBar>(FramesProperty, FrameProperty, InFrameProperty, OutFrameProperty);
    }

    public long Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }
    public long Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public long InFrame { get => GetValue(InFrameProperty); set => SetValue(InFrameProperty, value); }
    public long OutFrame { get => GetValue(OutFrameProperty); set => SetValue(OutFrameProperty, value); }

    /// <summary>The user pressed or dragged on the bar at this frame (0 … <see cref="Frames"/>).</summary>
    public event EventHandler<long>? FrameRequested;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(Track, new Rect(0, h / 2 - 3, w, 6), 3);
        if (Frames <= 0) return;

        if (InFrame >= 0 || OutFrame >= 0)
        {
            var from = X(Math.Max(0, InFrame));
            var to = X(OutFrame >= 0 ? OutFrame : Frames);
            if (to > from) context.FillRectangle(Band, new Rect(from, h / 2 - 5, to - from, 10), 2);
        }
        var x = X(Math.Clamp(Frame, 0, Frames));
        context.DrawLine(Head, new Point(x, 1), new Point(x, h - 1));
    }

    private double X(long frame) => Bounds.Width * frame / Math.Max(1, Frames);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        Request(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Request(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    private void Request(double x)
    {
        if (Frames <= 0 || Bounds.Width <= 0) return;
        FrameRequested?.Invoke(this, (long)Math.Round(Math.Clamp(x / Bounds.Width, 0, 1) * Frames));
    }
}
