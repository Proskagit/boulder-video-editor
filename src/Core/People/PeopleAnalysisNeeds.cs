using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.People;

/// <summary>
/// D061 amendment 1: which parts of a video a clip needs analysed, in source time — never the whole file for a short
/// part. The tracks: around each followed part (<see cref="TrackMargin"/> on each side: the glides, the person's
/// re-identification). The masks: where the clip's hold outlines are shown (<see cref="OutlineSpans"/>) while it hides them
/// behind people (<see cref="MaskMargin"/> on each side); nothing for a clip without outlines.
/// </summary>
public static class PeopleAnalysisNeeds
{
    public static readonly MediaTime TrackMargin = MediaTime.FromSeconds(1);
    public static readonly MediaTime MaskMargin = MediaTime.FromSeconds(0.5);

    /// <summary>The followed parts of <paramref name="clip"/> within it, widened by <see cref="TrackMargin"/> — and at the
    /// clip's edges by <paramref name="before"/> / <paramref name="after"/> (source time a Dissolve or a Push shows past the
    /// cut).</summary>
    public static IEnumerable<(MediaTime From, MediaTime To)> FollowSpans(VideoClip clip, MediaTime before = default, MediaTime after = default)
    {
        if (clip.Follow is not { } follow) yield break;
        foreach (var part in follow.Parts)
            if (part.RangeIn(clip.SourceIn, clip.SourceOut) is { } range)
                yield return Widened(clip, range, TrackMargin, before, after);
    }

    /// <summary>Where <paramref name="clip"/> needs masks: its outlines' spans widened by <see cref="MaskMargin"/> (and at the
    /// clip's edges by what a transition shows past the cut), when it hides them behind people.</summary>
    public static IEnumerable<(MediaTime From, MediaTime To)> MaskSpans(VideoClip clip, FrameRate rate, MediaTime before = default, MediaTime after = default) =>
        clip.HidesOutlinesBehindPeople
            ? OutlineSpans(clip, rate).Select(s => Widened(clip, s, MaskMargin, before, after))
            : Enumerable.Empty<(MediaTime, MediaTime)>();

    private static (MediaTime From, MediaTime To) Widened(VideoClip clip, (MediaTime From, MediaTime To) span, MediaTime margin, MediaTime before, MediaTime after) =>
        (span.From - margin - (span.From <= clip.SourceIn ? before : MediaTime.Zero),
         span.To + margin + (span.To >= clip.SourceOut ? after : MediaTime.Zero));

    /// <summary>The source time a transition of <paramref name="transitions"/> shows of <paramref name="clip"/> before its
    /// start and after its end (half the zone each side of the cut, at the clip's speed).</summary>
    public static (MediaTime Before, MediaTime After) PastTheCut(VideoClip clip, IEnumerable<Transition> transitions)
    {
        MediaTime Source(MediaTime timeline) => new((long)((Int128)timeline.Ticks * clip.Speed.Numerator / (2 * (Int128)clip.Speed.Denominator)));
        var list = transitions.ToList();
        var before = list.Where(t => t.RightClipId == clip.Id).Select(t => Source(t.Duration)).DefaultIfEmpty(MediaTime.Zero).Max();
        var after = list.Where(t => t.LeftClipId == clip.Id).Select(t => Source(t.Duration)).DefaultIfEmpty(MediaTime.Zero).Max();
        return (before, after);
    }

    /// <summary>
    /// The source time spans of <paramref name="clip"/> where a route's hold outline is drawn: a route with holds and no
    /// animation — the whole clip; with an animation (D036) — from its start to the last contour's disappearance (or the
    /// clip's end), within the clip.
    /// </summary>
    public static IEnumerable<(MediaTime From, MediaTime To)> OutlineSpans(VideoClip clip, FrameRate rate)
    {
        if (clip.Routes.IsDefaultOrEmpty) yield break;
        long first = clip.TimelineStart.ToNearestFrame(rate), end = clip.TimelineEnd.ToNearestFrame(rate);
        foreach (var route in clip.Routes)
        {
            if (HoldOutline.Of(route, clip.TimelineStart, clip.SourceIn, clip.Speed, rate) is not { } outline) continue;
            var from = Math.Max(first, outline.Schedule?.StartFrame ?? first);
            var to = Math.Min(end, outline.Schedule?.EndFrame ?? end);
            if (to <= from) continue;
            yield return (SourceAt(clip, from, rate), SourceAt(clip, to, rate));
        }
    }

    private static MediaTime SourceAt(VideoClip clip, long frame, FrameRate rate) =>
        HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, rate);

    /// <summary>The chunks (<see cref="PeopleAnalyzer.ChunkLength"/>) of a video of <paramref name="duration"/> that
    /// <paramref name="spans"/> touch.</summary>
    public static IEnumerable<int> Chunks(IEnumerable<(MediaTime From, MediaTime To)> spans, MediaTime duration)
    {
        var count = PeopleAnalyzer.ChunkCount(duration);
        foreach (var (from, to) in spans)
        {
            if (to <= from || to <= MediaTime.Zero) continue;
            var last = Math.Min(count - 1, PeopleAnalyzer.ChunkOf(to - new MediaTime(1)));
            for (var k = PeopleAnalyzer.ChunkOf(from < MediaTime.Zero ? MediaTime.Zero : from); k <= last; k++) yield return k;
        }
    }
}
