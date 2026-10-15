using System.Collections.Immutable;
using System.ComponentModel;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
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
/// the selected hold, and a loupe of the full-size markup frame under the pointer. The selected hold's handles (D035) are
/// grabbed here: a drag moves one, Alt+click on the outline inserts a vertex, a double-click on a handle removes it — the
/// view-model applies each gesture as one step. Hit-testable only while the mode is on; the confirmed outlines themselves
/// are the composition's (Preview = export), the handles exist only in this overlay and never reach the export.
/// </summary>
public sealed class HoldMarkupOverlay : Control, ICustomHitTest
{
    public const double LoupeSize = 140;
    public const double LoupeZoom = 3;

    /// <summary>Handle squares and the pointer's reach for a handle or (Alt+click) the outline, in view pixels.</summary>
    public const double HandleSize = 7;
    public const double HandleReach = 8;
    public const double EdgeReach = 6;

    /// <summary>The least spacing of handles on the screen (view pixels): a hold small in the Preview shows fewer.</summary>
    public const double HandleSpacing = 14;

    /// <summary>A press on a handle becomes a drag once the pointer has moved this far (view pixels).</summary>
    public const double DragThreshold = 3;

    public static readonly StyledProperty<HoldMarkupViewModel?> MarkupProperty =
        AvaloniaProperty.Register<HoldMarkupOverlay, HoldMarkupViewModel?>(nameof(Markup));

    private static readonly IPen CandidateKeyline = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 4, lineJoin: PenLineJoin.Round);
    private static readonly IPen CandidatePen = new Pen(new SolidColorBrush(Color.Parse("#00E5FF")), 2, lineJoin: PenLineJoin.Round);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#FFEA00")), 2, new DashStyle(new double[] { 3, 2 }, 0), lineJoin: PenLineJoin.Round);
    private static readonly IPen LoupeBorder = new Pen(Brushes.White, 1);
    private static readonly IPen Crosshair = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);
    private static readonly IPen InvalidPen = new Pen(new SolidColorBrush(Color.Parse("#FF1744")), 2, lineJoin: PenLineJoin.Round);
    private static readonly IPen HandleBorder = new Pen(Brushes.Black, 1);
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush HoveredFill = new SolidColorBrush(Color.Parse("#FFEA00"));

    private HoldMarkupViewModel? _markup;
    private Point? _pointer;
    private WriteableBitmap? _loupeBitmap;
    private DecodedFrame? _loupeFrame;

    // A press on a handle: which one, where, and whether it became a drag.
    private int _pressedHandle = -1;
    private Point _pressedAt;
    private bool _dragging;

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
        if (e.PropertyName is nameof(HoldMarkupViewModel.Geometry) or nameof(HoldMarkupViewModel.MarkupFrame) or nameof(HoldMarkupViewModel.Canvas))
            UpdateHandleSpacing();
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateHandleSpacing();
    }

    /// <summary>Tells the view-model how many frame pixels <see cref="HandleSpacing"/> view pixels are now.</summary>
    private void UpdateHandleSpacing()
    {
        if (_markup is not { Geometry: { } geometry, MarkupFrame: { } frame } markup || !markup.Canvas.IsValid) return;
        var viewPerSource = CompositionDrawPlan.Viewport(markup.Canvas, Bounds.Width, Bounds.Height).A * geometry.FitScale * geometry.Scale;
        var viewPerFrame = viewPerSource * geometry.Source.Width / frame.Width;
        if (viewPerFrame > 0) markup.SetHandleSpacing(HandleSpacing / viewPerFrame);
    }

    private void UpdateCursor() =>
        Cursor = _markup is not { IsActive: true, IsPreparing: false } markup ? null
            : new Cursor(markup.HoveredHandle is not null || _dragging ? StandardCursorType.SizeAll : StandardCursorType.Cross);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loupeBitmap?.Dispose();
        _loupeBitmap = null;
        _loupeFrame = null;
    }

    /// <summary>The view's point <paramref name="p"/> as a normalized point of the clip's source frame, or null when it
    /// is outside the visible (cropped) picture.</summary>
    public static NormalizedPoint? ToSource(LayerGeometry geometry, FrameSize canvas, Size view, Point p) =>
        ToSourceUnbounded(geometry, canvas, view, p, out var inside) is { } source && inside ? source : null;

    /// <summary>The view's point <paramref name="p"/> as a normalized point of the clip's source frame wherever it is
    /// (a dragged handle may leave the picture; the edit clamps it to the frame); <paramref name="inside"/>: within the
    /// visible (cropped) picture. Null only for a degenerate view or transform.</summary>
    public static NormalizedPoint? ToSourceUnbounded(LayerGeometry geometry, FrameSize canvas, Size view, Point p, out bool inside)
    {
        inside = false;
        if (!canvas.IsValid || !(view.Width > 0) || !(view.Height > 0)) return null;
        var toView = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).Compose(geometry.Transform);
        if (Math.Abs(toView.Determinant) < 1e-12) return null;
        var local = toView.Invert().Apply(new PointD(p.X, p.Y));
        inside = !(local.X < 0 || local.Y < 0 || local.X > geometry.SourceRect.Width || local.Y > geometry.SourceRect.Height);
        return new NormalizedPoint((local.X + geometry.SourceRect.X) / geometry.Source.Width,
            (local.Y + geometry.SourceRect.Y) / geometry.Source.Height);
    }

    /// <summary>The handle of the selected contour within <see cref="HandleReach"/> of <paramref name="p"/> (the nearest),
    /// as an index into <see cref="HoldMarkupViewModel.SelectedHandles"/>.</summary>
    public static int? HandleAt(LayerGeometry geometry, FrameSize canvas, Size view, ImmutableArray<NormalizedPoint> contour,
        ImmutableArray<int> handles, Point p)
    {
        int? best = null;
        var bestDistance = HandleReach;
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i] >= contour.Length) continue;
            var v = ToView(geometry, canvas, view, contour[handles[i]]);
            var d = Math.Max(Math.Abs(v.X - p.X), Math.Abs(v.Y - p.Y));
            if (d <= bestDistance) (best, bestDistance) = (i, d);
        }
        return best;
    }

    /// <summary>The edge of <paramref name="contour"/> within <see cref="EdgeReach"/> of <paramref name="p"/> and the
    /// parameter along it (measured in the view; an affine map keeps it in the source).</summary>
    public static (int Edge, double T)? EdgeAt(LayerGeometry geometry, FrameSize canvas, Size view, ImmutableArray<NormalizedPoint> contour, Point p)
    {
        if (contour.Length < 3) return null;
        var points = contour.Select(n => ToView(geometry, canvas, view, n)).Select(v => (v.X, v.Y)).ToList();
        var (edge, t, distance) = ContourEditing.NearestEdge(points, (p.X, p.Y));
        return edge >= 0 && distance <= EdgeReach ? (edge, t) : null;
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

        if (markup.CanEditContour)
        {
            // Alt+click is for the outline only (it wins over a handle near by) and never starts a candidate.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                if (EdgeAt(geometry, markup.Canvas, Bounds.Size, markup.SelectedContour, point.Position) is { } edge)
                    markup.InsertVertex(edge.Edge, edge.T);
                else
                    markup.ReportOffOutline();
                return;
            }
            if (HandleAt(geometry, markup.Canvas, Bounds.Size, markup.SelectedContour, markup.SelectedHandles, point.Position) is { } handle)
            {
                if (e.ClickCount >= 2)
                {
                    markup.DeleteVertex(handle);
                    return;
                }
                _pressedHandle = handle;
                _pressedAt = point.Position;
                _dragging = false;
                e.Pointer.Capture(this);
                return;
            }
        }
        if (ToSource(geometry, markup.Canvas, Bounds.Size, point.Position) is { } source)
            await markup.OnClickAsync(source, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_markup is not { IsActive: true } markup) return;

        if (_pressedHandle >= 0 && markup.Geometry is { } geometry)
        {
            if (!_dragging && Math.Max(Math.Abs(position.X - _pressedAt.X), Math.Abs(position.Y - _pressedAt.Y)) >= DragThreshold)
                _dragging = markup.BeginVertexDrag(_pressedHandle);
            if (_dragging && ToSourceUnbounded(geometry, markup.Canvas, Bounds.Size, position, out _) is { } source)
                markup.DragVertex(source);
        }
        else
        {
            markup.HoveredHandle = markup.CanEditContour && markup.Geometry is { } g
                ? HandleAt(g, markup.Canvas, Bounds.Size, markup.SelectedContour, markup.SelectedHandles, position)
                : null;
        }
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedHandle < 0) return;
        var dragging = _dragging;
        EndPress(e.Pointer);
        if (dragging) _markup?.EndVertexDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressedHandle < 0) return;
        var dragging = _dragging;
        EndPress(null);
        if (dragging) _markup?.CancelVertexDrag();
    }

    private void EndPress(IPointer? pointer)
    {
        _pressedHandle = -1;
        _dragging = false;
        pointer?.Capture(null);   // raises the capture-lost handler with the press already ended
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        if (_markup is not null && _pressedHandle < 0) _markup.HoveredHandle = null;
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

        DrawContour(context, geometry, markup.Canvas, view, markup.SelectedContour, null, markup.IsContourInvalid ? InvalidPen : SelectedPen);
        if (markup.CanEditContour || markup.IsDraggingVertex) DrawHandles(context, geometry, markup, view);
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

    /// <summary>The selected contour's handles: white squares, the one under the pointer (or being dragged) yellow.</summary>
    private void DrawHandles(DrawingContext context, LayerGeometry geometry, HoldMarkupViewModel markup, Size view)
    {
        var (contour, handles) = (markup.SelectedContour, markup.SelectedHandles);
        var active = _pressedHandle >= 0 ? _pressedHandle : markup.HoveredHandle ?? -1;
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i] >= contour.Length) continue;
            var v = ToView(geometry, markup.Canvas, view, contour[handles[i]]);
            var size = i == active ? HandleSize + 2 : HandleSize;
            var rect = new Rect(v.X - size / 2, v.Y - size / 2, size, size);
            context.FillRectangle(i == active ? HoveredFill : HandleFill, rect);
            context.DrawRectangle(null, HandleBorder, rect);
        }
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
