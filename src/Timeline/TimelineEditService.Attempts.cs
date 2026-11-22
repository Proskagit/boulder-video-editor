using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

/// <summary>D074 amendment 1: a route's attempts stated from «Итоги» — on its «Боулдер» plates.</summary>
public sealed partial class TimelineEditService
{
    public TimelineEditResult SetRouteAttempts(IReadOnlyList<Guid> plateIds, int? attempts)
    {
        if (attempts is { } n && (n < TemplateRules.MinAttempts || n > TemplateRules.MaxAttempts))
            return TimelineEditResult.Fail(Messages.Template_AttemptsRange);
        var plan = new EditPlan(Sequence, Settings);
        if (plateIds.Count == 0) return TimelineEditResult.Unchanged();
        if (Resolve(plan, plateIds, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var plates = plateIds.Select(id => clips.OfType<TemplateClip>().FirstOrDefault(c => c.Id == id && c.Kind == TemplateKind.Boulder)).ToList();
        if (plates.Any(p => p is null)) return TimelineEditResult.Unchanged();
        if (CheckEditable(plan, plates!) is { } locked) return TimelineEditResult.Fail(locked);

        // The last attempt states the number (the board takes the last stated value); the earlier ones are cleared, so the
        // route has one number. Empty: every plate cleared — the attempts counted from the video again.
        var changed = false;
        for (var i = 0; i < plates.Count; i++)
        {
            var plate = plates[i]!;
            var wanted = i == plates.Count - 1 ? attempts : null;
            var fields = plan.TemplateFieldsOf(plate);
            if (fields.Attempts == wanted) continue;
            plan.SetTemplateFields(plate, fields with { Attempts = wanted });
            changed = true;
        }
        if (!changed) return TimelineEditResult.Unchanged();
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(error);
        // Its own step (a lone property command would merge with an earlier edit of the plate, D017).
        var command = new CompositeCommand("Change Attempts", new[] { plan.BuildCommand("Change Attempts") });
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(plates.Select(p => p!.Id).ToList());
    }
}
