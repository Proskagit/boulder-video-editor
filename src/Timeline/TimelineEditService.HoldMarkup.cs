using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- Hold markup (D034 §5, §6) ----------------------------------------------------------------------------------------
// The markup is an immutable value on a video clip; every edit replaces it as a whole (one undo step with absolute
// before / after values). The route colour and the keyline are resolved here from the samples stored with the holds
// (RouteColor) — this service never decodes a frame. Timing is untouched, so no timeline validation is needed.
public sealed partial class TimelineEditService
{
    public TimelineEditResult AddHold(Guid clipId, Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (HoldMarkupRules.ValidateHold(hold) is { } holdError) return TimelineEditResult.Fail(holdError);

        var before = clip.HoldMarkup;
        var current = before ?? new HoldMarkup();
        if (current.Holds.Any(h => h.Id == hold.Id)) return TimelineEditResult.Fail("That hold is already marked.");
        if (current.Holds.Length >= HoldMarkupRules.MaxHolds) return TimelineEditResult.Fail($"A clip can have at most {HoldMarkupRules.MaxHolds} holds.");

        return Apply(clip, before, RouteColor.WithResolved(current with { Holds = current.Holds.Add(hold) }), HoldMarkupFields.Holds);
    }

    public TimelineEditResult RemoveHold(Guid clipId, Guid holdId)
    {
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        var before = clip.HoldMarkup;
        var index = before is null ? -1 : before.Holds.IndexOf(before.Holds.FirstOrDefault(h => h.Id == holdId)!);
        if (before is null || index < 0) return TimelineEditResult.Fail("That hold is not marked on the clip.");

        return Apply(clip, before, RouteColor.WithResolved(before with { Holds = before.Holds.RemoveAt(index) }), HoldMarkupFields.Holds);
    }

    public TimelineEditResult SetHoldStyle(Guid clipId, HoldStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (MarkupClip(clipId, out var clip) is { } error) return TimelineEditResult.Fail(error);
        if (style.ManualColorHex is { } manual && !HoldMarkupRules.IsColorHex(manual))
            return TimelineEditResult.Fail("The outline colour must be #RRGGBB.");

        var before = clip.HoldMarkup;
        var current = before ?? new HoldMarkup();
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

        return Apply(clip, before, after, fields);
    }

    /// <summary>Validates the new markup and executes it as one command; unchanged values are no step.</summary>
    private TimelineEditResult Apply(VideoClip clip, HoldMarkup? before, HoldMarkup after, HoldMarkupFields fields)
    {
        if (HoldMarkupRules.Validate(after) is { } invalid) return TimelineEditResult.Fail(invalid);
        if (before is not null && after.SameAs(before)) return TimelineEditResult.Unchanged();

        var command = new SetHoldMarkupCommand(clip, before, after, fields);
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>Null when <paramref name="clipId"/> is a video clip on an unlocked track, otherwise why not.</summary>
    private string? MarkupClip(Guid clipId, out VideoClip clip)
    {
        clip = null!;
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return resolveError;
        if (clips[0] is not VideoClip video) return "Holds can be marked only on a video clip.";
        var track = plan.TrackOf(video);
        if (track.IsLocked) return $"Track {track.Name} is locked.";
        clip = video;
        return null;
    }
}
