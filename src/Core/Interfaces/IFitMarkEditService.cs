using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The user's actions on the FIT marks (D051 §5.4–§5.7, §8.2), each one Undo step. A refusal leaves everything as it was
/// and says why. Proposals are made by the FIT import and by setting a video's <c>Z</c>
/// (<see cref="ITimelineEditService.SetRecordingZeroOverride"/>), not here.
/// </summary>
public interface IFitMarkEditService
{
    /// <summary>Moves the climb's mark to a source time of its video; the mark becomes <c>moved</c>.</summary>
    TimelineEditResult MoveFitMark(ClimbIdentity climb, MediaTime sourceTime);

    /// <summary>Hides the mark / declines the climb (the Delete key on a mark): the climb goes to the declined climbs with
    /// its mark as it was and without its link (D051 §5.7). A climb without a mark is declined without one.</summary>
    TimelineEditResult DeclineClimb(ClimbIdentity climb);

    /// <summary>«Вернуть»: the climb is active again — its former mark as it was, never with a link; a climb declined
    /// before any mark existed gets a new proposal if a video is chosen (D051 §5.5.1).</summary>
    TimelineEditResult RestoreClimb(ClimbIdentity climb);

    /// <summary>«Связать с FIT-подъёмом»: links an unlinked Duration hold animation (of any video) to an active climb
    /// without a link.</summary>
    TimelineEditResult LinkFitClimb(ClimbIdentity climb, Guid clipId, Guid? routeId = null);

    /// <summary>«Снять FIT-связь»: removes the climb's link; the animation is unchanged.</summary>
    TimelineEditResult UnlinkFitClimb(ClimbIdentity climb);

    /// <summary>«Разметить трассу от метки»: the D048 layout of the clip with <c>T</c> on the mark's frame and the clip
    /// linked to the climb, in one step.</summary>
    TimelineEditResult LayOutRouteFromMark(ClimbIdentity climb, Guid clipId, Guid? routeId = null);

    /// <summary>D053 §7 (mode B): one route with its green only for every active FIT climb without one, on the clip that
    /// shows its start — from the layout's start before it to its end plus 5 s; no holds, no animation, no status. One
    /// Undo step; with nothing new no step and a message.</summary>
    TimelineEditResult CreateRoutesFromFit();
}
