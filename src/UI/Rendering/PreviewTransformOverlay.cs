using System.Collections.Immutable;
using System.ComponentModel;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The Move / Crop tools' layer over the Preview's <see cref="CompositionView"/> (D038): maps the pointer to canvas pixels
/// through the same "contain" viewport the composition uses (<see cref="CompositionDrawPlan.Viewport"/>, frozen while a
/// gesture runs), draws the target's frame — with the four corner handles in Move, the eight crop handles and the whole
/// source's outline in Crop — and turns presses into the view-model's gestures: a corner handle scales, the inside moves,
/// a crop handle crops, a press on another layer's picture selects that clip (in Move the same press then moves it). A
/// press becomes a gesture after <see cref="DragThreshold"/> view pixels; the release ends it, a lost capture cancels it.
/// Hit-testable only while a tool is on; nothing drawn here reaches the composition or the export.
/// </summary>
public sealed class PreviewTransformOverlay : Control, ICustomHitTest
{
    /// <summary>Handle squares and the pointer's reach for one, in view pixels.</summary>
    public const double HandleSize = 8;
    public const double HandleReach = 8;

    /// <summary>A press becomes a gesture once the pointer has moved this far (view pixels).</summary>
    public const double DragThreshold = 3;

    public static readonly StyledProperty<PreviewTransformViewModel?> TransformProperty =
        AvaloniaProperty.Register<PreviewTransformOverlay, PreviewTransformViewModel?>(nameof(Transform));

    public static readonly StyledProperty<ImmutableArray<LayerPicture>> LayersProperty =
        AvaloniaProperty.Register<PreviewTransformOverlay, ImmutableArray<LayerPicture>>(nameof(Layers), ImmutableArray<LayerPicture>.Empty);

    // Immutable pens and brushes: the static helpers below may run on any thread (tests); drawing is on the UI thread.
    private static readonly IPen Keyline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 3.5);
    private static readonly IPen FramePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#4FC3F7")), 1.5);
    private static readonly IPen LockedPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(220, 160, 160, 160)), 1.5,
        new ImmutableDashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen SourcePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1,
        new ImmutableDashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen HoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
    private static readonly IPen HandleBorder = new ImmutablePen(Brushes.Black, 1);
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush HoveredFill = new ImmutableSolidColorBrush(Color.Parse("#FFEA00"));

    private PreviewTransformViewModel? _transform;
    private Point? _pointer;

    // A press: what it grabbed, where, and the viewport at that moment (kept for the gesture).
    private Press? _press;

    private enum PressKind { Move, Scale, Crop, None }

    private sealed record Press(PressKind Kind, CropEdges Handle, Point At, Affine2D Viewport)
    {
        public bool Started { get; set; }
    }

    static PreviewTransformOverlay()
    {
        TransformProperty.Changed.AddClassHandler<PreviewTransformOverlay>((o, e) => o.Attach(e.NewValue as PreviewTransformViewModel));
        AffectsRender<PreviewTransformOverlay>(LayersProperty);
        FocusableProperty.OverrideDefaultValue<PreviewTransformOverlay>(true);   // a press takes the focus off text inputs
        // A frame scaled or moved past the canvas is drawn up to the Preview's edge, never over the panels around it.
        ClipToBoundsProperty.OverrideDefaultValue<PreviewTransformOverlay>(true);
    }

    public PreviewTransformViewModel? Transform
    {
        get => GetValue(TransformProperty);
        set => SetValue(TransformProperty, value);
    }

    public ImmutableArray<LayerPicture> Layers
    {
        get => GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    public bool HitTest(Point point) => _transform is { IsActive: true } && new Rect(Bounds.Size).Contains(point);

    private void Attach(PreviewTransformViewModel? transform)
    {
        if (_transform is not null) _transform.PropertyChanged -= OnTransformChanged;
        _transform = transform;
        if (_transform is not null) _transform.PropertyChanged += OnTransformChanged;
        InvalidateVisual();
    }

    private void OnTransformChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A gesture ended elsewhere (Esc, playback, a save …): the press is over too.
        if (e.PropertyName == nameof(PreviewTransformViewModel.IsGestureActive) && _transform is { IsGestureActive: false } && _press is { Started: true })
            _press = null;
        UpdateCursor();
        InvalidateVisual();
    }

    // ---- mapping ----------------------------------------------------------------------------------------------------

    /// <summary>The viewport canvas → view for the overlay's current size.</summary>
    public static Affine2D Viewport(FrameSize canvas, Size view) => CompositionDrawPlan.Viewport(canvas, view.Width, view.Height);

    /// <summary>The overlay reaches <see cref="Inset"/> view pixels past the picture's area on every side (its margin in
    /// the Preview), so the handles of a clip that fills the canvas are whole and can be grabbed at the letterbox edge;
    /// the canvas maps into the area inside that border exactly as the <see cref="CompositionView"/> maps it.</summary>
    public const double Inset = 16;

    /// <summary>The viewport canvas → overlay for an overlay of <paramref name="bounds"/> with the <see cref="Inset"/> border.</summary>
    public static Affine2D ViewportIn(FrameSize canvas, Size bounds) =>
        Affine2D.Translation(Inset, Inset).Compose(Viewport(canvas, new Size(Math.Max(0, bounds.Width - 2 * Inset), Math.Max(0, bounds.Height - 2 * Inset))));

    public static PointD ToCanvas(Affine2D viewport, Point p) => viewport.Invert().Apply(new PointD(p.X, p.Y));

    public static Point ToView(Affine2D viewport, PointD p)
    {
        var v = viewport.Apply(p);
        return new Point(v.X, v.Y);
    }

    /// <summary>The handle of <paramref name="frame"/> within <see cref="HandleReach"/> of <paramref name="p"/> (view), the
    /// nearest; corners only when <paramref name="cornersOnly"/>.</summary>
    public static CropEdges HandleAt(Affine2D viewport, TransformFrame frame, Point p, bool cornersOnly)
    {
        var best = CropEdges.None;
        var bestDistance = HandleReach;
        foreach (var handle in cornersOnly ? TransformFrame.Corners : TransformFrame.Handles)
        {
            var v = ToView(viewport, frame.HandlePoint(handle));
            var d = Math.Max(Math.Abs(v.X - p.X), Math.Abs(v.Y - p.Y));
            if (d <= bestDistance) (best, bestDistance) = (handle, d);
        }
        return best;
    }

    /// <summary>The topmost layer of <paramref name="layers"/> (bottom to top) whose picture or text box contains the
    /// canvas point; layers of unknown size are skipped (D038 §2).</summary>
    public static Guid? LayerAt(FrameSize canvas, IReadOnlyList<LayerPicture> layers, PointD canvasPoint)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
            if (FrameOf(canvas, layers[i]) is { } frame && frame.Contains(canvasPoint))
                return layers[i].Layer.ClipId;
        return null;
    }

    private static TransformFrame? FrameOf(FrameSize canvas, LayerPicture picture) => picture.Layer switch
    {
        PictureLayer { Geometry: { } geometry } => PreviewTransformMath.PictureFrame(geometry),
        TextLayer text => TextFrame(text),
        _ => null
    };

    private static TransformFrame TextFrame(TextLayer text)
    {
        var layout = CompositionPainter.Layout(text.Text);
        var (w, h) = (layout.MaxTextWidth, layout.Height);
        return new TransformFrame(text.Transform, new RectD(-w / 2, -h / 2, w, h));
    }

    // ---- input ------------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_transform is not { IsActive: true } t || !t.Canvas.IsValid) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Focus();
        if (!t.IsPaused || t.IsGestureActive) return;

        var viewport = ViewportIn(t.Canvas, Bounds.Size);
        var canvasPoint = ToCanvas(viewport, point.Position);
        var kind = PressKind.None;
        var handle = CropEdges.None;

        if (t.Target is { } target)
        {
            if (t.HasScaleHandles && HandleAt(viewport, target.Frame, point.Position, cornersOnly: true) is var corner and not CropEdges.None)
                (kind, handle) = (PressKind.Scale, corner);
            else if (t.HasCropHandles && HandleAt(viewport, target.Frame, point.Position, cornersOnly: false) is var crop and not CropEdges.None)
                (kind, handle) = (PressKind.Crop, crop);
            else if (target.Frame.Contains(canvasPoint))
            {
                if (target.IsLocked) { t.ReportLocked(); return; }
                kind = t.Tool == PreviewTool.Move ? PressKind.Move : PressKind.None;
            }
        }

        if (kind == PressKind.None && !(t.Target?.Frame.Contains(canvasPoint) ?? false))
        {
            // Another layer's picture: it becomes the selection; in Move the same press then moves it.
            var layers = Layers.IsDefault ? ImmutableArray<LayerPicture>.Empty : Layers;
            if (LayerAt(t.Canvas, layers, canvasPoint) is not { } id || !t.SelectLayer(id)) return;
            if (t.Target is { IsLocked: true }) { t.ReportLocked(); return; }
            if (t.Tool == PreviewTool.Move) kind = PressKind.Move;
        }

        if (kind == PressKind.None) return;
        _press = new Press(kind, handle, point.Position, viewport);
        e.Pointer.Capture(this);
        UpdateCursor();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_transform is not { IsActive: true } t) return;

        if (_press is { } press)
        {
            if (!press.Started && Math.Max(Math.Abs(position.X - press.At.X), Math.Abs(position.Y - press.At.Y)) >= DragThreshold)
            {
                var at = ToCanvas(press.Viewport, press.At);
                press.Started = press.Kind switch
                {
                    PressKind.Move => t.BeginMove(at),
                    PressKind.Scale => t.BeginScale(press.Handle, at),
                    PressKind.Crop => t.BeginCrop(press.Handle, at),
                    _ => false
                };
                if (!press.Started) { EndPress(e.Pointer); return; }
            }
            if (press.Started) t.UpdateGesture(ToCanvas(press.Viewport, position), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null) return;
        var started = _press.Started;
        EndPress(e.Pointer);
        if (started) _transform?.EndGesture();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_press is null) return;
        var started = _press.Started;
        EndPress(null);
        if (started) _transform?.CancelGesture();
    }

    private void EndPress(IPointer? pointer)
    {
        _press = null;
        pointer?.Capture(null);   // raises the capture-lost handler with the press already ended
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    private void UpdateCursor()
    {
        if (_transform is not { IsActive: true, IsPaused: true } t || _pointer is not { } p || !t.Canvas.IsValid)
        {
            Cursor = null;
            return;
        }
        var viewport = _press?.Viewport ?? ViewportIn(t.Canvas, Bounds.Size);
        CropEdges handle;
        if (_press is { Kind: PressKind.Scale or PressKind.Crop } press) handle = press.Handle;
        else if (_press is { Kind: PressKind.Move }) handle = CropEdges.None;
        else if (t.Target is { } target && t.HasScaleHandles) handle = HandleAt(viewport, target.Frame, p, cornersOnly: true);
        else if (t.Target is { } cropTarget && t.HasCropHandles) handle = HandleAt(viewport, cropTarget.Frame, p, cornersOnly: false);
        else handle = CropEdges.None;

        if (handle != CropEdges.None && t.Target is { } h)
        {
            Cursor = new Cursor(HandleCursor(viewport, h.Frame, handle));
            return;
        }
        var inside = _press is { Kind: PressKind.Move } ||
                     (t.Tool == PreviewTool.Move && t.Target is { IsLocked: false } m && m.Frame.Contains(ToCanvas(viewport, p)));
        Cursor = inside ? new Cursor(StandardCursorType.SizeAll) : null;
    }

    /// <summary>A resize cursor along the handle's direction from the frame's centre on the screen (rotation included).</summary>
    private static StandardCursorType HandleCursor(Affine2D viewport, TransformFrame frame, CropEdges handle)
    {
        var c = ToView(viewport, frame.Center);
        var h = ToView(viewport, frame.HandlePoint(handle));
        var angle = Math.Atan2(h.Y - c.Y, h.X - c.X) * 180 / Math.PI;   // 0 = right, 90 = down
        var octant = (int)Math.Round(((angle % 180) + 180) % 180 / 45) % 4;
        return octant switch
        {
            0 => StandardCursorType.SizeWestEast,
            1 => StandardCursorType.BottomRightCorner,
            2 => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.BottomLeftCorner
        };
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        if (_transform is not { IsActive: true, IsPaused: true } t || !t.Canvas.IsValid) return;
        var viewport = _press?.Viewport ?? ViewportIn(t.Canvas, Bounds.Size);

        // Another layer under the pointer: a thin outline (a click selects it).
        if (_press is null && _pointer is { } pointer && !Layers.IsDefault)
        {
            var at = ToCanvas(viewport, pointer);
            if (LayerAt(t.Canvas, Layers, at) is { } id && id != t.Target?.Clip.Id && !(t.Target?.Frame.Contains(at) ?? false) &&
                Layers.FirstOrDefault(l => l.Layer.ClipId == id) is { } hovered && FrameOf(t.Canvas, hovered) is { } hoverFrame)
                DrawFrame(context, viewport, hoverFrame, null, HoverPen);
        }

        if (t.Target is not { } target) return;
        if (t.HasCropHandles && target.Geometry is { } geometry)
            DrawFrame(context, viewport, PreviewTransformMath.SourceFrame(geometry), Keyline, SourcePen);
        DrawFrame(context, viewport, target.Frame, Keyline, target.IsLocked ? LockedPen : FramePen);
        DrawCentre(context, ToView(viewport, target.Frame.Center));

        var active = _press is { Kind: PressKind.Scale or PressKind.Crop } press ? press.Handle
            : _pointer is { } p && t.HasScaleHandles ? HandleAt(viewport, target.Frame, p, cornersOnly: true)
            : _pointer is { } q && t.HasCropHandles ? HandleAt(viewport, target.Frame, q, cornersOnly: false)
            : CropEdges.None;
        if (t.HasScaleHandles) DrawHandles(context, viewport, target.Frame, TransformFrame.Corners, active);
        else if (t.HasCropHandles) DrawHandles(context, viewport, target.Frame, TransformFrame.Handles, active);
    }

    private static void DrawFrame(DrawingContext context, Affine2D viewport, TransformFrame frame, IPen? under, IPen pen)
    {
        var stream = new StreamGeometry();
        using (var g = stream.Open())
        {
            var corners = TransformFrame.Corners.Select(c => ToView(viewport, frame.HandlePoint(c))).ToArray();
            g.BeginFigure(corners[0], false);
            for (var i = 1; i < corners.Length; i++) g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        if (under is not null) context.DrawGeometry(null, under, stream);
        context.DrawGeometry(null, pen, stream);
    }

    private static void DrawCentre(DrawingContext context, Point c)
    {
        context.DrawLine(Keyline, new Point(c.X - 6, c.Y), new Point(c.X + 6, c.Y));
        context.DrawLine(Keyline, new Point(c.X, c.Y - 6), new Point(c.X, c.Y + 6));
        context.DrawLine(FramePen, new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y));
        context.DrawLine(FramePen, new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5));
    }

    private static void DrawHandles(DrawingContext context, Affine2D viewport, TransformFrame frame, IEnumerable<CropEdges> handles, CropEdges active)
    {
        foreach (var handle in handles)
        {
            var v = ToView(viewport, frame.HandlePoint(handle));
            var size = handle == active ? HandleSize + 2 : HandleSize;
            var rect = new Rect(v.X - size / 2, v.Y - size / 2, size, size);
            context.FillRectangle(handle == active ? HoveredFill : HandleFill, rect);
            context.DrawRectangle(null, HandleBorder, rect);
        }
    }
}
