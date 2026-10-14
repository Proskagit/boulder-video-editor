using System.Collections.Immutable;
using System.ComponentModel;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The markup mode's layer over the Preview's <see cref="CompositionView"/> (D034 §1): turns a click into a point of the
/// clip's source frame through the same viewport and layer geometry the composition uses (D018), draws the candidate and
/// the selected hold, and a loupe of the full-size markup frame under the pointer. Hit-testable only while the mode is
/// on; the confirmed outlines themselves are the composition's (Preview = export).
/// </summary>
public sealed class HoldMarkupOverlay : Control, ICustomHitTest
{
    public const double LoupeSize = 140;
    public const double LoupeZoom = 3;

    public static readonly StyledProperty<HoldMarkupViewModel?> MarkupProperty =
        AvaloniaProperty.Register<HoldMarkupOverlay, HoldMarkupViewModel?>(nameof(Markup));

    private static readonly IPen CandidateKeyline = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 4, lineJoin: PenLineJoin.Round);
    private static readonly IPen CandidatePen = new Pen(new SolidColorBrush(Color.Parse("#00E5FF")), 2, lineJoin: PenLineJoin.Round);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#FFEA00")), 2, new DashStyle(new double[] { 3, 2 }, 0), lineJoin: PenLineJoin.Round);
    private static readonly IPen LoupeBorder = new Pen(Brushes.White, 1);
    private static readonly IPen Crosshair = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);

    private HoldMarkupViewModel? _markup;
    private Point? _pointer;
    private WriteableBitmap? _loupeBitmap;
    private DecodedFrame? _loupeFrame;

    static HoldMarkupOverlay()
    {
        MarkupProperty.Changed.AddClassHandler<HoldMarkupOverlay>((o, e) => o.Attach(e.NewValue as HoldMarkupViewModel));
        FocusableProperty.OverrideDefaultValue<HoldMarkupOverlay>(true);   // a click takes the focus off text inputs
    }

    public HoldMarkupViewModel? Markup
    {
        get => GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    public bool HitTest(Point point) => _markup is { IsActive: true } && new Rect(Bounds.Size).Contains(point);

    private void Attach(HoldMarkupViewModel? markup)
    {
        if (_markup is not null) _markup.PropertyChanged -= OnMarkupChanged;
        _markup = markup;
        if (_markup is not null) _markup.PropertyChanged += OnMarkupChanged;
        InvalidateVisual();
    }

    private void OnMarkupChanged(object? sender, PropertyChangedEventArgs e)
    {
        Cursor = _markup is { IsActive: true, IsPreparing: false } ? new Cursor(StandardCursorType.Cross) : null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loupeBitmap?.Dispose();
        _loupeBitmap = null;
        _loupeFrame = null;
    }

    /// <summary>The view's point <paramref name="p"/> as a normalized point of the clip's source frame, or null when it
    /// is outside the visible (cropped) picture.</summary>
    public static NormalizedPoint? ToSource(LayerGeometry geometry, FrameSize canvas, Size view, Point p)
    {
        if (!canvas.IsValid || !(view.Width > 0) || !(view.Height > 0)) return null;
        var toView = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).Compose(geometry.Transform);
        if (Math.Abs(toView.Determinant) < 1e-12) return null;
        var local = toView.Invert().Apply(new PointD(p.X, p.Y));
        if (local.X < 0 || local.Y < 0 || local.X > geometry.SourceRect.Width || local.Y > geometry.SourceRect.Height) return null;
        return new NormalizedPoint((local.X + geometry.SourceRect.X) / geometry.Source.Width,
            (local.Y + geometry.SourceRect.Y) / geometry.Source.Height);
    }

    /// <summary>The normalized source point <paramref name="n"/> in the view.</summary>
    public static Point ToView(LayerGeometry geometry, FrameSize canvas, Size view, NormalizedPoint n)
    {
        var toView = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).Compose(geometry.Transform);
        var p = toView.Apply(new PointD(n.X * geometry.Source.Width - geometry.SourceRect.X, n.Y * geometry.Source.Height - geometry.SourceRect.Y));
        return new Point(p.X, p.Y);
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_markup is not { IsActive: true, Geometry: { } geometry } markup) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Focus();
        if (ToSource(geometry, markup.Canvas, Bounds.Size, point.Position) is { } source)
            await markup.OnClickAsync(source, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        if (_markup is { IsActive: true }) InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_markup is not { IsActive: true }) return;
        _markup.OnWheel(e.Delta.Y);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        if (_markup is not { IsActive: true, Geometry: { } geometry } markup || !markup.Canvas.IsValid) return;
        var view = Bounds.Size;

        DrawContour(context, geometry, markup.Canvas, view, markup.SelectedContour, null, SelectedPen);
        DrawContour(context, geometry, markup.Canvas, view, markup.CandidateContour, CandidateKeyline, CandidatePen);

        if (markup.IsLoupeOn && !markup.IsPreparing && _pointer is { } pointer && markup.MarkupFrame is { } frame &&
            ToSource(geometry, markup.Canvas, view, pointer) is { } source)
            DrawLoupe(context, geometry, markup.Canvas, view, frame, pointer, source);
    }

    private static void DrawContour(DrawingContext context, LayerGeometry geometry, FrameSize canvas, Size view,
        ImmutableArray<NormalizedPoint> contour, IPen? under, IPen pen)
    {
        if (contour.IsDefaultOrEmpty || contour.Length < 3) return;
        var stream = new StreamGeometry();
        using (var g = stream.Open())
        {
            g.BeginFigure(ToView(geometry, canvas, view, contour[0]), false);
            for (var i = 1; i < contour.Length; i++) g.LineTo(ToView(geometry, canvas, view, contour[i]));
            g.EndFigure(true);
        }
        if (under is not null) context.DrawGeometry(null, under, stream);
        context.DrawGeometry(null, pen, stream);
    }

    /// <summary>A square of the full-size frame around the pointer, <see cref="LoupeZoom"/> × the Preview's scale, beside
    /// the pointer (kept inside the view), with a crosshair on the point a click would take.</summary>
    private void DrawLoupe(DrawingContext context, LayerGeometry geometry, FrameSize canvas, Size view, DecodedFrame frame,
        Point pointer, NormalizedPoint source)
    {
        if (!ReferenceEquals(frame, _loupeFrame))
        {
            _loupeBitmap = FrameBitmap.Ensure(_loupeBitmap, frame);
            FrameBitmap.Copy(frame, _loupeBitmap);
            _loupeFrame = frame;
        }
        if (_loupeBitmap is null) return;

        var viewScale = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).A * geometry.FitScale * geometry.Scale
                        * frame.Width / geometry.Source.Width;
        var span = LoupeSize / Math.Max(viewScale * LoupeZoom, 1e-6);   // frame pixels across the loupe
        var cx = source.X * frame.Width;
        var cy = source.Y * frame.Height;
        var sourceRect = new Rect(cx - span / 2, cy - span / 2, span, span);

        var x = pointer.X + 24;
        var y = pointer.Y + 24;
        if (x + LoupeSize > view.Width) x = pointer.X - 24 - LoupeSize;
        if (y + LoupeSize > view.Height) y = pointer.Y - 24 - LoupeSize;
        var target = new Rect(Math.Max(0, x), Math.Max(0, y), LoupeSize, LoupeSize);

        context.FillRectangle(Brushes.Black, target);
        using (context.PushClip(target))
            context.DrawImage(_loupeBitmap, sourceRect, target);
        context.DrawRectangle(null, LoupeBorder, target);
        var c = target.Center;
        context.DrawLine(Crosshair, new Point(c.X - 8, c.Y), new Point(c.X + 8, c.Y));
        context.DrawLine(Crosshair, new Point(c.X, c.Y - 8), new Point(c.X, c.Y + 8));
    }
}
