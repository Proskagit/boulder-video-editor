using AiVideoEditor.UI.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// D064 §2: draws a clip's volume line (<see cref="ClipVolumeShape"/>) — always as a thin line (the volume at a glance), and on
/// the selected clip brighter with its points and the two fade handles; the sound fades' areas shaded under a ramp. Only the
/// part inside the timeline's visible area is drawn (a clip can be millions of pixels wide), redrawn on scroll. Never hit-tested
/// itself: the timeline asks the shape (<see cref="ClipVolumeShape.HitTest"/>).
/// </summary>
public sealed class VolumeEnvelopeView : Control
{
    public static readonly StyledProperty<ClipVolumeShape?> ShapeProperty =
        AvaloniaProperty.Register<VolumeEnvelopeView, ClipVolumeShape?>(nameof(Shape));

    private static readonly IImmutableSolidColorBrush LineIdle = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD5, 0x4F));
    private static readonly IImmutableSolidColorBrush LineActive = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly IImmutableSolidColorBrush LineMuted = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xB0, 0xB0, 0xB0));
    private static readonly IImmutableSolidColorBrush Shade = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00));
    private static readonly IImmutableSolidColorBrush PointFill = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly IImmutableSolidColorBrush PointSelected = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IImmutableSolidColorBrush Outline = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly IImmutableSolidColorBrush Handle = new ImmutableSolidColorBrush(Colors.White);

    private ScrollViewer? _scroll;

    static VolumeEnvelopeView()
    {
        AffectsRender<VolumeEnvelopeView>(ShapeProperty);
    }

    public VolumeEnvelopeView() => IsHitTestVisible = false;

    public ClipVolumeShape? Shape
    {
        get => GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
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
        if (Shape is not { } shape || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var height = Bounds.Height;
        var width = Math.Min(Bounds.Width, shape.Width);
        var (from, to) = _scroll is not null && this.TranslatePoint(default, _scroll) is { } origin
            ? WaveformView.Visible(origin.X, _scroll.Viewport.Width, width)
            : WaveformView.Visible(0, width, width);
        if (from >= to) return;
        var (top, area) = shape.Area(height);

        // The sound fades: the area shaded, a ramp from silence to the line.
        if (shape.FadeInWidth > 0 && from < shape.FadeInWidth)
            DrawFade(context, shape, 0, Math.Min(shape.FadeInWidth, width), top, area, height, rising: true);
        if (shape.FadeOutWidth > 0 && to > width - shape.FadeOutWidth)
            DrawFade(context, shape, Math.Max(0, width - shape.FadeOutWidth), width, top, area, height, rising: false);

        // The line, sampled every 3 px (the curve is even in dB between points, so not straight in gain) and at every point.
        var xs = new SortedSet<double>();
        for (double x = from; x < to; x += 3) xs.Add(x);
        xs.Add(Math.Min(to, width));
        foreach (var (_, px) in shape.ShownPoints())
            if (px >= from && px <= to) xs.Add(px);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = true;
            foreach (var x in xs)
            {
                var p = new Point(x, shape.YOf(shape.GainAt(x), height));
                if (first) { g.BeginFigure(p, isFilled: false); first = false; }
                else g.LineTo(p);
            }
            g.EndFigure(isClosed: false);
        }
        var brush = shape.IsMuted ? LineMuted : shape.IsActive ? LineActive : LineIdle;
        context.DrawGeometry(null, new ImmutablePen(brush, shape.IsActive ? 1.6 : 1), geometry);

        if (!shape.IsActive) return;
        var radius = height < 40 ? 3 : 3.5;
        foreach (var (point, px) in shape.ShownPoints())
        {
            if (px < from - radius || px > to + radius) continue;
            var y = shape.YOf(shape.Volume * point.Gain, height);
            context.DrawEllipse(point.Id == shape.SelectedPoint ? PointSelected : PointFill, new ImmutablePen(Outline, 1), new Point(px, y), radius, radius);
        }
        // The fade handles at the top corners (at the fades' ends).
        DrawHandle(context, Math.Clamp(shape.FadeInWidth, 0, width), top + 3);
        DrawHandle(context, Math.Clamp(width - shape.FadeOutWidth, 0, width), top + 3);
    }

    private static void DrawFade(DrawingContext context, ClipVolumeShape shape, double x0, double x1, double top, double area, double height, bool rising)
    {
        var bottom = top + area;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x0, top), isFilled: true);
            g.LineTo(new Point(x1, top));
            if (rising)
            {
                g.LineTo(new Point(x1, shape.YOf(shape.GainAt(x1), height)));
                g.LineTo(new Point(x0, bottom));
            }
            else
            {
                g.LineTo(new Point(x1, bottom));
                g.LineTo(new Point(x0, shape.YOf(shape.GainAt(x0), height)));
            }
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(Shade, null, geometry);
        context.DrawLine(new ImmutablePen(LineIdle, 1),
            rising ? new Point(x0, bottom) : new Point(x0, shape.YOf(shape.GainAt(x0), height)),
            rising ? new Point(x1, shape.YOf(shape.GainAt(x1), height)) : new Point(x1, bottom));
    }

    private static void DrawHandle(DrawingContext context, double x, double y) =>
        context.DrawRectangle(Handle, new ImmutablePen(Outline, 1), new Rect(x - 3.5, y - 3.5, 7, 7), 1, 1);
}
