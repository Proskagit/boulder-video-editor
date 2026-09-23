using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The only entry point for changing the timeline (<see cref="Project.Timeline"/>).
/// Every operation validates the complete result first, then applies it as a
/// single <see cref="IUndoableCommand"/> through <see cref="IUndoRedoService"/> —
/// a rejected operation changes nothing. All positions are kept exactly on the
/// project's frame grid (<see cref="ProjectSettings.FrameRate"/>).
/// </summary>
public interface ITimelineEditService
{
    /// <summary>Current project frame grid.</summary>
    FrameRate FrameRate { get; }

    /// <summary>Null when <paramref name="asset"/> can be added to the timeline now,
    /// otherwise a short user-facing reason (e.g. analysis still running).</summary>
    string? GetAddBlockReason(MediaAsset asset);

    /// <summary>Adds a clip for the asset. <paramref name="trackId"/> null = the first
    /// compatible track (V1 / A1); <paramref name="start"/> null = appended after the
    /// last clip on that track. An explicit start is snapped to the frame grid.
    /// Adding the first video fixes the project frame rate (see
    /// <see cref="ProjectSettings.IsFrameRateLocked"/>) in the same undo step.</summary>
    TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null);

    /// <summary>Moves clips by a whole number of frames. With <paramref name="targetTrackId"/>
    /// all clips must currently be on one track and move to the target track.</summary>
    TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null);

    /// <summary>Dry run of <see cref="MoveClips"/> for drag previews: null when the move
    /// would succeed (or change nothing), otherwise the reason it would be rejected.</summary>
    string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null);

    /// <summary>Moves one edge of a clip to <paramref name="edgeTime"/> (snapped to the
    /// frame grid), clamped to neighbours, the source media and a one-frame minimum.</summary>
    TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime);

    /// <summary>Dry run of <see cref="TrimClip"/> for drag previews: the clamped timing the
    /// clip would get, or null if the trim isn't allowed.</summary>
    (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime);

    /// <summary>Splits clips at <paramref name="at"/> (snapped to the frame grid). With a
    /// non-empty <paramref name="clipIds"/> only those clips are split; otherwise every
    /// clip under <paramref name="at"/> on an unlocked track.</summary>
    TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null);

    TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds);

    TimelineEditResult AddTrack(TrackType type);

    /// <summary>Finds the snap target nearest to any of <paramref name="candidates"/>
    /// within <paramref name="tolerance"/>. Targets: time zero, the playhead and every
    /// clip edge except those of <paramref name="excludedClipIds"/>.</summary>
    SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds);
}

public enum ClipEdge
{
    Start,
    End
}

/// <summary>Outcome of a timeline edit. <see cref="Message"/> is safe to show in the
/// status bar; on success it may carry an informational note (e.g. frame rate fixed).</summary>
public sealed class TimelineEditResult
{
    public required bool Success { get; init; }

    /// <summary>True when the operation was valid but changed nothing (no undo step).</summary>
    public bool NoChange { get; init; }

    public string? Message { get; init; }

    /// <summary>Clips created or changed by the operation (e.g. the new clip after Add,
    /// both halves after Split).</summary>
    public IReadOnlyList<Guid> ClipIds { get; init; } = Array.Empty<Guid>();

    public static TimelineEditResult Ok(IReadOnlyList<Guid>? clipIds = null, string? message = null) =>
        new() { Success = true, ClipIds = clipIds ?? Array.Empty<Guid>(), Message = message };

    public static TimelineEditResult Unchanged() => new() { Success = true, NoChange = true };

    public static TimelineEditResult Fail(string message) => new() { Success = false, Message = message };
}

/// <param name="Snapped">False when no target was within tolerance.</param>
/// <param name="Offset">What to add to the candidate to land on <paramref name="Target"/>.</param>
public readonly record struct SnapResult(bool Snapped, MediaTime Offset, MediaTime Target)
{
    public static SnapResult None => new(false, MediaTime.Zero, MediaTime.Zero);
}
