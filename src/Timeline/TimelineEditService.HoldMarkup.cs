using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- Hold markup (D034 §5, §6; D053 — per route) -----------------------------------------------------------------------
// A route's markup is an immutable value on a video clip; every edit replaces it as a whole (one undo step with absolute
// before / after values). The route colour and the keyline are resolved here from the samples stored with the holds
// (RouteColor) — this service never decodes a frame. Timing is untouched, so no timeline validation is needed.
// D053: every edit names its route (routeId); without one it works on the clip's only route — refused when the clip has
// several. Adding holds to a route that has no markup yet (a new route, or one with its green only) creates it.
public sealed partial class TimelineEditService
{
    public TimelineEditResult AddHold(Guid clipId, Hold hold, Guid? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(hold);
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (HoldMarkupRules.ValidateHold(hold) is { } holdError) return TimelineEditResult.Fail(holdError);
        if (MarkupRoute(clip, routeId, create: true, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);

        var current = before ?? new HoldMarkup { Id = id };
        if (current.Holds.Any(h => h.Id == hold.Id)) return TimelineEditResult.Fail(Messages.Holds_AlreadyMarked);
        if (current.Holds.Length >= HoldMarkupRules.MaxHolds) return TimelineEditResult.Fail(string.Format(Messages.Holds_MaxHolds, HoldMarkupRules.MaxHolds));

        return Apply(clip, id, before, RouteColor.WithResolved(current with { Holds = current.Holds.Add(hold) }), HoldMarkupFields.Holds);
    }

    public TimelineEditResult AddHolds(Guid clipId, IEnumerable<Hold> holds, Guid? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(holds);
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (MarkupRoute(clip, routeId, create: true, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);

        var current = before ?? new HoldMarkup { Id = id };
        var added = current.Holds.ToBuilder();
        var ids = new HashSet<Guid>(added.Select(h => h.Id));
        foreach (var hold in holds)
        {
            if (hold is null) continue;
            if (hold.Id == Guid.Empty || !ids.Add(hold.Id)) continue;            // skip duplicates
            if (HoldMarkupRules.ValidateHold(hold) is { } holdError) continue;   // skip invalid
            if (added.Count >= HoldMarkupRules.MaxHolds) break;
            added.Add(hold);
        }

        if (added.Count == current.Holds.Length) return TimelineEditResult.Unchanged();
        return Apply(clip, id, before, RouteColor.WithResolved(current with { Holds = added.ToImmutable() }), HoldMarkupFields.Holds);
    }

    public TimelineEditResult RemoveHold(Guid clipId, Guid holdId, Guid? routeId = null)
    {
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        // Without a route the hold's own route (its identity is unique on the clip).
        routeId ??= clip.Routes.FirstOrDefault(r => r.Holds.Any(h => h.Id == holdId))?.Id;
        if (MarkupRoute(clip, routeId, create: false, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);
        var index = before is null ? -1 : before.Holds.IndexOf(before.Holds.FirstOrDefault(h => h.Id == holdId)!);
        if (before is null || index < 0) return TimelineEditResult.Fail(Messages.Holds_NotMarked);

        return Apply(clip, id, before, RouteColor.WithResolved(before with { Holds = before.Holds.RemoveAt(index) }), HoldMarkupFields.Holds);
    }

    public TimelineEditResult ReplaceHoldContour(Guid clipId, Guid holdId, ImmutableArray<NormalizedPoint> points, Guid? routeId = null)
    {
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        routeId ??= clip.Routes.FirstOrDefault(r => r.Holds.Any(h => h.Id == holdId))?.Id;
        if (MarkupRoute(clip, routeId, create: false, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);
        var index = before is null ? -1 : before.Holds.IndexOf(before.Holds.FirstOrDefault(h => h.Id == holdId)!);
        if (before is null || index < 0) return TimelineEditResult.Fail(Messages.Holds_NotMarked);

        var old = before.Holds[index];
        var corrected = new Hold(old.Id, points, old.SurfaceLab, old.WallLab, old.SourceTime);
        if (HoldMarkupRules.ValidateHold(corrected) is { } holdError) return TimelineEditResult.Fail(holdError);
        if (corrected.SameAs(old)) return TimelineEditResult.Unchanged();
        if (ContourEditing.IntroducesCrossing(old.Points, corrected.Points)) return TimelineEditResult.Fail(ContourEditing.CrossingMessage);

        // The samples stay as taken at the confirm (D034 §5), so the colour and the keyline are not resolved again.
        return Apply(clip, id, before, before with { Holds = before.Holds.SetItem(index, corrected) }, HoldMarkupFields.Contour);
    }

    public TimelineEditResult SetHoldStyle(Guid clipId, HoldStyle style, Guid? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (style.ManualColorHex is { } manual && !HoldMarkupRules.IsColorHex(manual))
            return TimelineEditResult.Fail(Messages.Holds_ColourFormat);
        if (MarkupRoute(clip, routeId, create: true, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);

        var current = before ?? new HoldMarkup { Id = id };
        var colorHex = style.ColorMode == HoldColorMode.Manual ? (style.ManualColorHex ?? current.ColorHex).ToUpperInvariant() : current.ColorHex;
        var restyled = current with
        {
            StrokeWidth1080 = style.StrokeWidth1080, ColorMode = style.ColorMode, ColorHex = colorHex, Keyline = style.Keyline
        };
        // Auto stays Auto: the stored colour stays (D034 §5 — recomputed only when the set of holds changes); only the
        // keyline is re-evaluated for it. Switching to Auto resolves the colour of the current set at once.
        var after = style.ColorMode == HoldColorMode.Auto && current.ColorMode == HoldColorMode.Auto && current.Holds.Length > 0
            ? RouteColor.WithKeyline(restyled)
            : RouteColor.WithResolved(restyled);

        var fields = HoldMarkupFields.None;
        if (!after.StrokeWidth1080.Equals(current.StrokeWidth1080)) fields |= HoldMarkupFields.StrokeWidth;
        if (after.ColorMode != current.ColorMode || !string.Equals(after.ColorHex, current.ColorHex, StringComparison.OrdinalIgnoreCase))
            fields |= HoldMarkupFields.Color;
        if (after.Keyline != current.Keyline) fields |= HoldMarkupFields.Keyline;
        if (fields == HoldMarkupFields.None) return TimelineEditResult.Unchanged();

        return Apply(clip, id, before, after, fields);
    }

    public TimelineEditResult SetHoldFollowsCamera(Guid clipId, bool follows, Guid? routeId = null)
    {
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (MarkupRoute(clip, routeId, create: false, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);
        if (before is null) return TimelineEditResult.Fail(Messages.Holds_NotMarked);
        if (before.FollowsCamera == follows) return TimelineEditResult.Unchanged();
        return Apply(clip, id, before, before with { FollowsCamera = follows }, HoldMarkupFields.FollowsCamera);
    }

    public TimelineEditResult SetHoldAnimation(Guid clipId, HoldAnimation? animation, Guid? routeId = null)
    {
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (animation is not null && HoldMarkupRules.ValidateAnimation(animation) is { } invalid) return TimelineEditResult.Fail(invalid);
        if (MarkupRoute(clip, routeId, create: true, out var id, out var before) is { } routeError) return TimelineEditResult.Fail(routeError);

        var current = before ?? new HoldMarkup { Id = id };
        var fields = AnimationFields(current.Animation, animation);
        if (fields == HoldMarkupFields.None) return TimelineEditResult.Unchanged();
        // D048 §3.4 / §4.2: a Duration animation (the orange) stays inside its clip — D053 §4: inside its route's green when
        // it has one; no anchored lilac over it.
        if (OrangeOutsideClip(clip, id, current with { Animation = animation }) is { } outside) return TimelineEditResult.Fail(outside);
        if (AnchoredAnimationConflict(clip, id, current with { Animation = animation }) is { } overlap) return TimelineEditResult.Fail(overlap);
        // Only the timing of the outlines changes (D036): the colour and the keyline stay as resolved.
        return Apply(clip, id, before, current with { Animation = animation }, fields);
    }

    /// <summary>Which animation settings differ (D036 §3 — the unit of undo merging).</summary>
    private static HoldMarkupFields AnimationFields(HoldAnimation? a, HoldAnimation? b)
    {
        if (a is null || b is null) return (a is null) == (b is null) ? HoldMarkupFields.None : HoldMarkupFields.AnimationOn;
        var fields = HoldMarkupFields.None;
        if (a.Order != b.Order) fields |= HoldMarkupFields.AnimationOrder;
        if (a.StartSourceTime != b.StartSourceTime) fields |= HoldMarkupFields.AnimationStart;
        if (a.Interval != b.Interval) fields |= HoldMarkupFields.AnimationInterval;
        if (a.AppearFade != b.AppearFade) fields |= HoldMarkupFields.AnimationAppearFade;
        if (a.StayAfterLast != b.StayAfterLast || a.StayUntilClipEnd != b.StayUntilClipEnd) fields |= HoldMarkupFields.AnimationStay;
        if (a.DisappearFade != b.DisappearFade) fields |= HoldMarkupFields.AnimationDisappearFade;
        if (a.Mode != b.Mode || a.Duration != b.Duration) fields |= HoldMarkupFields.AnimationDuration;
        return fields;
    }

    /// <summary>Validates the route's new markup and executes it as one command; unchanged values are no step.</summary>
    private TimelineEditResult Apply(VideoClip clip, Guid routeId, HoldMarkup? before, HoldMarkup after, HoldMarkupFields fields)
    {
        after = after with { Id = routeId };
        if (HoldMarkupRules.Validate(after) is { } invalid) return TimelineEditResult.Fail(invalid);
        if (before is not null && after.SameAs(before)) return TimelineEditResult.Unchanged();

        var command = new SetHoldMarkupCommand(clip, routeId, before, after, fields);
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>
    /// D053: the route an edit of <paramref name="clip"/> works on — <paramref name="routeId"/> when given (with
    /// <paramref name="create"/> a route without markup yet is allowed: a new route, or one with its green only); otherwise
    /// the clip's only route (its markup or its anchored green), a new one when it has none and <paramref name="create"/>.
    /// Null when found, otherwise why not: several routes and none named.
    /// </summary>
    private string? MarkupRoute(VideoClip clip, Guid? routeId, bool create, out Guid id, out HoldMarkup? markup)
    {
        id = Guid.Empty;
        markup = null;
        if (routeId is { } named)
        {
            id = named;
            markup = RouteOverlays.Find(clip, named);
            return markup is null && !create ? Messages.Holds_NotMarked : null;
        }
        var known = RouteOverlays.RouteIds(clip, RouteOverlays.AnchoredTo(clip, Sequence.VideoTracks.SelectMany(t => t.Clips)));
        if (known.Count > 1) return Messages.Route_Ambiguous;
        if (known.Count == 1)
        {
            id = known[0];
            markup = RouteOverlays.Find(clip, id);
            return markup is null && !create ? Messages.Holds_NotMarked : null;
        }
        if (!create) return Messages.Holds_NotMarked;
        id = Guid.NewGuid();
        return null;
    }

    /// <summary>Null when <paramref name="clipId"/> is a video clip on an unlocked track, otherwise why not.</summary>
    private string? MarkupClip(Guid clipId, out VideoClip clip)
    {
        clip = null!;
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return resolveError;
        if (clips[0] is not VideoClip video) return Messages.Holds_OnlyVideo;
        var track = plan.TrackOf(video);
        if (track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
        clip = video;
        return null;
    }
}
