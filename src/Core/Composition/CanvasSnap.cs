using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>What a snap landed on: the canvas edges the picture's edges now lie on.</summary>
public readonly record struct CanvasSnapResult(VisualProperties Visual, CropEdges Edges)
{
    public bool Snapped => Edges != CropEdges.None;
}

/// <summary>
/// D063 §B: the magnetic snap of a picture's edges to the canvas edges in the Preview's Move and Scale gestures.
/// <list type="bullet">
/// <item><b>Threshold</b> in canvas pixels — the Preview converts its screen-pixel threshold by its current zoom, so the
/// feel does not depend on the source's resolution or the Preview's size.</item>
/// <item><b>Stable</b>: the snap is decided from the gesture's raw (unsnapped) result alone, never from the previous
/// snapped one — no jitter, and moving the pointer on beyond the threshold releases it.</item>
/// <item><b>Exact</b>: for a picture turned by a multiple of 90° the snapped value is computed in exact rational arithmetic
/// from the model's own values (the same as D018's coverage test) and rounded to the double on the covering side — the
/// picture's edge lies exactly on the canvas edge (to the last bit, never short of it), so no thin black line is left and
/// <see cref="LayerGeometry.CoversCanvas"/> holds where the picture covers the canvas. Other angles snap the bounding box
/// in doubles.</item>
/// <item><b>Move</b> snaps the position on each axis to the nearer edge within the threshold; <b>Scale</b> (around the
/// picture's centre, its aspect kept) snaps the scale so the edge nearest a canvas edge lies on it, within the scale's limits.</item>
/// </list>
/// </summary>
public static class CanvasSnap
{
    /// <summary>The default threshold in screen pixels.</summary>
    public const double DefaultThresholdPixels = 8;

    public const double MinThresholdPixels = 0;
    public const double MaxThresholdPixels = 40;

    /// <summary>The position snapped (Move): each axis whose nearer picture edge lies within <paramref name="threshold"/>
    /// canvas pixels of the canvas edge moves that edge exactly onto it.</summary>
    public static CanvasSnapResult Move(FrameSize canvas, FrameSize source, VisualProperties raw, double threshold)
    {
        if (!(threshold > 0) || !canvas.IsValid || !source.IsValid) return new CanvasSnapResult(raw, CropEdges.None);
        var bounds = CompositionMath.Layout(canvas, source, raw).Bounds;
        var edges = CropEdges.None;
        var result = raw;

        var (left, right) = (Math.Abs(bounds.X), Math.Abs(bounds.Right - canvas.Width));
        if (Math.Min(left, right) <= threshold)
        {
            var toLeft = left <= right;
            result = result with { PositionX = SnappedPosition(canvas, source, raw, horizontal: true, nearStart: toLeft, bounds) };
            edges |= toLeft ? CropEdges.Left : CropEdges.Right;
        }
        var (top, bottom) = (Math.Abs(bounds.Y), Math.Abs(bounds.Bottom - canvas.Height));
        if (Math.Min(top, bottom) <= threshold)
        {
            var toTop = top <= bottom;
            result = result with { PositionY = SnappedPosition(canvas, source, raw, horizontal: false, nearStart: toTop, bounds) };
            edges |= toTop ? CropEdges.Top : CropEdges.Bottom;
        }
        if (Math.Abs(result.PositionX) > ClipPropertyLimits.MaxPositionMagnitude || Math.Abs(result.PositionY) > ClipPropertyLimits.MaxPositionMagnitude)
            return new CanvasSnapResult(raw, CropEdges.None);
        return new CanvasSnapResult(result, edges);
    }

    /// <summary>The scale snapped (Scale around the centre): the picture edge nearest its canvas edge, within
    /// <paramref name="threshold"/> canvas pixels, lands exactly on it.</summary>
    public static CanvasSnapResult Scale(FrameSize canvas, FrameSize source, VisualProperties raw, double threshold)
    {
        if (!(threshold > 0) || !canvas.IsValid || !source.IsValid) return new CanvasSnapResult(raw, CropEdges.None);
        var bounds = CompositionMath.Layout(canvas, source, raw).Bounds;
        var candidates = new (CropEdges Edge, double Distance)[]
        {
            (CropEdges.Left, Math.Abs(bounds.X)), (CropEdges.Right, Math.Abs(bounds.Right - canvas.Width)),
            (CropEdges.Top, Math.Abs(bounds.Y)), (CropEdges.Bottom, Math.Abs(bounds.Bottom - canvas.Height))
        };
        foreach (var (edge, distance) in candidates.Where(c => c.Distance <= threshold).OrderBy(c => c.Distance))
        {
            var scale = SnappedScale(canvas, source, raw, edge, bounds);
            if (double.IsFinite(scale) && scale >= ClipPropertyLimits.MinScale && scale <= ClipPropertyLimits.MaxScale)
                return new CanvasSnapResult(raw with { Scale = scale }, edge);
        }
        return new CanvasSnapResult(raw, CropEdges.None);
    }

    /// <summary>
    /// D063 amendment 1: the snap of a corner scale with the opposite corner held (<see cref="PreviewTransformMath.ScaleFromAnchor"/>).
    /// <paramref name="start"/> is the picture at the gesture's start, <paramref name="raw"/> the gesture's result. On each axis
    /// the edge that moves (the dragged corner's side) snaps onto its canvas edge within <paramref name="threshold"/> — the
    /// nearer of the two axes sets the scale, the held edge stays put. Exact for a quarter-turned picture: the snapped edge
    /// lies on the canvas edge (the covering side), and a held edge that lay on a canvas edge at the start stays exactly on it,
    /// so a picture put in a corner and stretched to the opposite one fills the canvas with no black line.
    /// </summary>
    public static CanvasSnapResult ScaleFromAnchor(FrameSize canvas, FrameSize source, VisualProperties start, VisualProperties raw, double threshold)
    {
        if (!canvas.IsValid || !source.IsValid) return new CanvasSnapResult(raw, CropEdges.None);
        var startBounds = CompositionMath.Layout(canvas, source, start).Bounds;
        var bounds = CompositionMath.Layout(canvas, source, raw).Bounds;
        // The held side on each axis: the edge that moved less.
        var heldLeft = Math.Abs(bounds.X - startBounds.X) <= Math.Abs(bounds.Right - startBounds.Right);
        var heldTop = Math.Abs(bounds.Y - startBounds.Y) <= Math.Abs(bounds.Bottom - startBounds.Bottom);
        var result = raw;
        var edges = CropEdges.None;

        if (threshold > 0)
        {
            var candidates = new List<(CropEdges Edge, double Distance, bool Horizontal)>
            {
                heldLeft ? (CropEdges.Right, Math.Abs(bounds.Right - canvas.Width), true) : (CropEdges.Left, Math.Abs(bounds.X), true),
                heldTop ? (CropEdges.Bottom, Math.Abs(bounds.Bottom - canvas.Height), false) : (CropEdges.Top, Math.Abs(bounds.Y), false)
            };
            foreach (var (edge, distance, horizontal) in candidates.Where(c => c.Distance <= threshold).OrderBy(c => c.Distance))
            {
                // The held edge's coordinate and the moving edge's target: the extent between them sets the scale.
                var held = horizontal ? (heldLeft ? startBounds.X : startBounds.Right) : (heldTop ? startBounds.Y : startBounds.Bottom);
                var target = edge switch { CropEdges.Left => 0.0, CropEdges.Top => 0.0, CropEdges.Right => canvas.Width, _ => canvas.Height };
                var extentAtStart = horizontal ? startBounds.Width : startBounds.Height;
                var scale = ExactHalfExtentsAtOne(canvas, source, start) is { } half
                    ? (ExactRational.FromDouble(Math.Abs(target - held)) / (((ExactRational)2) * (horizontal ? half.HalfWidth : half.HalfHeight))).ToDoubleAtLeast()
                    : start.Scale * Math.Abs(target - held) / extentAtStart;
                if (!double.IsFinite(scale) || scale < ClipPropertyLimits.MinScale || scale > ClipPropertyLimits.MaxScale) continue;
                var anchor = new PointD(heldLeft ? startBounds.X : startBounds.Right, heldTop ? startBounds.Y : startBounds.Bottom);
                var centre = new PointD(startBounds.X + startBounds.Width / 2, startBounds.Y + startBounds.Height / 2);
                result = PreviewTransformMath.AboutAnchor(start, scale, anchor, centre);
                edges = edge;
                break;
            }
        }

        // Exact edges: the snapped one onto its canvas edge; a held one that lay on a canvas edge at the start stays on it.
        bool OnEdge(double value, double edge) => Math.Abs(value - edge) <= 1e-6;
        var b = CompositionMath.Layout(canvas, source, result).Bounds;
        if (edges is CropEdges.Left or CropEdges.Right)
            result = result with { PositionX = SnappedPosition(canvas, source, result, horizontal: true, nearStart: edges == CropEdges.Left, b) };
        else if (heldLeft ? OnEdge(startBounds.X, 0) : OnEdge(startBounds.Right, canvas.Width))
            result = result with { PositionX = SnappedPosition(canvas, source, result, horizontal: true, nearStart: heldLeft, b) };
        if (edges is CropEdges.Top or CropEdges.Bottom)
            result = result with { PositionY = SnappedPosition(canvas, source, result, horizontal: false, nearStart: edges == CropEdges.Top, b) };
        else if (heldTop ? OnEdge(startBounds.Y, 0) : OnEdge(startBounds.Bottom, canvas.Height))
            result = result with { PositionY = SnappedPosition(canvas, source, result, horizontal: false, nearStart: heldTop, b) };
        if (Math.Abs(result.PositionX) > ClipPropertyLimits.MaxPositionMagnitude || Math.Abs(result.PositionY) > ClipPropertyLimits.MaxPositionMagnitude)
            return new CanvasSnapResult(raw, CropEdges.None);
        return new CanvasSnapResult(result, edges);
    }

    /// <summary>The picture's half extents on the canvas at scale 1 (centred), exactly, for a quarter-turned picture.</summary>
    private static (ExactRational HalfWidth, ExactRational HalfHeight)? ExactHalfExtentsAtOne(FrameSize canvas, FrameSize source, VisualProperties v)
    {
        if (Affine2D.QuarterTurns(v.RotationDegrees) is not { } quarters) return null;
        ExactRational R(double x) => ExactRational.FromDouble(x);
        ExactRational cw = canvas.Width, ch = canvas.Height;
        var w = (1 - R(v.Crop.Left) - R(v.Crop.Right)) * source.Width;
        var h = (1 - R(v.Crop.Top) - R(v.Crop.Bottom)) * source.Height;
        var fit = ExactRational.Min(cw / w, ch / h);
        var two = (ExactRational)2;
        return quarters % 2 == 0 ? (w * fit / two, h * fit / two) : (h * fit / two, w * fit / two);
    }

    private static double SnappedPosition(FrameSize canvas, FrameSize source, VisualProperties v, bool horizontal, bool nearStart, RectD bounds)
    {
        if (ExactHalfExtentsAtOne(canvas, source, v) is { } half)
        {
            var extent = (horizontal ? half.HalfWidth : half.HalfHeight) * ExactRational.FromDouble(v.Scale);
            var size = (ExactRational)(horizontal ? canvas.Width : canvas.Height);
            var two = (ExactRational)2;
            // Near edge on 0: centre − extent = 0 → position = extent − size / 2 (at most: the edge at or past 0).
            // Far edge on the size: centre + extent = size → position = size / 2 − extent (at least).
            return nearStart ? (extent - size / two).ToDoubleAtMost() : (size / two - extent).ToDoubleAtLeast();
        }
        var position = horizontal ? v.PositionX : v.PositionY;
        var shift = horizontal
            ? (nearStart ? -bounds.X : canvas.Width - bounds.Right)
            : (nearStart ? -bounds.Y : canvas.Height - bounds.Bottom);
        return position + shift;
    }

    private static double SnappedScale(FrameSize canvas, FrameSize source, VisualProperties v, CropEdges edge, RectD bounds)
    {
        var horizontal = edge is CropEdges.Left or CropEdges.Right;
        if (ExactHalfExtentsAtOne(canvas, source, v) is { } half)
        {
            var extentAtOne = horizontal ? half.HalfWidth : half.HalfHeight;
            var size = (ExactRational)(horizontal ? canvas.Width : canvas.Height);
            var centre = size / (ExactRational)2 + ExactRational.FromDouble(horizontal ? v.PositionX : v.PositionY);
            // The edge on the canvas edge: centre − s·e = 0 (left / top) or centre + s·e = size (right / bottom); the scale
            // rounded up, so the picture reaches the edge.
            var distance = edge is CropEdges.Left or CropEdges.Top ? centre : size - centre;
            if (distance <= ExactRational.Zero) return double.NaN;
            return (distance / extentAtOne).ToDoubleAtLeast();
        }
        // Any other angle: the bounding box scales about the centre.
        var c = horizontal ? bounds.X + bounds.Width / 2 : bounds.Y + bounds.Height / 2;
        var e = (horizontal ? bounds.Width : bounds.Height) / 2 / v.Scale;
        var d = edge switch
        {
            CropEdges.Left => c,
            CropEdges.Top => c,
            CropEdges.Right => canvas.Width - c,
            _ => canvas.Height - c
        };
        return d > 0 && e > 0 ? d / e : double.NaN;
    }
}
