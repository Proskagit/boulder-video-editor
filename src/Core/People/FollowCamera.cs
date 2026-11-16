using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>Why a clip's Follow has no camera path.</summary>
public enum FollowUnavailable
{
    None,

    /// <summary>The followed person is never seen.</summary>
    NoTarget,

    /// <summary>The clip is rotated by an angle other than a multiple of 90°: the window cannot be kept exactly inside the
    /// picture (D061 §3.6).</summary>
    Rotation,

    /// <summary>The picture is not larger than the canvas on any axis at the clip's scale: there is no room to move it —
    /// zoom in (Scale above «Заполнить кадр») to follow.</summary>
    NoRoom,

    /// <summary>D061 amendment 1: the followed part of the video lies outside this clip (a split half, a trimmed clip) — the
    /// clip keeps its own framing; not a problem.</summary>
    OutsideRange
}

/// <summary>
/// The Follow camera of one clip (D061 §3.5): the clip's position on the canvas per source time — <see cref="At"/>
/// interpolates linearly between the analysed frames, holding the first / last value outside them. Only the position moves:
/// the scale, rotation, crop and opacity stay the clip's own. Immutable, read from any thread (the export's parallel
/// rasterizers, D059).
/// </summary>
public sealed record FollowPath(ImmutableArray<MediaTime> Times, ImmutableArray<double> X, ImmutableArray<double> Y)
{
    /// <summary>Whether the camera moves horizontally / vertically (the picture is larger than the canvas on that axis).</summary>
    public bool MovesX { get; init; }
    public bool MovesY { get; init; }

    public (double X, double Y) At(MediaTime time)
    {
        if (Times.IsDefaultOrEmpty) return (0, 0);
        if (time <= Times[0]) return (X[0], Y[0]);
        if (time >= Times[^1]) return (X[^1], Y[^1]);
        int lo = 0, hi = Times.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Times[mid] <= time) lo = mid; else hi = mid;
        }
        var span = (Times[hi] - Times[lo]).Ticks;
        var f = span > 0 ? (time - Times[lo]).Ticks / (double)span : 0;
        return (X[lo] + (X[hi] - X[lo]) * f, Y[lo] + (Y[hi] - Y[lo]) * f);
    }
}

/// <summary>
/// Turns the followed person's trajectory into the clip's camera path (D061 §3.5). Pure:
/// <list type="number">
/// <item><b>Losses.</b> Up to <see cref="GlideGap"/> the person's point is interpolated (the camera glides through a
/// short miss). A longer loss holds the last seen point and moves to the reappearance over the last
/// <see cref="EaseTime"/> before it (never a jump); before the first and after the last sighting the camera holds.</item>
/// <item><b>Placement.</b> The person's anchor (the torso centre) is put at the canvas centre: the clip's position is the
/// offset that maps the anchor there through the clip's own D018 geometry.</item>
/// <item><b>No black borders.</b> The position is clamped so the picture keeps covering the canvas on every axis where
/// it is larger than the canvas; on an axis where it is not (no room), the clip's own position is kept.</item>
/// <item><b>Smoothing.</b> A non-causal Gaussian over the clamped positions (σ by <see cref="FollowSmoothness"/>): the
/// trajectory is known in advance, so the camera neither lags nor shakes; a weighted mean of clamped values stays clamped.</item>
/// </list>
/// </summary>
public static class FollowCamera
{
    public static readonly MediaTime GlideGap = MediaTime.FromSeconds(1.5);
    public static readonly MediaTime EaseTime = MediaTime.FromSeconds(1.0);

    /// <summary>D061 amendment 1: how long the camera glides from the clip's own framing to the person at the start of the
    /// followed part (and back at its end) — inside the part, at most half of it.</summary>
    public static readonly MediaTime RangeEaseTime = MediaTime.FromSeconds(0.75);

    /// <summary>
    /// D061 amendment 1: <paramref name="path"/> limited to the followed part <c>[from, to]</c> of a clip that shows
    /// <c>[clipFrom, clipTo]</c>: outside the part the clip's own position (<paramref name="ownX"/>, <paramref name="ownY"/>);
    /// a side of the part at the clip's edge stays open beyond it (a Dissolve / Push shows the frames past the cut — the camera
    /// keeps following there, as with Follow on the whole clip); at an edge of the part that is not the clip's edge the camera
    /// glides between the two over
    /// <see cref="RangeEaseTime"/> (a smoothstep) inside the part — never a jump. The part as the whole clip returns the path
    /// unchanged. Samples are added at the edges so the glide starts exactly there.
    /// </summary>
    public static FollowPath Limit(FollowPath path, MediaTime from, MediaTime to, MediaTime clipFrom, MediaTime clipTo, double ownX, double ownY)
    {
        var easesIn = from > clipFrom;
        var easesOut = to < clipTo;
        if ((!easesIn && !easesOut) || path.Times.IsDefaultOrEmpty) return path;
        var ease = new MediaTime(Math.Min(RangeEaseTime.Ticks, Math.Max(1, (to - from).Ticks / 2)));
        double Weight(MediaTime t)
        {
            if ((easesIn && t < from) || (easesOut && t > to)) return 0;
            var w = 1.0;
            if (easesIn) w = Math.Min(w, SmoothStep((t - from).Ticks / (double)ease.Ticks));
            if (easesOut) w = Math.Min(w, SmoothStep((to - t).Ticks / (double)ease.Ticks));
            return w;
        }
        var times = path.Times.ToList();
        foreach (var edge in new[] { from, from + ease, to - ease, to })
            if (edge > times[0] && edge < times[^1] && times.BinarySearch(edge) < 0) times.Insert(~times.BinarySearch(edge), edge);
        var xs = ImmutableArray.CreateBuilder<double>(times.Count);
        var ys = ImmutableArray.CreateBuilder<double>(times.Count);
        foreach (var t in times)
        {
            var (x, y) = path.At(t);
            var w = Weight(t);
            xs.Add(ownX + (x - ownX) * w);
            ys.Add(ownY + (y - ownY) * w);
        }
        return path with { Times = times.ToImmutableArray(), X = xs.MoveToImmutable(), Y = ys.MoveToImmutable() };
    }

    /// <summary>The camera path, or null with the reason.</summary>
    public static (FollowPath? Path, FollowUnavailable Reason) Solve(FollowTrajectory trajectory, FrameSize canvas, FrameSize source,
        VisualProperties visual, FollowSmoothness smoothness)
    {
        if (Affine2D.QuarterTurns(visual.RotationDegrees) is null) return (null, FollowUnavailable.Rotation);
        var points = trajectory.Points;
        if (points.IsDefaultOrEmpty || !points.Any(p => p.IsSeen)) return (null, FollowUnavailable.NoTarget);

        var centred = CompositionMath.Layout(canvas, source, visual with { PositionX = 0, PositionY = 0 });
        var bounds = centred.Bounds;
        // A hair inside the exact limit, so a picture at the edge still provably covers the canvas (D018's exact test).
        const double inset = 1e-6;
        // Room to move: at least half a canvas pixel beyond the canvas.
        var movesX = bounds.Width - canvas.Width > 0.5;
        var movesY = bounds.Height - canvas.Height > 0.5;
        if (!movesX && !movesY) return (null, FollowUnavailable.NoRoom);
        var (minX, maxX) = (canvas.Width - bounds.X - bounds.Width + inset, -bounds.X - inset);
        var (minY, maxY) = (canvas.Height - bounds.Y - bounds.Height + inset, -bounds.Y - inset);
        if (minX > maxX) (minX, maxX) = (0, 0);
        if (minY > maxY) (minY, maxY) = (0, 0);

        var anchors = Filled(points);
        var crop = centred.SourceRect;
        var xs = new double[anchors.Length];
        var ys = new double[anchors.Length];
        for (var i = 0; i < anchors.Length; i++)
        {
            var local = new PointD(anchors[i].X * source.Width - crop.X, anchors[i].Y * source.Height - crop.Y);
            var q = centred.Transform.Apply(local);
            xs[i] = movesX ? Math.Clamp(canvas.Width / 2.0 - q.X, minX, maxX) : visual.PositionX;
            ys[i] = movesY ? Math.Clamp(canvas.Height / 2.0 - q.Y, minY, maxY) : visual.PositionY;
        }
        var times = points.Select(p => p.Time).ToImmutableArray();
        var sigma = FollowSettings.SigmaOf(smoothness);
        return (new FollowPath(times, Smooth(times, xs, sigma), Smooth(times, ys, sigma)) { MovesX = movesX, MovesY = movesY },
            FollowUnavailable.None);
    }

    /// <summary>The person's point on every analysed frame, the losses filled (step 1).</summary>
    public static NormalizedPoint[] Filled(ImmutableArray<FollowPoint> points)
    {
        var result = new NormalizedPoint[points.Length];
        var seen = Enumerable.Range(0, points.Length).Where(i => points[i].IsSeen).ToArray();
        var next = 0;
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i].Anchor is { } a) { result[i] = a; continue; }
            while (next < seen.Length && seen[next] < i) next++;
            var after = next < seen.Length ? seen[next] : -1;
            var before = next > 0 ? seen[next - 1] : -1;
            if (before < 0) { result[i] = points[after].Anchor!.Value; continue; }
            if (after < 0) { result[i] = points[before].Anchor!.Value; continue; }
            var (pa, pb) = (points[before].Anchor!.Value, points[after].Anchor!.Value);
            var (ta, tb, t) = (points[before].Time, points[after].Time, points[i].Time);
            double f;
            if (tb - ta <= GlideGap) f = (t - ta).Ticks / (double)(tb - ta).Ticks;
            else
            {
                var easeStart = tb - EaseTime;
                f = t <= easeStart ? 0 : SmoothStep((t - easeStart).Ticks / (double)EaseTime.Ticks);
            }
            result[i] = new NormalizedPoint(pa.X + (pb.X - pa.X) * f, pa.Y + (pb.Y - pa.Y) * f);
        }
        return result;
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>A truncated, renormalized Gaussian over the samples by time (non-causal; uneven spacing allowed).</summary>
    public static ImmutableArray<double> Smooth(ImmutableArray<MediaTime> times, double[] values, MediaTime sigma)
    {
        var s = sigma.TotalSeconds;
        var result = new double[values.Length];
        if (s <= 0) return ImmutableArray.Create(values);
        var reach = 3 * s;
        int lo = 0, hi = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var t = times[i].TotalSeconds;
            while (times[lo].TotalSeconds < t - reach) lo++;
            while (hi + 1 < values.Length && times[hi + 1].TotalSeconds <= t + reach) hi++;
            double sum = 0, weight = 0;
            for (var j = lo; j <= hi; j++)
            {
                var d = (times[j].TotalSeconds - t) / s;
                var w = Math.Exp(-0.5 * d * d);
                sum += w * values[j];
                weight += w;
            }
            result[i] = weight > 0 ? sum / weight : values[i];
        }
        return ImmutableArray.Create(result);
    }
}
