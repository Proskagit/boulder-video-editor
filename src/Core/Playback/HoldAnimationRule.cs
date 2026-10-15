using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// When each hold of an animated route is shown (D036 §2), on timeline frames like the fades of D025 — so the Preview and
/// the export, which both take a frame's layers from <see cref="PlaybackSnapshot.LayersAt"/>, give every frame the same
/// value. Times are of the hold markup's clip; the frames are timeline frames of the project rate.
/// <list type="bullet">
/// <item>Start frame <c>n0</c>: the first timeline frame <c>n</c> whose source time <c>t(n) = SourceIn + (FromFrame(n) − S) ·
/// speed</c> (D009 / D022) is at or after <see cref="HoldAnimation.StartSourceTime"/> — i.e.
/// <c>T0 = S + (StartSourceTime − SourceIn) / speed</c> moved up to the frame grid, exact in integers. It may lie before the
/// clip (the animation is under way at the clip's start, e.g. the second half of a split) or after it.</item>
/// <item>Durations in frames by <see cref="TransitionRules.Frames"/> (<c>ToNearestFrame</c>): the hold of rank <c>r</c>
/// appears at <c>A_r = n0 + Frames(r · Interval)</c> (no drift of a rounded interval), fades in over
/// <c>Fa = Frames(AppearFade)</c>; after the last, <c>A_last + Fa</c>, every contour stays <c>Frames(StayAfterLast)</c>
/// and then fades out together over <c>Fd = Frames(DisappearFade)</c> — or, with "until clip end", stays.</item>
/// <item>Opacity of a hold at frame <c>n</c>: 0 before <c>A_r</c>; then <see cref="FadeRule.Ramp"/>(<c>n − A_r</c>, <c>Fa</c>)
/// (<c>1/(Fa + 1)</c> on its first frame, 1 from <c>A_r + Fa</c>); from the disappear start <c>D</c> × <c>(Fd − j)/(Fd + 1)</c>
/// for <c>j = n − D &lt; Fd</c>, 0 from <c>D + Fd</c> — the D025 ramps.</item>
/// </list>
/// </summary>
public static class HoldAnimationRule
{
    /// <summary>Centres closer than this (normalized units) count as level: the marking order decides.</summary>
    public const double LevelTolerance = 1e-9;

    /// <summary>The start frame <c>n0</c> of an animation starting at source time <paramref name="startSourceTime"/> on a
    /// clip at <paramref name="clipStart"/> / <paramref name="sourceIn"/> / <paramref name="speed"/>.</summary>
    public static long StartFrame(MediaTime clipStart, MediaTime sourceIn, ClipSpeed speed, MediaTime startSourceTime, FrameRate rate)
    {
        long a = speed.Numerator, b = speed.Denominator;   // speed = a/b
        // t(n) ≥ start ⇔ FromFrame(n) − S ≥ b·(start − SourceIn)/a ⇔ FromFrame(n) ≥ S + ⌈b·(start − SourceIn)/a⌉ (ticks).
        var offset = CeilingDiv((Int128)b * (startSourceTime.Ticks - sourceIn.Ticks), a);
        var t0 = (Int128)clipStart.Ticks + offset;
        return new MediaTime(checked((long)t0)).ToFrameCeiling(rate);
    }

    /// <summary>The source time <c>t(n)</c> a timeline frame shows (rounded down to a tick): for "Set to playhead" — an
    /// animation starting there starts exactly at that frame.</summary>
    public static MediaTime SourceTimeAt(MediaTime clipStart, MediaTime sourceIn, ClipSpeed speed, long frame, FrameRate rate)
    {
        long a = speed.Numerator, b = speed.Denominator;
        var bt = (Int128)b * sourceIn.Ticks + (Int128)a * (MediaTime.FromFrame(frame, rate).Ticks - clipStart.Ticks);
        return new MediaTime(checked((long)FloorDiv(bt, b)));
    }

    /// <summary>The rank (0 = first to appear) of each hold of <paramref name="holds"/>, by index. Bottom to top: the
    /// centre of the contour (its area centroid; the mean of its vertices for a degenerate one), the larger Y (lower in the
    /// frame) first; level centres from left to right; then the marking order. Marking order: the index itself.</summary>
    public static ImmutableArray<int> Ranks(ImmutableArray<Hold> holds, HoldAnimationOrder order)
    {
        var indices = Enumerable.Range(0, holds.Length).ToList();
        if (order == HoldAnimationOrder.BottomToTop)
        {
            var centres = holds.Select(h => Centre(h.Points)).ToArray();
            indices.Sort((i, j) =>
            {
                var (ci, cj) = (centres[i], centres[j]);
                if (Math.Abs(ci.Y - cj.Y) > LevelTolerance) return cj.Y.CompareTo(ci.Y);
                if (Math.Abs(ci.X - cj.X) > LevelTolerance) return ci.X.CompareTo(cj.X);
                return i.CompareTo(j);
            });
        }
        var ranks = new int[holds.Length];
        for (var r = 0; r < indices.Count; r++) ranks[indices[r]] = r;
        return ranks.ToImmutableArray();
    }

    /// <summary>The schedule of <paramref name="animation"/> for <paramref name="holds"/> starting at frame
    /// <paramref name="startFrame"/>.</summary>
    public static HoldSchedule Schedule(HoldAnimation animation, ImmutableArray<Hold> holds, long startFrame, FrameRate rate)
    {
        var ranks = Ranks(holds, animation.Order);
        var appear = ranks.Select(r => startFrame + TransitionRules.Frames(new MediaTime(r * animation.Interval.Ticks), rate)).ToImmutableArray();
        var appearFade = TransitionRules.Frames(animation.AppearFade, rate);
        var disappearFade = TransitionRules.Frames(animation.DisappearFade, rate);
        var lastAppear = appear.IsEmpty ? startFrame : appear.Max();
        long? disappearStart = animation.StayUntilClipEnd
            ? null
            : lastAppear + appearFade + TransitionRules.Frames(animation.StayAfterLast, rate);
        return new HoldSchedule(startFrame, appear, appearFade, disappearStart, disappearFade);
    }

    /// <summary>The centre of a contour: its area centroid (shoelace); the mean of its vertices when it has no area.</summary>
    public static NormalizedPoint Centre(ImmutableArray<NormalizedPoint> points)
    {
        if (points.IsDefaultOrEmpty) return new NormalizedPoint(0, 0);
        double area = 0, cx = 0, cy = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Length]);
            var cross = p.X * q.Y - q.X * p.Y;
            area += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(area) > 1e-12) return new NormalizedPoint(cx / (3 * area), cy / (3 * area));
        return new NormalizedPoint(points.Average(p => p.X), points.Average(p => p.Y));
    }

    private static Int128 FloorDiv(Int128 n, Int128 d)
    {
        var q = n / d;
        return (n % d != 0 && (n < 0) != (d < 0)) ? q - 1 : q;
    }

    private static Int128 CeilingDiv(Int128 n, Int128 d) => -FloorDiv(-n, d);
}

/// <summary>
/// The frames of one animated route (D036, <see cref="HoldAnimationRule"/>): <see cref="AppearFrames"/> by hold index (the
/// markup's order, which is also the drawing order), the fade lengths and the disappear start (null: the contours stay to
/// the clip's end). Renderer-neutral and immutable.
/// </summary>
public sealed record HoldSchedule(long StartFrame, ImmutableArray<long> AppearFrames, long AppearFadeFrames, long? DisappearStartFrame,
    long DisappearFadeFrames)
{
    /// <summary>The frame the last hold appears at.</summary>
    public long LastAppearFrame => AppearFrames.IsEmpty ? StartFrame : AppearFrames.Max();

    /// <summary>The first frame on which every hold is fully visible.</summary>
    public long AllVisibleFrame => LastAppearFrame + AppearFadeFrames;

    /// <summary>The first frame with no contour left; null when they stay to the clip's end.</summary>
    public long? EndFrame => DisappearStartFrame + DisappearFadeFrames;

    /// <summary>The opacity of hold <paramref name="hold"/> on timeline frame <paramref name="frame"/>: 0..1.</summary>
    public double OpacityOf(int hold, long frame)
    {
        var appear = AppearFrames[hold];
        if (frame < appear) return 0;
        var opacity = FadeRule.Ramp(frame - appear, AppearFadeFrames);
        if (DisappearStartFrame is { } d && frame >= d)
        {
            var j = frame - d;
            opacity *= j < DisappearFadeFrames ? (double)(DisappearFadeFrames - j) / (DisappearFadeFrames + 1) : 0;
        }
        return opacity;
    }

    /// <summary>Every hold's opacity on <paramref name="frame"/>, by index.</summary>
    public ImmutableArray<double> OpacitiesAt(long frame)
    {
        var result = new double[AppearFrames.Length];
        for (var i = 0; i < result.Length; i++) result[i] = OpacityOf(i, frame);
        return result.ToImmutableArray();
    }
}
