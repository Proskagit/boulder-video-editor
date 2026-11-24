using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>
/// D063 §4: where a followed picture lies on the canvas, centred, at the clip's own scale <see cref="Scale"/>
/// (<see cref="Bounds"/>, D018) — and so at any scale <c>s</c>: the picture is scaled about the canvas centre before its
/// position, so its centred bounds are <see cref="Bounds"/> scaled by <c>s / Scale</c> about the centre. The limits keep the
/// picture covering the canvas (D061: a hair inside the exact limit); at <see cref="Scale"/> itself they are exactly D061's.
/// </summary>
public sealed record FollowExtent(RectD Bounds, double Scale)
{
    /// <summary>A hair inside the exact limit, so a picture at the edge still provably covers the canvas (D018's exact test).</summary>
    public const double Inset = 1e-6;

    /// <summary>The centred bounds at <paramref name="scale"/>.</summary>
    public RectD BoundsAt(double scale, FrameSize canvas)
    {
        if (scale == Scale) return Bounds;
        var r = scale / Scale;
        var (cx, cy) = (canvas.Width / 2.0, canvas.Height / 2.0);
        return new RectD(cx - r * (cx - Bounds.X), cy - r * (cy - Bounds.Y), r * Bounds.Width, r * Bounds.Height);
    }

    /// <summary>Room to move on the axis: at least half a canvas pixel beyond the canvas.</summary>
    public bool RoomX(double scale, FrameSize canvas) => BoundsAt(scale, canvas).Width - canvas.Width > 0.5;
    public bool RoomY(double scale, FrameSize canvas) => BoundsAt(scale, canvas).Height - canvas.Height > 0.5;

    public double ClampX(double x, double scale, FrameSize canvas)
    {
        var b = BoundsAt(scale, canvas);
        var (min, max) = (canvas.Width - b.X - b.Width + Inset, -b.X - Inset);
        return min > max ? 0 : Math.Clamp(x, min, max);
    }

    public double ClampY(double y, double scale, FrameSize canvas)
    {
        var b = BoundsAt(scale, canvas);
        var (min, max) = (canvas.Height - b.Y - b.Height + Inset, -b.Y - Inset);
        return min > max ? 0 : Math.Clamp(y, min, max);
    }
}

/// <summary>
/// D061 amendment 1: the weights of a clip's followed parts per source time — 1 inside a part, gliding (a smoothstep over
/// <see cref="FollowCamera.RangeEaseTime"/>, at most half the part) to 0 at a free edge, passing from one part to the next at
/// an edge they share (the two weights sum to 1), open beyond the clip's edges (a Dissolve / Push shows frames past the cut).
/// Shared by <see cref="FollowCamera.Combine"/> and <see cref="FollowFraming"/>.
/// </summary>
internal sealed class FollowBlend
{
    private readonly IReadOnlyList<(MediaTime From, MediaTime To)> _parts;
    private readonly MediaTime _clipFrom, _clipTo;
    private readonly long[] _ease;
    private readonly bool[] _joinedBefore, _joinedAfter;
    private readonly long[] _joinEase;   // the ease of the boundary between part i-1 and i

    public FollowBlend(IReadOnlyList<(MediaTime From, MediaTime To)> parts, MediaTime clipFrom, MediaTime clipTo)
    {
        const long touch = TimeSpan.TicksPerMillisecond;
        _parts = parts;
        (_clipFrom, _clipTo) = (clipFrom, clipTo);
        var n = parts.Count;
        _ease = new long[n];
        for (var i = 0; i < n; i++) _ease[i] = Math.Min(FollowCamera.RangeEaseTime.Ticks, Math.Max(1, (parts[i].To - parts[i].From).Ticks / 2));
        _joinedBefore = new bool[n];
        _joinedAfter = new bool[n];
        _joinEase = new long[n + 1];
        for (var i = 1; i < n; i++)
            if (Math.Abs((parts[i].From - parts[i - 1].To).Ticks) <= touch)
            {
                _joinedBefore[i] = _joinedAfter[i - 1] = true;
                _joinEase[i] = Math.Min(_ease[i], _ease[i - 1]);
            }
    }

    public double Weight(int i, MediaTime t)
    {
        var (from, to) = _parts[i];
        var w = 1.0;
        if (_joinedBefore[i]) w = Math.Min(w, FollowCamera.SmoothStep((t.Ticks - (from.Ticks - _joinEase[i] / 2.0)) / _joinEase[i]));
        else if (from > _clipFrom)
        {
            if (t < from) return 0;
            w = Math.Min(w, FollowCamera.SmoothStep((t - from).Ticks / (double)_ease[i]));
        }
        if (_joinedAfter[i]) w = Math.Min(w, 1 - FollowCamera.SmoothStep((t.Ticks - (to.Ticks - _joinEase[i + 1] / 2.0)) / _joinEase[i + 1]));
        else if (to < _clipTo)
        {
            if (t > to) return 0;
            w = Math.Min(w, FollowCamera.SmoothStep((to - t).Ticks / (double)_ease[i]));
        }
        return w;
    }

    /// <summary>The times where a glide starts or ends (a sampled path keeps them).</summary>
    public IEnumerable<MediaTime> EdgeTimes()
    {
        for (var i = 0; i < _parts.Count; i++)
        {
            var (from, to) = _parts[i];
            foreach (var edge in new[] { from, from + new MediaTime(_ease[i]), to - new MediaTime(_ease[i]), to,
                         from - new MediaTime(_joinEase[i] / 2), from + new MediaTime(_joinEase[i] / 2) })
                yield return edge;
        }
    }
}

/// <summary>One followed part of a <see cref="FollowFraming"/>: its index in the clip's Follow, its camera path, its range in
/// the clip (source time) and its framing — the static offset and the framing keyframes (values, never the project's
/// entities: the snapshot holds no mutable project state).</summary>
public sealed record FollowFramingPart(int Index, FollowPath Path, MediaTime From, MediaTime To, double OffsetX = 0, double OffsetY = 0,
    TransformKeyframes? Keyframes = null)
{
    /// <summary>D080: the part's dynamic scale per source time, or null (the scale of its keyframes, else the clip's).</summary>
    public FollowZoomCurve? Zoom { get; init; }

    /// <summary>The part's framing at <paramref name="time"/> (<see cref="FollowPart.FramingAt"/>); with the dynamic scale its
    /// scale is the curve's.</summary>
    public TransformValue FramingAt(MediaTime time, double clipScale)
    {
        var framing = Keyframes is { } keys ? keys.At(time) : new TransformValue(OffsetX, OffsetY, clipScale);
        return Zoom is { } zoom ? framing with { Scale = zoom.At(time) } : framing;
    }
}

/// <summary>
/// D063 §4–§5: a clip's framing per source time while it has Follow — the one evaluation the Preview, the export and the
/// editor share (<see cref="CompositionDrawPlan.GeometryAt"/>). Outside every followed part the clip's own transform (its
/// keyframes, else its static values); inside a part the camera's position on the person at the part's scale (the part's
/// keyframes, else the clip's own scale there), clamped on that very frame so the picture keeps covering the canvas; at a
/// part's free edge the two blend by the part's weight (<see cref="FollowBlend"/>) — the position and the scale together, so
/// Follow's start and end never jump, whatever the two transforms are. The two keyframe systems stay independent: the clip's
/// keyframes never change a part's framing, a part's keyframes never change the clip's transform. Immutable, read from any
/// thread (the export's parallel rasterizers).
/// </summary>
public sealed class FollowFraming
{
    private readonly FollowBlend _blend;

    public FollowFraming(IReadOnlyList<FollowFramingPart> parts, MediaTime clipFrom, MediaTime clipTo, VisualProperties visual,
        TransformKeyframes? clipKeyframes, FrameSize canvas)
    {
        Parts = parts.ToImmutableArray();
        Visual = visual;
        ClipKeyframes = clipKeyframes;
        Canvas = canvas;
        _blend = new FollowBlend(parts.Select(p => (p.From, p.To)).ToList(), clipFrom, clipTo);
    }

    public ImmutableArray<FollowFramingPart> Parts { get; }

    /// <summary>D063: some part has its own scale (framing keyframes) — the picture drawn may then be smaller than the clip's
    /// own transform, so the layer never hides what is below it on that account (<see cref="Composition.PictureLayer.OccludesBelow"/>).</summary>
    public bool HasPartScale => Parts.Any(p => p.Keyframes is not null || p.Zoom is not null);

    /// <summary>The clip's static values (its rotation, opacity and crop are always these).</summary>
    public VisualProperties Visual { get; }

    public TransformKeyframes? ClipKeyframes { get; }

    public FrameSize Canvas { get; }

    /// <summary>The clip's own transform at source time <paramref name="time"/> (as without Follow).</summary>
    public VisualProperties OwnAt(MediaTime time) => TransformKeyframes.Apply(Visual, ClipKeyframes, time);

    /// <summary>The picture's properties at source time <paramref name="time"/>.</summary>
    public VisualProperties At(MediaTime time)
    {
        var own = OwnAt(time);
        double x = own.PositionX, y = own.PositionY, s = own.Scale;
        for (var i = 0; i < Parts.Length; i++)
        {
            var w = _blend.Weight(i, time);
            if (w <= 0) continue;
            var (px, py, ps) = Placed(Parts[i], time, own);
            x += (px - own.PositionX) * w;
            y += (py - own.PositionY) * w;
            s += (ps - own.Scale) * w;
        }
        return own with { PositionX = x, PositionY = y, Scale = s };
    }

    /// <summary>The part of the largest weight at <paramref name="time"/> (the one followed fully inside a part), or null
    /// outside every part.</summary>
    public FollowFramingPart? PartAt(MediaTime time)
    {
        FollowFramingPart? best = null;
        var most = 0.0;
        for (var i = 0; i < Parts.Length; i++)
        {
            var w = _blend.Weight(i, time);
            if (w > most) (best, most) = (Parts[i], w);
        }
        return best;
    }

    /// <summary>Where <paramref name="part"/> puts the picture at <paramref name="time"/>: the camera's position clamped at
    /// the part's scale there; an axis with no room keeps the clip's own position.</summary>
    private (double X, double Y, double Scale) Placed(FollowFramingPart part, MediaTime time, VisualProperties own)
    {
        var scale = part.FramingAt(time, own.Scale).Scale;
        var (px, py) = part.Path.At(time);
        if (part.Path.Extent is { } extent)
        {
            px = part.Path.MovesX && extent.RoomX(scale, Canvas) ? extent.ClampX(px, scale, Canvas) : own.PositionX;
            py = part.Path.MovesY && extent.RoomY(scale, Canvas) ? extent.ClampY(py, scale, Canvas) : own.PositionY;
        }
        return (px, py, scale);
    }
}
