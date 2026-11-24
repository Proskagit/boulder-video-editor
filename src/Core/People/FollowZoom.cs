using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>
/// D080 §3: a followed part's dynamic scale — the picture's <see cref="VisualProperties.Scale"/> per analysed frame of the
/// trajectory, so the climber keeps <see cref="Share"/> of the frame's height. <see cref="At"/> interpolates linearly between
/// the analysed frames (values never below the fill stay above it between them), holding the first / last value outside them.
/// Immutable, read from any thread (the export's parallel rasterizers).
/// </summary>
public sealed record FollowZoomCurve(ImmutableArray<MediaTime> Times, ImmutableArray<double> Scales, double Share, bool IsAuto)
{
    public double At(MediaTime time)
    {
        if (time <= Times[0]) return Scales[0];
        if (time >= Times[^1]) return Scales[^1];
        int lo = 0, hi = Times.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Times[mid] <= time) lo = mid; else hi = mid;
        }
        var span = (Times[hi] - Times[lo]).Ticks;
        var f = span > 0 ? (time - Times[lo]).Ticks / (double)span : 0;
        return Scales[lo] + (Scales[hi] - Scales[lo]) * f;
    }
}

/// <summary>
/// D080 §3 — Follow keeps the climber at a scale. Pure:
/// <list type="number">
/// <item><b>The climber's height</b> on the canvas at scale 1 per analysed frame (his box through the clip's own D018 geometry,
/// any quarter turn); a loss is filled from the neighbours (linear inside, held at the ends), then a running median over
/// <see cref="MedianWindow"/> takes out the pose (arms up, a leg out).</item>
/// <item><b>The scale</b> <c>share × canvas height / height</c>, never below «Заполнить» (the picture keeps covering the
/// canvas — no black borders) and never above the cap (the automatic default's <see cref="AutoMaxZoom"/>, else
/// <see cref="ClipPropertyLimits.MaxScale"/>) — clamped before the smoothing.</item>
/// <item><b>Smoothing</b>: the Follow camera's Gaussian (σ by «Плавность») over the logarithm of the scale — a weighted mean
/// of clamped values stays clamped, so the camera never shows a border and has no kink at the limit.</item>
/// </list>
/// <b>The automatic share</b> (the owner's answer to Q2): the share at which the scale reaches <see cref="AutoMaxZoom"/> × the
/// part's starting scale (at least the fill) — limited by the source's reserve (one source pixel per canvas pixel) — when the
/// climber is at his small end (<see cref="AutoPercentile"/> of his height over the part: the top of the wall).
/// </summary>
public static class FollowZoom
{
    /// <summary>The owner (Q2): the automatic default zooms in by at most about 20 % over the part's starting frame.</summary>
    public const double AutoMaxZoom = 1.2;

    /// <summary>The climber's small end of the part for the automatic share.</summary>
    public const double AutoPercentile = 0.1;

    /// <summary>The user's share's limits (the climber's height / the frame's height).</summary>
    public const double MinShare = 0.05;
    public const double MaxShare = 1.0;

    public static readonly MediaTime MedianWindow = MediaTime.FromSeconds(2);

    /// <summary>The dynamic scale of a part <c>[from, to]</c>; null when the climber is never seen or the picture has no size.
    /// <paramref name="baseScale"/>: the clip's own scale at the part's start; <paramref name="share"/>: the user's share, null —
    /// automatic.</summary>
    public static FollowZoomCurve? Solve(FollowTrajectory trajectory, MediaTime from, MediaTime to, FrameSize canvas, FrameSize source,
        VisualProperties visual, double baseScale, double? share, FollowSmoothness smoothness)
    {
        var points = trajectory.Points;
        if (points.IsDefaultOrEmpty || !points.Any(p => p.IsSeen && p.Box is not null)) return null;
        var heights = Cleaned(points, Heights(points, canvas, source, visual));
        if (heights is null) return null;
        var times = points.Select(p => p.Time).ToImmutableArray();

        var fill = CompositionMath.FillScale(canvas, source, visual);
        var start = Math.Max(baseScale, fill);
        var unit = CompositionMath.Layout(canvas, source, visual with { Scale = 1, PositionX = 0, PositionY = 0 }).FitScale;
        var native = unit > 0 ? 1 / unit : start;
        var autoCap = Math.Clamp(Math.Min(start * AutoMaxZoom, Math.Max(start, native)), fill, ClipPropertyLimits.MaxScale);

        var isAuto = share is null;
        var shareUsed = share ?? AutoShare(times, heights, from, to, canvas, autoCap);
        var cap = isAuto ? autoCap : ClipPropertyLimits.MaxScale;
        var logs = new double[heights.Length];
        for (var i = 0; i < heights.Length; i++)
            logs[i] = Math.Log(Math.Clamp(shareUsed * canvas.Height / heights[i], fill, Math.Max(fill, cap)));
        var smoothed = FollowCamera.Smooth(times, logs, FollowSettings.SigmaOf(smoothness));
        // Only rounding is corrected here: the mean of clamped logarithms is already within the limits.
        var scales = smoothed.Select(l => Math.Clamp(Math.Exp(l), fill, Math.Max(fill, cap))).ToImmutableArray();
        return new FollowZoomCurve(times, scales, shareUsed, isAuto);
    }

    /// <summary>The climber's share of the frame's height at <paramref name="scale"/> for his height <paramref name="height"/>
    /// (canvas pixels at scale 1).</summary>
    public static double ShareAt(double height, double scale, FrameSize canvas) => height * scale / canvas.Height;

    /// <summary>Whether the user has scaled the clip by hand (D080, the owner's answer to Q3): its own scale is neither «Вписать»
    /// nor «Заполнить», or its keyframes change the scale — then a new followed part keeps that scale and only centres the
    /// climber (the dynamic scale off); otherwise it is on.</summary>
    public static bool IsHandScaled(VideoClip clip, FrameSize canvas, FrameSize? source)
    {
        const double tolerance = 1e-4;
        if (clip.Keyframes is { } keys && keys.Keys.Any(k => Math.Abs(k.Scale - keys.Keys[0].Scale) > tolerance)) return true;
        var visual = VisualProperties.Of(clip) ?? VisualProperties.Default;
        var scale = clip.Keyframes is { } k0 ? k0.Keys[0].Scale : visual.Scale;
        if (Math.Abs(scale - 1) <= tolerance) return false;
        if (source is not { } size || !canvas.IsValid || !size.IsValid) return true;
        if (Affine2D.QuarterTurns(visual.RotationDegrees) is null) return true;
        var fit = CompositionMath.FitScale(canvas, size, visual);
        var fill = CompositionMath.FillScale(canvas, size, visual);
        return Math.Abs(scale - fit) > tolerance * fit && Math.Abs(scale - fill) > tolerance * fill;
    }

    /// <summary>The climber's height on the canvas at scale 1 per point (NaN where he is not seen).</summary>
    internal static double[] Heights(ImmutableArray<FollowPoint> points, FrameSize canvas, FrameSize source, VisualProperties visual)
    {
        var g = CompositionMath.Layout(canvas, source, visual with { PositionX = 0, PositionY = 0 });
        var crop = g.SourceRect;
        var result = new double[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i] is not { IsSeen: true, Box: { } box }) { result[i] = double.NaN; continue; }
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var (x, y) in new[] { (box.X0, box.Y0), (box.X1, box.Y0), (box.X0, box.Y1), (box.X1, box.Y1) })
            {
                var q = g.Transform.Apply(new PointD(x * source.Width - crop.X, y * source.Height - crop.Y));
                lo = Math.Min(lo, q.Y);
                hi = Math.Max(hi, q.Y);
            }
            var h = (hi - lo) / visual.Scale;
            result[i] = h > 0 ? h : double.NaN;
        }
        return result;
    }

    /// <summary>Losses filled like the Follow camera's anchor (<see cref="FollowCamera.Filled"/>: a glide through a loss up to
    /// <see cref="FollowCamera.GlideGap"/>, a longer one holds the last height and eases to the next over the last
    /// <see cref="FollowCamera.EaseTime"/>; held at the ends) — so the scale never moves on its own while the position holds —
    /// then the running median; null when no height.</summary>
    internal static double[]? Cleaned(ImmutableArray<FollowPoint> points, double[] heights)
    {
        var seen = Enumerable.Range(0, heights.Length).Where(i => !double.IsNaN(heights[i])).ToArray();
        if (seen.Length == 0) return null;
        var filled = new double[heights.Length];
        var next = 0;
        for (var i = 0; i < heights.Length; i++)
        {
            if (!double.IsNaN(heights[i])) { filled[i] = heights[i]; continue; }
            while (next < seen.Length && seen[next] < i) next++;
            var after = next < seen.Length ? seen[next] : -1;
            var before = next > 0 ? seen[next - 1] : -1;
            if (before < 0) { filled[i] = heights[after]; continue; }
            if (after < 0) { filled[i] = heights[before]; continue; }
            var (ta, tb, t) = (points[before].Time, points[after].Time, points[i].Time);
            double f;
            if (tb - ta <= FollowCamera.GlideGap) f = (t - ta).Ticks / (double)(tb - ta).Ticks;
            else
            {
                var easeStart = tb - FollowCamera.EaseTime;
                f = t <= easeStart ? 0 : FollowCamera.SmoothStep((t - easeStart).Ticks / (double)FollowCamera.EaseTime.Ticks);
            }
            filled[i] = heights[before] + (heights[after] - heights[before]) * f;
        }
        var half = MedianWindow.Ticks / 2;
        var result = new double[filled.Length];
        int lo = 0, hi = 0;
        var window = new List<double>();
        for (var i = 0; i < filled.Length; i++)
        {
            var t = points[i].Time.Ticks;
            while (points[lo].Time.Ticks < t - half) lo++;
            while (hi + 1 < filled.Length && points[hi + 1].Time.Ticks <= t + half) hi++;
            window.Clear();
            for (var j = lo; j <= hi; j++) window.Add(filled[j]);
            window.Sort();
            var n = window.Count;
            result[i] = n % 2 == 1 ? window[n / 2] : (window[n / 2 - 1] + window[n / 2]) / 2;
        }
        return result;
    }

    /// <summary>The automatic share: the climber at his small end of the part reaches <paramref name="cap"/>.</summary>
    private static double AutoShare(ImmutableArray<MediaTime> times, double[] heights, MediaTime from, MediaTime to, FrameSize canvas, double cap)
    {
        var inPart = Enumerable.Range(0, heights.Length).Where(i => times[i] >= from && times[i] <= to).Select(i => heights[i]).ToList();
        if (inPart.Count == 0) inPart = heights.ToList();
        inPart.Sort();
        var small = inPart[(int)Math.Floor(AutoPercentile * (inPart.Count - 1))];
        return Math.Clamp(ShareAt(small, cap, canvas), MinShare, MaxShare);
    }
}
