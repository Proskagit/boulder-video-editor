using AiVideoEditor.UI.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Draws a timeline clip's waveform (D024 Step 9.5): symmetric around a centre line, over the whole clip (audio) or its
/// lower half (video, PO-W1), one column per pixel by <see cref="WaveformLayout"/>, dimmed when muted (PO-W2). Only the
/// columns inside the timeline's visible area are computed and drawn — a clip can be millions of pixels wide at the
/// top zoom —, and the view redraws when the timeline scrolls.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<ClipWaveform?> WaveformProperty =
        AvaloniaProperty.Register<WaveformView, ClipWaveform?>(nameof(Waveform));

    private static readonly IImmutableSolidColorBrush Audible = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly IImmutableSolidColorBrush Muted = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    private ScrollViewer? _scroll;

    static WaveformView()
    {
        AffectsRender<WaveformView>(WaveformProperty);
    }

    public WaveformView() => IsHitTestVisible = false;

    public ClipWaveform? Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Waveform is not { } clip || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var (from, to) = VisibleColumns(Math.Min(Bounds.Width, WaveformLayout.ColumnCount(clip)));
        if (from >= to) return;

        var top = clip.LowerHalf ? Bounds.Height / 2 : 0;
        var half = (Bounds.Height - top) / 2;
        var centre = top + half;

        var heights = new double[to - from];
        for (var c = from; c < to; c++)
            heights[c - from] = WaveformLayout.Column(clip, c) * half;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(from, centre - heights[0]), isFilled: true);
            for (var c = from; c < to; c++)
            {
                g.LineTo(new Point(c, centre - heights[c - from]));
                g.LineTo(new Point(c + 1, centre - heights[c - from]));
            }
            for (var c = to - 1; c >= from; c--)
            {
                g.LineTo(new Point(c + 1, centre + heights[c - from]));
                g.LineTo(new Point(c, centre + heights[c - from]));
            }
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(clip.IsMuted ? Muted : Audible, null, geometry);
    }

    /// <summary>The clip-local columns inside the timeline's viewport (all of them without a scroll viewer).</summary>
    private (int From, int To) VisibleColumns(double width) =>
        _scroll is not null && this.TranslatePoint(default, _scroll) is { } origin
            ? Visible(origin.X, _scroll.Viewport.Width, width)
            : Visible(0, width, width);

    /// <summary>Columns <c>[from, to)</c> of a clip <paramref name="width"/> pixels wide whose left edge is at
    /// <paramref name="originX"/> in a viewport <paramref name="viewportWidth"/> pixels wide.</summary>
    internal static (int From, int To) Visible(double originX, double viewportWidth, double width)
    {
        var from = Math.Max(0, -originX);
        var to = Math.Min(width, viewportWidth - originX);
        return ((int)Math.Floor(from), (int)Math.Ceiling(Math.Max(from, to)));
    }
}
