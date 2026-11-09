using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// D057 §3.14 (amends D006 for the playhead): stepping the playhead frame by frame by the source of the active video clip.
/// The highest frame rate among the timeline's video clips (and the project's) is the navigation grid — the finest place the
/// playhead may take; a step goes to the previous / next frame of the topmost visible video clip's source at its speed
/// (30 fps → 1/30 s, 240 fps → 1/240 s, a 240 fps clip at 0.25× → 1/60 s), never finer than that grid; where no video clip is,
/// one frame of the project. Clips' edges and grid-bound edits stay on the project's frames (D006); only the playhead and the
/// Preview may lie between them. Integer ticks throughout (D001, D006).
/// </summary>
public static class FrameNavigation
{
    /// <summary>The navigation grid: the highest of the project's rate and the source rates of the video clips.</summary>
    public static FrameRate NavigationRate(Sequence timeline, Func<Guid, MediaAsset?> asset, FrameRate projectRate)
    {
        var best = projectRate;
        foreach (var clip in timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>())
            if (SourceRate(clip, asset) is { } rate && Faster(rate, best)) best = rate;
        return best;
    }

    /// <summary>The nominal frame rate of a video clip's source (the sampling rate of D009), or null.</summary>
    public static FrameRate? SourceRate(VideoClip clip, Func<Guid, MediaAsset?> asset) =>
        asset(clip.MediaAssetId)?.Metadata is { } m ? m.AvgFrameRate ?? m.FrameRate : null;

    /// <summary>The topmost visible video clip at <paramref name="time"/> (its span holds it), or null.</summary>
    public static VideoClip? ActiveClip(Sequence timeline, MediaTime time)
    {
        for (var i = timeline.VideoTracks.Count - 1; i >= 0; i--)
        {
            var track = timeline.VideoTracks[i];
            if (track.IsHidden) continue;
            if (track.Clips.OfType<VideoClip>().FirstOrDefault(c => c.TimelineStart <= time && time < c.TimelineEnd) is { } clip)
                return clip;
        }
        return null;
    }

    /// <summary>The playhead after one step from <paramref name="position"/>: <paramref name="direction"/> +1 forward, −1 back.
    /// The source frame a position shows is the one playback shows there (D009 / D022: <see cref="SourceFrameSelector"/>).</summary>
    public static MediaTime Step(Sequence timeline, Func<Guid, MediaAsset?> asset, MediaTime position, int direction, FrameRate projectRate)
    {
        var grid = NavigationRate(timeline, asset, projectRate);
        if (direction >= 0)
        {
            if (ActiveClip(timeline, position) is { } clip && SourceRate(clip, asset) is { } rate)
            {
                var n0 = position.ToFrameFloor(grid);
                var k = Shown(clip, n0, grid, rate);
                var end = clip.TimelineEnd.ToFrameCeiling(grid);
                var n = Math.Max(n0 + 1, TimeOf(clip, MediaTime.FromFrame(k + 1, rate)).ToFrameFloor(grid) - 1);
                while (n < end && Shown(clip, n, grid, rate) <= k) n++;
                var next = MediaTime.FromFrame(n, grid);
                return next < clip.TimelineEnd ? next : clip.TimelineEnd;
            }
            return MediaTime.FromFrame(position.ToFrameFloor(projectRate) + 1, projectRate);
        }

        if (position <= MediaTime.Zero) return MediaTime.Zero;
        var before = new MediaTime(position.Ticks - 1);
        if (ActiveClip(timeline, before) is { } prev && SourceRate(prev, asset) is { } prevRate)
        {
            var first = prev.TimelineStart.ToFrameCeiling(grid);
            var n = Math.Max(first, before.ToFrameFloor(grid));
            var k = Shown(prev, n, grid, prevRate);                      // the frame shown just before the playhead
            while (n > first && Shown(prev, n - 1, grid, prevRate) >= k) n--;
            var at = MediaTime.FromFrame(n, grid);
            return at > prev.TimelineStart ? at : prev.TimelineStart;
        }
        return MediaTime.FromFrame(Math.Max(0, position.ToFrameCeiling(projectRate) - 1), projectRate);
    }

    /// <summary>The source frame index <paramref name="clip"/> shows at frame <paramref name="n"/> of <paramref name="grid"/>:
    /// the frame holding its sample point (D009 / D022), by its nominal rate.</summary>
    public static long Shown(VideoClip clip, long n, FrameRate grid, FrameRate sourceRate)
    {
        var point = SourceFrameSelector.SamplePoint(clip.TimelineStart, clip.SourceIn, clip.Speed, n, grid, sourceRate);
        var numerator = point.TicksNumerator * sourceRate.Numerator;
        var denominator = (Int128)point.TicksDenominator * TimeSpan.TicksPerSecond * sourceRate.Denominator;
        return (long)(numerator >= 0 ? numerator / denominator : -((-numerator + denominator - 1) / denominator));
    }

    /// <summary>The source time <paramref name="clip"/> shows at timeline time <paramref name="time"/> (down to a tick).</summary>
    public static MediaTime SourceAt(VideoClip clip, MediaTime time)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        var bt = (Int128)b * clip.SourceIn.Ticks + (Int128)a * (time.Ticks - clip.TimelineStart.Ticks);
        return new MediaTime(checked((long)FloorDiv(bt, b)));
    }

    /// <summary>The first timeline time at which <paramref name="clip"/> shows source time <paramref name="source"/>.</summary>
    public static MediaTime TimeOf(VideoClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        var offset = CeilingDiv((Int128)b * (source.Ticks - clip.SourceIn.Ticks), a);
        return new MediaTime(checked((long)((Int128)clip.TimelineStart.Ticks + offset)));
    }

    private static bool Faster(FrameRate a, FrameRate b) => (Int128)a.Numerator * b.Denominator > (Int128)b.Numerator * a.Denominator;

    private static Int128 FloorDiv(Int128 n, long d) => n >= 0 ? n / d : -((-n + d - 1) / d);

    private static Int128 CeilingDiv(Int128 n, long d) => n >= 0 ? (n + d - 1) / d : -(-n / d);
}
