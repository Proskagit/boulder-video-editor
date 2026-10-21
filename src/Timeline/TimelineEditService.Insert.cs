using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

// --- Insert / Overwrite (D031 SQ6–SQ9, SQ12, SQ13, SQ15) ---------------------------------------------------------------
// Three-point editing from the Source viewer: a source range placed at a timeline point. Built only from the existing
// rules — the split (PlanSplitClip), the trim (TrimmedState), the move (ShiftedState), the first video's frame rate
// (PlanFirstVideoRate) — in one EditPlan, validated and committed like every other edit.
public sealed partial class TimelineEditService
{
    public TimelineEditResult InsertClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null,
        bool soundOnly = false) =>
        PlaceSourceRange(mediaAssetId, sourceIn, sourceOut, at, trackId, overwrite: false, soundOnly);

    public TimelineEditResult OverwriteClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null,
        bool soundOnly = false) =>
        PlaceSourceRange(mediaAssetId, sourceIn, sourceOut, at, trackId, overwrite: true, soundOnly);

    public SourceGrid? GetSourceGrid(MediaAsset asset, out string? reason)
    {
        reason = asset.Kind == MediaKind.Image
            ? Messages.Edit_ImagesNoRange
            : GetAddBlockReason(asset);
        if (reason is not null) return null;

        // SQ12: the rate PlanFirstVideoRate would give the project with this asset, or the project's.
        var rate = asset.Kind == MediaKind.Video && !Settings.IsFrameRateLocked ? ResolveSourceFrameRate(asset).Rate : Settings.FrameRate;
        var frames = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rate);
        if (frames < 1)
        {
            reason = string.Format(Messages.Edit_FileShorterThanFrame, asset.FileName);
            return null;
        }
        return new SourceGrid(rate, frames);
    }

    private TimelineEditResult PlaceSourceRange(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at,
        Guid? trackId, bool overwrite, bool soundOnly)
    {
        var verb = overwrite ? Messages.Verb_Overwrite : Messages.Verb_Insert;
        if (FindAsset(mediaAssetId) is not { } asset)
            return TimelineEditResult.Fail(Messages.Edit_MediaNotInProject);
        if (asset.Kind == MediaKind.Image)
            return TimelineEditResult.Fail(Messages.Edit_ImagesNoRange);
        if (GetAddBlockReason(asset) is { } blocked)
            return TimelineEditResult.Fail(blocked);

        // R7 / AQ10: the sound of a video file alone, as an audio clip of that file. Its grid is the Source viewer's (SQ12),
        // so a first video file's sound fixes the project rate as the file itself would (D007).
        var sound = soundOnly && asset.Kind == MediaKind.Video;
        if (sound && asset.Metadata?.AudioCodec is null)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_NoSound, asset.FileName));

        var plan = new EditPlan(Sequence, Settings);
        if (PlanFirstVideoRate(plan, asset, out var info) is { } regridError)
            return TimelineEditResult.Fail($"Can't {verb} {asset.FileName}: {regridError}");
        var rate = plan.Rate;

        var kind = asset.Kind == MediaKind.Audio || sound ? TrackType.Audio : TrackType.Video;
        Track? track;
        if (trackId is { } id)
        {
            track = FindTrack(id);
            if (track is null) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
            if (track.Type != kind)
                return TimelineEditResult.Fail(kind == TrackType.Audio ? Messages.Edit_AudioOnAudioTrack : Messages.Edit_VideoOnlyVideoTrack);
        }
        else
        {
            track = (kind == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks).FirstOrDefault();
            if (track is null) return TimelineEditResult.Fail(kind == TrackType.Video ? Messages.Edit_NoVideoTrack : Messages.Edit_NoAudioTrack);
        }
        if (track.IsLocked)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        // The range on the grid (SQ12): In defaults to the start, Out to the last whole frame of the source.
        var lastFrame = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rate);
        var inFrame = Math.Clamp(sourceIn?.ToNearestFrame(rate) ?? 0, 0, lastFrame);
        var outFrame = Math.Clamp(sourceOut?.ToNearestFrame(rate) ?? lastFrame, 0, lastFrame);
        var frames = outFrame - inFrame;
        if (frames < 1)
            return TimelineEditResult.Fail(Messages.Edit_RangeShorterThanFrame);
        var atFrame = Math.Max(0, at.ToNearestFrame(rate));

        // The new clip at 1× (SQ13): built at 0 and moved to the point by the move rule, which also keeps its source end
        // inside the media (the one-tick rule of a frame count's length at another position).
        var state = ClipState.FromFrames(0, frames, MediaTime.FromFrame(inFrame, rate), rate);
        Clip clip = sound ? new AudioClip { MediaAssetId = asset.Id } : CreateClip(asset, state);
        state.ApplyTo(clip);
        var (placed, placeError) = ShiftedState(clip, atFrame, rate);
        if (placeError is not null) return TimelineEditResult.Fail($"Can't {verb}: {placeError}");
        placed.ApplyTo(clip);

        // The track's clips as the plan leaves them so far (re-gridded when this edit fixes the frame rate).
        var existing = plan.EffectiveClips(track).ToList();
        var splits = new List<(Clip Left, Clip Right)>();
        var error = overwrite
            ? PlanOverwrite(plan, track, existing, atFrame, atFrame + frames, splits)
            : PlanInsert(plan, track, existing, atFrame, frames, splits);
        if (error is not null) return TimelineEditResult.Fail(error);

        // AQ13: a linked clip the edit splits or trims takes its partner along — the same insert / overwrite rule applied
        // to the partner alone on its own track (nothing is placed there); a removed clip's partner stays, its link cleared
        // (R5). The clips the insert only moves take theirs along by the move rule (Validate).
        foreach (var (covered, _) in existing)
        {
            var split = splits.Any(s => s.Left == covered);
            if (!split && (!overwrite || !plan.Changes(covered) || plan.Removes(covered))) continue;
            var (partner, partnerError) = FollowingPartner(plan, covered);
            if (partnerError is not null) return TimelineEditResult.Fail(partnerError);
            if (partner is null) continue;
            var partnerTrack = plan.TrackOf(partner);
            var partnerOnly = new[] { (Clip: (Clip)partner, State: ClipState.Capture(partner)) };
            if ((overwrite
                    ? PlanOverwrite(plan, partnerTrack, partnerOnly, atFrame, atFrame + frames, splits)
                    : PlanInsert(plan, partnerTrack, partnerOnly, atFrame, frames, splits)) is { } partnerPlanError)
                return TimelineEditResult.Fail(partnerPlanError);
        }
        LinkSplitParts(splits);

        plan.Insert(track, clip);
        if (Validate(plan) is { } invalid)
            return TimelineEditResult.Fail($"Can't {verb} {asset.FileName}: {invalid}");

        Commit(plan, overwrite ? "Overwrite Clip" : "Insert Clip");
        if (info is not null) _logger.LogInformation("{Message}", info);
        return TimelineEditResult.Ok(new[] { clip.Id }, Join(info, TransitionNote(plan)));
    }

    /// <summary>SQ6: a clip with <paramref name="atFrame"/> strictly inside is split there, and its right part with every
    /// clip from <paramref name="atFrame"/> on moves right by <paramref name="frames"/> — only on <paramref name="track"/>.
    /// Null when planned, otherwise the message.</summary>
    private string? PlanInsert(EditPlan plan, Track track, IReadOnlyList<(Clip Clip, ClipState State)> existing, long atFrame, long frames,
        List<(Clip Left, Clip Right)> splits)
    {
        var rate = plan.Rate;
        foreach (var (clip, state) in existing)
        {
            var start = state.Start.ToNearestFrame(rate);
            var end = state.End.ToNearestFrame(rate);
            if (start < atFrame && atFrame < end)
            {
                if (!PlanSplitClip(plan, track, clip, state, atFrame, out var right))
                    return Tr(track, ZoneAt(track, clip, state, atFrame, rate)?.TransitionTypeId,
                        Messages.Insert_InsideDissolve, Messages.Insert_InsideCrossfade, Messages.Insert_InsideNamed);
                splits.Add((clip, right));
                var (moved, error) = ShiftedState(right, frames, rate);
                if (error is not null) return string.Format(Messages.Edit_CantInsert, error);
                moved.ApplyTo(right);              // a planned insert, not in the model yet
            }
            else if (start >= atFrame)
            {
                var (moved, error) = ShiftedState(clip, state, frames, rate);
                if (error is not null) return string.Format(Messages.Edit_CantInsert, error);
                plan.Update(clip, track, moved);
            }
        }
        return null;
    }

    /// <summary>SQ9: [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="track"/> is cleared for the new
    /// clip — a clip inside it is removed, a clip partly inside is trimmed to it by the trim rule (which keeps the frames
    /// a dissolve at its other edge needs), a clip covering it is split at <paramref name="from"/> and its right part
    /// trimmed to start at <paramref name="to"/>. Null when planned, otherwise the message.</summary>
    private string? PlanOverwrite(EditPlan plan, Track track, IReadOnlyList<(Clip Clip, ClipState State)> existing, long from, long to,
        List<(Clip Left, Clip Right)> splits)
    {
        string Inside(Clip clip) =>
            Tr(track, EdgeTypeOf(track, clip), Messages.Overwrite_InsideDissolve, Messages.Overwrite_InsideCrossfade, Messages.Overwrite_InsideNamed);
        var rate = plan.Rate;
        var none = Array.Empty<Clip>();
        foreach (var (clip, state) in existing)
        {
            var start = state.Start.ToNearestFrame(rate);
            var end = state.End.ToNearestFrame(rate);
            if (end <= from || start >= to) continue;

            var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
            if (from <= start && end <= to)
            {
                plan.Remove(clip);
            }
            else if (start < from && to < end)
            {
                if (!PlanSplitClip(plan, track, clip, state, from, out var right))
                    return Inside(clip);
                splits.Add((clip, right));
                var rightAfter = TrimmedState(right, ClipState.Capture(right), none, ClipEdge.Start, to, Math.Max(1, partAtEnd), rate);
                if (rightAfter.Start.ToNearestFrame(rate) != to) return Inside(clip);
                rightAfter.ApplyTo(right);         // a planned insert, not in the model yet
            }
            else if (start < from)
            {
                var after = TrimmedState(clip, state, none, ClipEdge.End, from, Math.Max(1, partAtStart), rate);
                if (after.End.ToNearestFrame(rate) != from) return Inside(clip);
                plan.Update(clip, track, after);
            }
            else
            {
                var after = TrimmedState(clip, state, none, ClipEdge.Start, to, Math.Max(1, partAtEnd), rate);
                if (after.Start.ToNearestFrame(rate) != to) return Inside(clip);
                plan.Update(clip, track, after);
            }
        }
        return null;
    }
}
