using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

// --- Insert / Overwrite (D031 SQ6–SQ9, SQ12, SQ13, SQ15) ---------------------------------------------------------------
// Three-point editing from the Source viewer: a source range placed at a timeline point. Built only from the existing
// rules — the split (PlanSplitClip), the trim (TrimmedState), the move (ShiftedState), the first video's frame rate
// (PlanFirstVideoRate) — in one EditPlan, validated and committed like every other edit.
public sealed partial class TimelineEditService
{
    public TimelineEditResult InsertClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null) =>
        PlaceSourceRange(mediaAssetId, sourceIn, sourceOut, at, trackId, overwrite: false);

    public TimelineEditResult OverwriteClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null) =>
        PlaceSourceRange(mediaAssetId, sourceIn, sourceOut, at, trackId, overwrite: true);

    private TimelineEditResult PlaceSourceRange(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at,
        Guid? trackId, bool overwrite)
    {
        var verb = overwrite ? "overwrite" : "insert";
        if (FindAsset(mediaAssetId) is not { } asset)
            return TimelineEditResult.Fail("That media is not in the project.");
        if (asset.Kind == MediaKind.Image)
            return TimelineEditResult.Fail("Images have no source range: add them with Add to Timeline.");
        if (GetAddBlockReason(asset) is { } blocked)
            return TimelineEditResult.Fail(blocked);

        var plan = new EditPlan(Sequence, Settings);
        if (PlanFirstVideoRate(plan, asset, out var info) is { } regridError)
            return TimelineEditResult.Fail($"Can't {verb} {asset.FileName}: {regridError}");
        var rate = plan.Rate;

        var kind = asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video;
        Track? track;
        if (trackId is { } id)
        {
            track = FindTrack(id);
            if (track is null) return TimelineEditResult.Fail("That track no longer exists.");
            if (track.Type != kind)
                return TimelineEditResult.Fail(kind == TrackType.Audio ? "Audio can only go on an audio track." : "Video can only go on a video track.");
        }
        else
        {
            track = (kind == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks).FirstOrDefault();
            if (track is null) return TimelineEditResult.Fail($"The timeline has no {(kind == TrackType.Video ? "video" : "audio")} track.");
        }
        if (track.IsLocked)
            return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        // The range on the grid (SQ12): In defaults to the start, Out to the last whole frame of the source.
        var lastFrame = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rate);
        var inFrame = Math.Clamp(sourceIn?.ToNearestFrame(rate) ?? 0, 0, lastFrame);
        var outFrame = Math.Clamp(sourceOut?.ToNearestFrame(rate) ?? lastFrame, 0, lastFrame);
        var frames = outFrame - inFrame;
        if (frames < 1)
            return TimelineEditResult.Fail("The source range is shorter than one frame.");
        var atFrame = Math.Max(0, at.ToNearestFrame(rate));

        // The new clip at 1× (SQ13): built at 0 and moved to the point by the move rule, which also keeps its source end
        // inside the media (the one-tick rule of a frame count's length at another position).
        var clip = CreateClip(asset, ClipState.FromFrames(0, frames, MediaTime.FromFrame(inFrame, rate), rate));
        var (placed, placeError) = ShiftedState(clip, atFrame, rate);
        if (placeError is not null) return TimelineEditResult.Fail($"Can't {verb}: {placeError}");
        placed.ApplyTo(clip);

        // The track's clips as the plan leaves them so far (re-gridded when this edit fixes the frame rate).
        var existing = plan.EffectiveClips(track).ToList();
        var error = overwrite
            ? PlanOverwrite(plan, track, existing, atFrame, atFrame + frames)
            : PlanInsert(plan, track, existing, atFrame, frames);
        if (error is not null) return TimelineEditResult.Fail(error);

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
    private string? PlanInsert(EditPlan plan, Track track, IReadOnlyList<(Clip Clip, ClipState State)> existing, long atFrame, long frames)
    {
        var rate = plan.Rate;
        foreach (var (clip, state) in existing)
        {
            var start = state.Start.ToNearestFrame(rate);
            var end = state.End.ToNearestFrame(rate);
            if (start < atFrame && atFrame < end)
            {
                if (!PlanSplitClip(plan, track, clip, state, atFrame, out var right))
                    return "Can't insert inside a dissolve.";
                var (moved, error) = ShiftedState(right, frames, rate);
                if (error is not null) return $"Can't insert: {error}";
                moved.ApplyTo(right);              // a planned insert, not in the model yet
            }
            else if (start >= atFrame)
            {
                var (moved, error) = ShiftedState(clip, state, frames, rate);
                if (error is not null) return $"Can't insert: {error}";
                plan.Update(clip, track, moved);
            }
        }
        return null;
    }

    /// <summary>SQ9: [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="track"/> is cleared for the new
    /// clip — a clip inside it is removed, a clip partly inside is trimmed to it by the trim rule (which keeps the frames
    /// a dissolve at its other edge needs), a clip covering it is split at <paramref name="from"/> and its right part
    /// trimmed to start at <paramref name="to"/>. Null when planned, otherwise the message.</summary>
    private string? PlanOverwrite(EditPlan plan, Track track, IReadOnlyList<(Clip Clip, ClipState State)> existing, long from, long to)
    {
        const string insideDissolve = "Can't overwrite inside a dissolve.";
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
                    return insideDissolve;
                var rightAfter = TrimmedState(right, ClipState.Capture(right), none, ClipEdge.Start, to, Math.Max(1, partAtEnd), rate);
                if (rightAfter.Start.ToNearestFrame(rate) != to) return insideDissolve;
                rightAfter.ApplyTo(right);         // a planned insert, not in the model yet
            }
            else if (start < from)
            {
                var after = TrimmedState(clip, state, none, ClipEdge.End, from, Math.Max(1, partAtStart), rate);
                if (after.End.ToNearestFrame(rate) != from) return insideDissolve;
                plan.Update(clip, track, after);
            }
            else
            {
                var after = TrimmedState(clip, state, none, ClipEdge.Start, to, Math.Max(1, partAtEnd), rate);
                if (after.Start.ToNearestFrame(rate) != to) return insideDissolve;
                plan.Update(clip, track, after);
            }
        }
        return null;
    }
}
