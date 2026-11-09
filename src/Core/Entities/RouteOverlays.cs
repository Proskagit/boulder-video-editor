using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// D053: the routes of a video clip addressed by identity. A route is an id on a clip: its holds, outline style and
/// animation (the orange), when it has them, are the clip's markup with that id (<see cref="VideoClip.Routes"/>); its green
/// and lilac, its name and grade are the «Боулдер» anchored to it (<see cref="TemplateClip.AnchorRouteId"/>). Either may
/// exist without the other — a route made by the FIT import has only its green, a route marked without a layout only its
/// holds.
/// </summary>
public static class RouteOverlays
{
    /// <summary>The markup of route <paramref name="routeId"/> on <paramref name="clip"/>, or null.</summary>
    public static HoldMarkup? Find(VideoClip clip, Guid routeId) => Find(clip.Routes, routeId);

    public static HoldMarkup? Find(ImmutableArray<HoldMarkup> routes, Guid routeId)
    {
        if (routes.IsDefaultOrEmpty) return null;
        foreach (var route in routes)
            if (route.Id == routeId) return route;
        return null;
    }

    /// <summary><paramref name="routes"/> with route <paramref name="routeId"/>'s markup replaced by <paramref name="after"/>
    /// (null: removed); one not there yet is inserted at <paramref name="index"/> (the end by default). The order of the
    /// others is kept.</summary>
    public static ImmutableArray<HoldMarkup> Replace(ImmutableArray<HoldMarkup> routes, Guid routeId, HoldMarkup? after, int? index = null)
    {
        if (routes.IsDefault) routes = ImmutableArray<HoldMarkup>.Empty;
        if (after is not null && after.Id != routeId) after = after with { Id = routeId };
        for (var i = 0; i < routes.Length; i++)
            if (routes[i].Id == routeId)
                return after is null ? routes.RemoveAt(i) : routes.SetItem(i, after);
        if (after is null) return routes;
        return routes.Insert(Math.Clamp(index ?? routes.Length, 0, routes.Length), after);
    }

    /// <summary>The route <paramref name="template"/> belongs to on <paramref name="clip"/>, or null: its
    /// <see cref="TemplateClip.AnchorRouteId"/>, else the clip's single markup.</summary>
    public static Guid? RouteIdOf(TemplateClip template, VideoClip clip) => RouteIdOf(template.AnchorRouteId, clip.Routes);

    public static Guid? RouteIdOf(Guid? anchorRouteId, ImmutableArray<HoldMarkup> routes)
    {
        if (anchorRouteId is { } id) return id;
        return !routes.IsDefault && routes.Length == 1 ? routes[0].Id : null;
    }

    /// <summary>The markup of the route <paramref name="template"/> belongs to on <paramref name="clip"/>, or null.</summary>
    public static HoldMarkup? RouteOf(TemplateClip template, VideoClip clip) =>
        RouteIdOf(template, clip) is { } id ? Find(clip, id) : null;

    /// <summary>
    /// D056 §4: the computed start <c>T</c> of the route <paramref name="template"/> is the green of, as a source time of its
    /// anchored <paramref name="clip"/> — the stored one, else (an older file) the end of its route's Duration animation
    /// (D048 §3.4: the orange ends at <c>T</c>); null when neither is known.
    /// </summary>
    public static MediaTime? RouteStartOf(TemplateClip template, VideoClip clip, FrameRate rate)
    {
        if (template.RouteStart is { } stored) return stored;
        if (RouteOf(template, clip)?.Animation is not { Mode: HoldAnimationMode.Duration } animation) return null;
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, animation.StartSourceTime, rate);
        return HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, start + HoldAnimationRule.DurationFrames(animation, rate), rate);
    }

    /// <summary>The templates anchored to <paramref name="clip"/> among <paramref name="clips"/>.</summary>
    public static IEnumerable<TemplateClip> AnchoredTo(VideoClip clip, IEnumerable<Clip> clips) =>
        clips.OfType<TemplateClip>().Where(t => t.AnchorClipId == clip.Id);

    /// <summary>Every route id known on <paramref name="clip"/>: its markups' and those of the templates anchored to it.</summary>
    public static IReadOnlyList<Guid> RouteIds(VideoClip clip, IEnumerable<TemplateClip> anchored)
    {
        var ids = new List<Guid>();
        if (!clip.Routes.IsDefault) ids.AddRange(clip.Routes.Select(r => r.Id));
        foreach (var template in anchored)
            if (RouteIdOf(template, clip) is { } id && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// D053: the route a markup started on timeline frame <paramref name="frame"/> of <paramref name="clip"/> works on — the
    /// route whose green (its anchored «Боулдер») contains the frame, the latest started on an overlap; otherwise a route
    /// that has holds but no green yet (marked before its layout), the last one; otherwise null — a new route.
    /// </summary>
    public static Guid? RouteAt(VideoClip clip, IEnumerable<TemplateClip> anchored, long frame, FrameRate rate)
    {
        var templates = anchored.Where(t => t.Kind == TemplateKind.Boulder).ToList();
        return GreenAt(clip, templates, frame, rate) ?? MarkedWithoutGreen(clip, templates);
    }

    /// <summary>The route whose green contains timeline frame <paramref name="frame"/> (the latest started on an overlap),
    /// or null.</summary>
    public static Guid? GreenAt(VideoClip clip, IEnumerable<TemplateClip> anchored, long frame, FrameRate rate) =>
        anchored.Where(t => t.Kind == TemplateKind.Boulder)
            .Where(t => t.TimelineStart.ToNearestFrame(rate) <= frame && frame < t.TimelineEnd.ToNearestFrame(rate))
            .OrderByDescending(t => t.TimelineStart)
            .Select(t => RouteIdOf(t, clip))
            .FirstOrDefault(id => id is not null);

    /// <summary>The last route of <paramref name="clip"/> that has holds but no green yet (marked before its layout), or null.</summary>
    public static Guid? MarkedWithoutGreen(VideoClip clip, IEnumerable<TemplateClip> anchored)
    {
        var withGreen = anchored.Where(t => t.Kind == TemplateKind.Boulder).Select(t => RouteIdOf(t, clip)).OfType<Guid>().ToHashSet();
        return clip.Routes.IsDefaultOrEmpty ? null : clip.Routes.LastOrDefault(r => !withGreen.Contains(r.Id))?.Id;
    }

    /// <summary>
    /// D056 §3: the routes of <paramref name="clip"/> made from the FIT (their green a «Боулдер» with a FIT climb), nearest
    /// to timeline frame <paramref name="frame"/> first — each with its climb's number and its distance in frames (0 inside
    /// its green; of a split green the nearest part).
    /// </summary>
    public static IReadOnlyList<(Guid RouteId, int ClimbNumber, long Distance)> FitRoutesNear(VideoClip clip, IEnumerable<TemplateClip> anchored,
        long frame, FrameRate rate) =>
        anchored.Where(t => t.Kind == TemplateKind.Boulder && t.FitClimb is not null)
            .Select(t =>
            {
                long start = t.TimelineStart.ToNearestFrame(rate), end = t.TimelineEnd.ToNearestFrame(rate);
                var distance = frame < start ? start - frame : frame >= end ? frame - end + 1 : 0;
                return (Id: RouteIdOf(t, clip), Number: t.FitClimb!.Value.ClimbNumber, Distance: distance);
            })
            .Where(r => r.Id is not null)
            .GroupBy(r => r.Id!.Value)
            .Select(g => (g.Key, g.First().Number, g.Min(r => r.Distance)))
            .OrderBy(r => r.Item3).ThenBy(r => r.Number)
            .ToList();
}
