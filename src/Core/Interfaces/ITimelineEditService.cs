using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
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

    /// <summary>Adds a text clip with the default text properties ("Text", Segoe UI 48, white,
    /// centred) and a 5 s duration on the topmost video track, starting at <paramref name="start"/>
    /// (snapped to the frame grid). Rejected — nothing changes — when that track is locked, the
    /// clip would overlap another clip there, or the timeline has no video track. One Undo step.</summary>
    TimelineEditResult AddTextClip(MediaTime start);

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

    /// <summary>Ripple delete (D027 §2): removes the clips, and on each track that loses one every other clip that starts
    /// at or after the end of a removed clip moves left by the length of the removed clips that end at or before its
    /// start — no gap is left where they were, no overlap can arise. Clips on other tracks, the playhead and the markers
    /// stay. A removed clip's dissolves go with it (with D025's status note); every other dissolve keeps its length and
    /// zone; none is created where clips now meet; fades are unchanged. One Undo step. Rejected — nothing changes — when
    /// a clip's track is locked or the result fails the timeline's validation.</summary>
    TimelineEditResult RippleDeleteClips(IReadOnlyCollection<Guid> clipIds);

    /// <summary>Close gap (D027 §2): removes the empty span of <paramref name="trackId"/> that contains
    /// <paramref name="at"/> — between two clips, or before the first one — by moving every clip of that track from the
    /// gap's end on left by the gap's length. Only an existing gap: rejected when <paramref name="at"/> lies in a clip or
    /// after the track's last clip, or the track is locked. Dissolves move with their clips unchanged. One Undo
    /// step.</summary>
    TimelineEditResult CloseGap(Guid trackId, MediaTime at);

    /// <summary><see cref="CloseGap"/> for the gap right before <paramref name="clipId"/> on its track; rejected when the
    /// clip starts at 0 or right where another clip ends.</summary>
    TimelineEditResult CloseGapBefore(Guid clipId);

    /// <summary>Copy (D027 §5): detached copies of the clips — timing, speed, source range, properties, text, fades,
    /// the media they refer to (never the file) and the track each came from; no dissolve. Later edits of the clips
    /// don't change it. Not a project change (no Undo step); allowed from a locked track. Null when nothing could be
    /// copied (no clip given, or one no longer exists).</summary>
    TimelineClipboard? CopyClips(IReadOnlyCollection<Guid> clipIds);

    /// <summary>Paste (D027 §5): new clips (new ids) from <paramref name="clipboard"/>, the earliest starting at
    /// <paramref name="at"/> (snapped to the frame grid) and the others at their copied distances from it, each on the
    /// track it was copied from. Rejected as a whole — nothing pasted — when a clip would overlap another or break a
    /// rule: its track no longer exists or is locked, its media is no longer in the project, the project frame rate
    /// changed since the copy. Offline media is pasted like any other (the clip refers to the same asset). One Undo
    /// step; <see cref="TimelineEditResult.ClipIds"/> are the new clips.</summary>
    TimelineEditResult PasteClips(TimelineClipboard clipboard, MediaTime at);

    /// <summary>Duplicate (D027 §5): copies the clips and pastes them in one step right after them — the earliest copy
    /// starts where the last of them ends, every copy on its clip's track, the distances kept. Rejected as
    /// <see cref="PasteClips"/> is (an overlap, a locked track). One Undo step.</summary>
    TimelineEditResult DuplicateClips(IReadOnlyCollection<Guid> clipIds);

    /// <summary>Adds a marker (D027 §6) at <paramref name="at"/>, snapped to the frame grid, with the model's default
    /// label and colour. Rejected when a marker is already on that frame. One Undo step; saved in the project (v3).
    /// <see cref="TimelineEditResult.MarkerId"/> is the new marker.</summary>
    TimelineEditResult AddMarker(MediaTime at);

    /// <summary>Removes the marker on the frame of <paramref name="at"/>. Rejected when there is none. One Undo
    /// step.</summary>
    TimelineEditResult RemoveMarkerAt(MediaTime at);

    /// <summary>The position of the first marker on a frame after the frame of <paramref name="from"/>, or null.</summary>
    MediaTime? NextMarker(MediaTime from);

    /// <summary>The position of the last marker on a frame before the frame of <paramref name="from"/>, or null.</summary>
    MediaTime? PreviousMarker(MediaTime from);

    TimelineEditResult AddTrack(TrackType type);

    /// <summary>Deletes a track together with its clips and dissolves (D027 §3) as one Undo step; Undo puts the same
    /// track back at its place in the list, with its order, name, flags, clips and dissolves. Rejected — nothing
    /// changes — when the track is locked or is the only track of the timeline. Asking the user first when the track
    /// has clips is the caller's part.</summary>
    TimelineEditResult DeleteTrack(Guid trackId);

    /// <summary>Null when <see cref="DeleteTrack"/> would delete the track now, otherwise the reason it would refuse
    /// (so the user is not asked to confirm a deletion that can't happen).</summary>
    string? GetDeleteTrackBlockReason(Guid trackId);

    /// <summary>Moves a track one place among the tracks of its kind (D027 §3) by swapping its
    /// <see cref="Track.Order"/> with the neighbouring track (equal orders: the tracks of that kind are numbered anew
    /// in the new order): <paramref name="direction"/> +1 towards the higher order (video: composited above), −1
    /// towards the lower. Only the order changes — no clip, timing or dissolve.
    /// One Undo step. Rejected when the track or that neighbour is locked; <see cref="TimelineEditResult.NoChange"/>
    /// when there is no neighbour in that direction.</summary>
    TimelineEditResult MoveTrack(Guid trackId, int direction);

    /// <summary>Mutes or unmutes a track (D030 §4): a muted track gives no sound — an audio track's clips, a video track's
    /// video clips — in the Preview and the export; its picture and the sequence length are unchanged. One Undo step;
    /// allowed on a locked track (the lock protects the clips, not the monitoring state);
    /// <see cref="TimelineEditResult.NoChange"/> when the track already has that state.</summary>
    TimelineEditResult SetTrackMuted(Guid trackId, bool muted);

    /// <summary>Hides or shows a video track (D030 §4): a hidden track draws nothing — clips, texts, dissolves — in the
    /// Preview and the export; its video clips still sound unless the track is muted, and the sequence length is
    /// unchanged. One Undo step; allowed on a locked track. Rejected for an audio track (it has no picture).</summary>
    TimelineEditResult SetTrackHidden(Guid trackId, bool hidden);

    /// <summary>Locks or unlocks a track (D030 §4): every edit of a locked track's clips or of the track itself (move,
    /// delete) is refused while it is locked; playback and the export are unchanged. One Undo step. The lock is checked
    /// when an edit is planned — Undo / Redo of earlier steps are not blocked by it.</summary>
    TimelineEditResult SetTrackLocked(Guid trackId, bool locked);

    /// <summary>How many clips of the timeline use the media asset (any track, also hidden, muted or locked).</summary>
    int CountClipsUsing(Guid mediaAssetId);

    /// <summary>Null when <see cref="RemoveMedia"/> would remove the asset now, otherwise the reason it would refuse:
    /// the asset is not in the project, or a clip using it is on a locked track.</summary>
    string? GetRemoveMediaBlockReason(Guid mediaAssetId);

    /// <summary>Removes a media asset from the project together with every clip that uses it and their dissolves
    /// (D027 §4), as one Undo step; Undo puts the same asset object back at its place in the media list — with its id,
    /// path, metadata and analysis state — and the clips and dissolves exactly. The file on disk is never touched.
    /// Rejected — nothing changes — for the reasons of <see cref="GetRemoveMediaBlockReason"/>. Asking the user first
    /// when clips use the asset is the caller's part.</summary>
    TimelineEditResult RemoveMedia(Guid mediaAssetId);

    /// <summary>Sets absolute property values of one clip: every group given in
    /// <paramref name="change"/> replaces the clip's current values of that group exactly (no
    /// rounding or recomputation). Rejected when a group doesn't apply to the clip's kind, a value
    /// is out of range (<see cref="ClipPropertyLimits"/>) or the clip's track is locked. Changing
    /// nothing is <see cref="TimelineEditResult.NoChange"/>. Consecutive changes of the same
    /// properties of the same clip are merged into one Undo step.</summary>
    TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change);

    /// <summary>
    /// Changes the project canvas (D028, Step 13.4) to <paramref name="width"/> × <paramref name="height"/> as one Undo
    /// step. Refused, changing nothing, when the size breaks <see cref="ProjectSettingsRules.CanvasError"/>; the same
    /// size is <see cref="TimelineEditResult.NoChange"/>. The values kept in canvas pixels follow the canvas (CS-1 B): with
    /// <c>s = min(width / oldWidth, height / oldHeight)</c> — the factor by which D018's "contain" fits the old canvas into
    /// the new one — <c>PositionX / PositionY</c> of every video, image and text clip and the <c>FontSize</c> of every text
    /// clip are multiplied by <c>s</c>, on every track (locked and hidden too); nothing else changes (scale, rotation,
    /// crop, opacity, timing, fades, dissolves, markers). The whole change is refused when a scaled value leaves
    /// <see cref="ClipPropertyLimits"/> (no clamping); the message names the clip and its track. Undo / Redo write back
    /// the stored values. A change of the size and back again need not restore the old values (s is the "contain"
    /// factor both ways) — only Undo does.
    /// </summary>
    TimelineEditResult SetCanvasSize(int width, int height);

    /// <summary>
    /// Changes the project frame rate (D028, Step 13.5) to <paramref name="rate"/> — one of
    /// <see cref="ProjectSettingsRules.SelectableFrameRates"/> — as one Undo step, and locks it
    /// (<see cref="ProjectSettings.IsFrameRateLocked"/>), so a later first video no longer sets it. The timeline is
    /// re-gridded by D007's rule (<c>FrameRateRegrid</c>) on every track, locked and hidden ones too: each clip edge to its
    /// nearest frame of the new grid, a clip that would collapse grows by one frame into free space, a clip past its source
    /// is shortened to the whole frames the source has, a clip with a speed keeps its source range and speed; fades and
    /// dissolves keep their time (their frames are derived anew), markers keep their <see cref="MediaTime"/>, the playhead
    /// goes to its nearest frame of the grid. Refused whole — nothing changed, no Undo step — when the rate is not offered,
    /// or the re-grid or the validation fails (clips that would overlap, a dissolve shorter than two frames, a zone that no
    /// longer fits, source handles that no longer suffice — checked for every dissolve). The current rate of an unlocked
    /// project only locks it (one Undo step); of a locked one it is <see cref="TimelineEditResult.NoChange"/>.
    /// </summary>
    TimelineEditResult SetFrameRate(FrameRate rate);

    /// <summary>
    /// Changes the canvas, the frame rate and the export settings together (D028, Steps 13.6 / 13.9) — the Project Settings
    /// dialog's Apply. <paramref name="rate"/> null keeps the current rate as it is (also a provisional one);
    /// <paramref name="export"/> null keeps the export settings. Every part is checked first
    /// (<see cref="ProjectSettingsRules.CanvasError"/>, the offered rates, <see cref="ExportEncoding.Validate"/>); one part
    /// changing alone goes to its own method: the canvas → <see cref="SetCanvasSize"/>, the rate (a different one, or the
    /// current one of an unlocked project) → <see cref="SetFrameRate"/>, the export settings → <see cref="SetExportSettings"/>;
    /// none → <see cref="TimelineEditResult.NoChange"/>. Several changing are one Undo step with every rule of each, all
    /// checked before the first change: a refusal of any part changes nothing. One notification; Undo / Redo write back the
    /// stored values (the playhead follows the grid).
    /// </summary>
    TimelineEditResult SetProjectSettings(int width, int height, FrameRate? rate, ExportEncoding? export = null);

    /// <summary>
    /// Sets the project's export settings (D028, Step 13.9; EX-1): one Undo step, the project dirty; the same settings are
    /// <see cref="TimelineEditResult.NoChange"/>; settings that are not offered are refused. Exports made afterwards use them
    /// (<c>ExportPreflight</c> takes them into the job); the timeline doesn't change.
    /// </summary>
    TimelineEditResult SetExportSettings(ExportEncoding export);

    /// <summary>Changes the speed of a video or audio clip (D022). The start and the source range
    /// (SourceIn/SourceOut) stay; the duration becomes the whole number of frames the range allows at
    /// the new speed (<see cref="SpeedTiming.FramesFor"/>). Rejected without changes when the clip
    /// would be shorter than one frame, overlap the next clip, or its track is locked. Consecutive
    /// speed changes of the same clip merge into one Undo step.</summary>
    TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed);

    /// <summary>Adds a cross dissolve of <paramref name="duration"/> (whole frames, at least 2) on the cut between two
    /// touching clips of one unlocked video track (D025 §3–§5). Rejected, with nothing created, when the clips don't
    /// touch, the cut already has one, or the zone or the source handles can't hold it — the message then names the
    /// longest that fits. <see cref="TimelineEditResult.TransitionId"/> is the new dissolve.</summary>
    TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, MediaTime duration);

    /// <summary>Removes a dissolve (one Undo step).</summary>
    TimelineEditResult RemoveTransition(Guid transitionId);

    /// <summary>Changes a dissolve's length (whole frames, at least 2), within what the zone and the handles allow.
    /// Consecutive changes of the same dissolve merge into one Undo step.</summary>
    TimelineEditResult SetTransitionDuration(Guid transitionId, MediaTime duration);

    /// <summary>The longest dissolve, in frames, the cut between <paramref name="leftClipId"/> and
    /// <paramref name="rightClipId"/> can take now (its own dissolve, if any, not counted); null when the two aren't
    /// touching clips of one video track. Below 2 no dissolve fits.</summary>
    long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId);

    /// <summary>Finds the snap target nearest to any of <paramref name="candidates"/>
    /// within <paramref name="tolerance"/>. Targets: time zero, the playhead, every marker (D027 §6) and every
    /// clip edge except those of <paramref name="excludedClipIds"/>.</summary>
    SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds);
}

public enum ClipEdge
{
    Start,
    End
}

/// <summary>New values for <see cref="ITimelineEditService.SetClipProperties"/>. A null group is
/// left as it is; a given group is applied as a whole (read the current values with
/// <see cref="VisualProperties.Of"/> etc. and change the fields that should change).</summary>
public sealed record ClipPropertyChange
{
    /// <summary>Video, image and text clips.</summary>
    public VisualProperties? Visual { get; init; }

    /// <summary>Video and audio clips.</summary>
    public AudioProperties? Audio { get; init; }

    /// <summary>Text clips.</summary>
    public TextProperties? Text { get; init; }

    /// <summary>Every clip kind (D025). Each value is a whole number of frames of the project rate, at most the clip's
    /// length; it is stored as exactly that many frames.</summary>
    public FadeProperties? Fade { get; init; }
}

/// <summary>What <see cref="ITimelineEditService.CopyClips"/> took (D027 §5): detached copies of clips — never part of a
/// project — with the track each came from, and the project frame rate they were copied at. Only the edit service reads
/// the copies; the UI keeps the clipboard for the session of one project.</summary>
public sealed class TimelineClipboard
{
    public TimelineClipboard(FrameRate frameRate, FrameSize canvas, IReadOnlyList<TimelineClipboardEntry> entries)
    {
        FrameRate = frameRate;
        Canvas = canvas;
        Entries = entries;
    }

    public FrameRate FrameRate { get; }

    /// <summary>The project canvas when the clips were copied: their positions and font sizes are pixels of it, so a paste
    /// after a canvas change is refused (D028, Step 13.4 B-4), as one after a frame-rate change (D027 §5).</summary>
    public FrameSize Canvas { get; }
    public IReadOnlyList<TimelineClipboardEntry> Entries { get; }
    public int Count => Entries.Count;
}

/// <summary>One copied clip (a detached copy, never inserted itself) and the id of the track it was copied from.</summary>
public sealed record TimelineClipboardEntry(Clip Clip, Guid TrackId);

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

    /// <summary>The dissolve created or changed by the operation, if any.</summary>
    public Guid? TransitionId { get; init; }

    /// <summary>The marker created or removed by the operation, if any.</summary>
    public Guid? MarkerId { get; init; }

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
