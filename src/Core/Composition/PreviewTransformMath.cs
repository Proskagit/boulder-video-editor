using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>The edges a crop handle moves (D038 §3): one edge for an edge handle, two for a corner. Also names the
/// corners of a frame (Left | Top = the top-left corner).</summary>
[Flags]
public enum CropEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

/// <summary>
/// Where a clip lies on the canvas for the Preview's tools (D038 §2): <see cref="LocalRect"/> in the layer's local units
/// mapped onto the canvas by <see cref="Transform"/> — the crop-local rectangle of a picture (D018) or the text box of a
/// text clip (centred on its local origin). A rotated clip gives a rotated rectangle.
/// </summary>
public readonly record struct TransformFrame(Affine2D Transform, RectD LocalRect)
{
    /// <summary>The frame's handle corners and edge midpoints, top-left first, clockwise.</summary>
    public static readonly CropEdges[] Handles =
    {
        CropEdges.Left | CropEdges.Top, CropEdges.Top, CropEdges.Right | CropEdges.Top, CropEdges.Right,
        CropEdges.Right | CropEdges.Bottom, CropEdges.Bottom, CropEdges.Left | CropEdges.Bottom, CropEdges.Left
    };

    /// <summary>The four corners, top-left first, clockwise.</summary>
    public static readonly CropEdges[] Corners =
    {
        CropEdges.Left | CropEdges.Top, CropEdges.Right | CropEdges.Top, CropEdges.Right | CropEdges.Bottom, CropEdges.Left | CropEdges.Bottom
    };

    public PointD Center => Transform.Apply(new PointD(LocalRect.X + LocalRect.Width / 2, LocalRect.Y + LocalRect.Height / 2));

    /// <summary>The canvas point of a handle: a corner for two edges, an edge's midpoint for one.</summary>
    public PointD HandlePoint(CropEdges handle)
    {
        var x = handle.HasFlag(CropEdges.Left) ? LocalRect.X : handle.HasFlag(CropEdges.Right) ? LocalRect.Right : LocalRect.X + LocalRect.Width / 2;
        var y = handle.HasFlag(CropEdges.Top) ? LocalRect.Y : handle.HasFlag(CropEdges.Bottom) ? LocalRect.Bottom : LocalRect.Y + LocalRect.Height / 2;
        return Transform.Apply(new PointD(x, y));
    }

    /// <summary>Whether the canvas point lies inside the (possibly rotated) frame, edges included.</summary>
    public bool Contains(PointD canvasPoint)
    {
        if (Math.Abs(Transform.Determinant) < 1e-12 || !double.IsFinite(Transform.Determinant)) return false;
        var local = Transform.Invert().Apply(canvasPoint);
        return local.X >= LocalRect.X && local.X <= LocalRect.Right && local.Y >= LocalRect.Y && local.Y <= LocalRect.Bottom;
    }
}

/// <summary>
/// The pure geometry of the Preview's Move, Scale and Crop gestures (D038 §3). Every result is an ordinary
/// <see cref="VisualProperties"/> value that <see cref="ClipPropertyValidator"/> accepts; the composition (D018) is not
/// changed. All points are canvas pixels unless named source points (pixels of the source's display size).
/// </summary>
public static class PreviewTransformMath
{
    /// <summary>Scale results are rounded to 0.01 % — the precision the Inspector shows.</summary>
    public const int ScaleDecimals = 4;

    /// <summary>The least part of the source an anchored crop leaves on each axis: this fraction of the side…</summary>
    public const double MinCropFraction = 0.01;

    /// <summary>…and at least this many source pixels.</summary>
    public const double MinCropPixels = 4;

    /// <summary>The visible picture of a laid-out picture layer.</summary>
    public static TransformFrame PictureFrame(LayerGeometry geometry) =>
        new(geometry.Transform, new RectD(0, 0, geometry.SourceRect.Width, geometry.SourceRect.Height));

    /// <summary>The whole source frame (also its cropped-away part) where the layer would show it.</summary>
    public static TransformFrame SourceFrame(LayerGeometry geometry) =>
        new(SourceToCanvas(geometry), new RectD(0, 0, geometry.Source.Width, geometry.Source.Height));

    /// <summary>A text clip's box of <paramref name="boxWidth"/> × <paramref name="boxHeight"/> (its layout at its font
    /// size, centred on the local origin) through the text transform (D018).</summary>
    public static TransformFrame TextFrame(FrameSize canvas, VisualProperties visual, double boxWidth, double boxHeight) =>
        new(CompositionMath.TextTransform(canvas, visual), new RectD(-boxWidth / 2, -boxHeight / 2, boxWidth, boxHeight));

    /// <summary>Source pixels (of the whole source) → canvas, for the layer's current crop.</summary>
    public static Affine2D SourceToCanvas(LayerGeometry geometry) =>
        geometry.Transform.Compose(Affine2D.Translation(-geometry.SourceRect.X, -geometry.SourceRect.Y));

    /// <summary>
    /// Move (D038 §3): the position moved by the canvas delta rounded to whole canvas pixels (away from zero at .5), the
    /// fraction of the starting position kept; <paramref name="keepAxis"/> (Shift) keeps only the axis with the larger
    /// delta. Clamped to the position limit.
    /// </summary>
    public static VisualProperties Move(VisualProperties start, double dx, double dy, bool keepAxis)
    {
        if (keepAxis)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        var rx = Math.Round(dx, MidpointRounding.AwayFromZero);
        var ry = Math.Round(dy, MidpointRounding.AwayFromZero);
        return start with
        {
            PositionX = rx == 0 ? start.PositionX : ClampPosition(start.PositionX + rx),
            PositionY = ry == 0 ? start.PositionY : ClampPosition(start.PositionY + ry)
        };
    }

    /// <summary>
    /// Scale from a corner (D038 §3): proportional around <paramref name="center"/> (the clip's centre; the position never
    /// changes). The factor is the projection of <paramref name="corner"/> (the grabbed corner's new place) on the
    /// centre → <paramref name="startCorner"/> diagonal, divided by the diagonal's length; the result is rounded to
    /// 0.01 % and clamped to the scale limits; a corner at or past the centre gives the minimum.
    /// </summary>
    public static VisualProperties ScaleFromCorner(VisualProperties start, PointD center, PointD startCorner, PointD corner)
    {
        var ux = startCorner.X - center.X;
        var uy = startCorner.Y - center.Y;
        var length2 = ux * ux + uy * uy;
        if (!(length2 > 0) || !double.IsFinite(length2)) return start;

        var t = ((corner.X - center.X) * ux + (corner.Y - center.Y) * uy) / length2;
        if (t == 1) return start;   // not moved along the diagonal (also an exact return to the start)
        var scale = t > 0 ? Math.Round(start.Scale * t, ScaleDecimals, MidpointRounding.AwayFromZero) : ClipPropertyLimits.MinScale;
        if (!double.IsFinite(scale)) return start;
        scale = Math.Clamp(scale, ClipPropertyLimits.MinScale, ClipPropertyLimits.MaxScale);
        return start with { Scale = scale };
    }

    /// <summary>
    /// The anchored crop (D038 §3): the <paramref name="edges"/> dragged to <paramref name="sourcePoint"/> (pixels of the
    /// source <paramref name="source"/>; only the coordinate of each dragged edge's axis is used), rounded to whole source
    /// pixels; the edges not dragged keep their exact values. The scale and position compensate so the picture that stays
    /// visible keeps its place and size on the canvas: <c>Scale′ = k / fit′</c> with <c>k = fit · Scale</c> at the start,
    /// the new crop's centre where the source showed it; both unrounded. A dragged edge stops at the source's edge, at
    /// the minimum size (<see cref="MinCropFraction"/>, <see cref="MinCropPixels"/>) and where the compensating scale would
    /// leave the scale limits. A result the validator would reject — or an unchanged crop — returns
    /// <paramref name="start"/> itself.
    /// </summary>
    public static VisualProperties AnchoredCrop(FrameSize canvas, FrameSize source, VisualProperties start, CropEdges edges, PointD sourcePoint)
    {
        if (!canvas.IsValid || !source.IsValid || edges == CropEdges.None) return start;
        if (ClipPropertyValidator.ValidateVisual(start, allowCrop: true) is not null) return start;
        if (!double.IsFinite(sourcePoint.X) || !double.IsFinite(sourcePoint.Y)) return start;

        var g0 = CompositionMath.Layout(canvas, source, start);
        double w = source.Width, h = source.Height;
        double cw = canvas.Width, ch = canvas.Height;
        var k = g0.FitScale * g0.Scale;   // canvas pixels per source pixel, kept by the anchoring

        // The edges in source pixels.
        var crop = start.Crop;
        double l = crop.Left * w, t = crop.Top * h, r = w - crop.Right * w, b = h - crop.Bottom * h;

        var minW = Math.Min(w, Math.Max(MinCropPixels, Math.Ceiling(w * MinCropFraction)));
        var minH = Math.Min(h, Math.Max(MinCropPixels, Math.Ceiling(h * MinCropFraction)));
        // Scale′ = k / fit′ = k · max(cropW / cw, cropH / ch) ≤ MaxScale ⟺ cropW ≤ MaxScale · cw / k and cropH ≤ MaxScale · ch / k.
        var maxW = ClipPropertyLimits.MaxScale * cw / k;
        var maxH = ClipPropertyLimits.MaxScale * ch / k;

        if (edges.HasFlag(CropEdges.Left)) l = Place(sourcePoint.X, Math.Max(0, Math.Ceiling(r - maxW)), Math.Floor(r - minW), l);
        else if (edges.HasFlag(CropEdges.Right)) r = Place(sourcePoint.X, Math.Ceiling(l + minW), Math.Min(w, Math.Floor(l + maxW)), r);
        if (edges.HasFlag(CropEdges.Top)) t = Place(sourcePoint.Y, Math.Max(0, Math.Ceiling(b - maxH)), Math.Floor(b - minH), t);
        else if (edges.HasFlag(CropEdges.Bottom)) b = Place(sourcePoint.Y, Math.Ceiling(t + minH), Math.Min(h, Math.Floor(t + maxH)), b);

        // Scale′ ≥ MinScale ⟺ max(cropW / cw, cropH / ch) ≥ MinScale / k: when the drag went below, the dragged edge (the
        // horizontal one first) stops where the scale reaches the minimum.
        bool TooSmall() => k * Math.Max((r - l) / cw, (b - t) / ch) < ClipPropertyLimits.MinScale;
        if (TooSmall() && (edges & (CropEdges.Left | CropEdges.Right)) != 0)
        {
            var needW = ClipPropertyLimits.MinScale * cw / k;
            if (edges.HasFlag(CropEdges.Left)) l = Math.Max(0, Math.Min(l, Math.Floor(r - needW)));
            else r = Math.Min(w, Math.Max(r, Math.Ceiling(l + needW)));
        }
        if (TooSmall() && (edges & (CropEdges.Top | CropEdges.Bottom)) != 0)
        {
            var needH = ClipPropertyLimits.MinScale * ch / k;
            if (edges.HasFlag(CropEdges.Top)) t = Math.Max(0, Math.Min(t, Math.Floor(b - needH)));
            else b = Math.Min(h, Math.Max(b, Math.Ceiling(t + needH)));
        }
        if (TooSmall()) return start;

        var newCrop = new CropRect(
            edges.HasFlag(CropEdges.Left) ? l / w : crop.Left,
            edges.HasFlag(CropEdges.Top) ? t / h : crop.Top,
            edges.HasFlag(CropEdges.Right) ? (w - r) / w : crop.Right,
            edges.HasFlag(CropEdges.Bottom) ? (h - b) / h : crop.Bottom);
        if (newCrop == crop) return start;

        var cropped = start with { Crop = newCrop };
        if (ClipPropertyValidator.ValidateVisual(cropped, allowCrop: true) is not null) return start;

        var fit = CompositionMath.Layout(canvas, source, cropped with { Scale = 1, PositionX = 0, PositionY = 0 }).FitScale;
        var scale = Math.Clamp(k / fit, ClipPropertyLimits.MinScale, ClipPropertyLimits.MaxScale);

        // The new crop's centre (source pixels) stays where the source showed it.
        var center = SourceToCanvas(g0).Apply(new PointD((l + r) / 2, (t + b) / 2));
        var result = cropped with { Scale = scale, PositionX = center.X - cw / 2, PositionY = center.Y - ch / 2 };
        return ClipPropertyValidator.ValidateVisual(result, allowCrop: true) is null ? result : start;
    }

    /// <summary>The pointer's coordinate rounded to a whole pixel within [lo, hi]; the current value when the range is
    /// empty (the start already outside the constraints).</summary>
    private static double Place(double pointer, double lo, double hi, double current) =>
        lo <= hi ? Math.Clamp(Math.Round(pointer, MidpointRounding.AwayFromZero), lo, hi) : current;

    private static double ClampPosition(double value) =>
        Math.Clamp(value, -ClipPropertyLimits.MaxPositionMagnitude, ClipPropertyLimits.MaxPositionMagnitude);
}
