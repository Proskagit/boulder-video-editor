using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>Delete, tracks and their state and mix, ripple delete and close gap, markers, media removal (split out, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
    // --- Delete / tracks -------------------------------------------------------

    public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clips = WithPartners(plan, selected);   // AQ2: a linked clip goes with its partner

        if (clips.Select(plan.TrackOf).FirstOrDefault(t => t.IsLocked) is { } locked)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, locked.Name));

        foreach (var clip in clips) plan.Remove(clip);
        if (ReconcileOverlays(plan) is { } overlayError) return TimelineEditResult.Fail(overlayError);   // D048 §3.10
        if (plan.AllTracks.FirstOrDefault(t => t.IsLocked && t.Clips.Any(plan.Removes)) is { } lockedTemplateTrack)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, lockedTemplateTrack.Name));
        plan.ReconcileTransitions();   // a deleted clip's dissolves go with it (D025 §5)
        if (CloseMainTrackGaps(plan) is { } magnetError) return TimelineEditResult.Fail(magnetError);   // D069 §6

        Commit(plan, clips.Count == 1 ? "Delete Clip" : "Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult AddTrack(TrackType type)
    {
        var track = NewTrack(type);
        ExecuteTracked(new NotifyingCommand(new AddTrackCommand(Sequence, track), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public string? GetDeleteTrackBlockReason(Guid trackId)
    {
        if (FindTrack(trackId) is not { } track) return Messages.Edit_TrackGone;
        if (track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
        if (Sequence.VideoTracks.Count + Sequence.AudioTracks.Count <= 1) return Messages.Edit_NeedsOneTrack;
        // D032 R5, AQ12: deleting the track clears the links of its clips' partners — never on a locked track.
        if (LinkedPartners(track).FirstOrDefault(p => p.Track.IsLocked) is { Track: { } lockedTrack })
            return string.Format(Messages.Edit_TrackLocked, lockedTrack.Name);
        return null;
    }

    /// <summary>The clips on other tracks linked to a clip of <paramref name="track"/>, with their tracks.</summary>
    private List<(Track Track, MediaBackedClip Clip)> LinkedPartners(Track track) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).Where(t => t != track)
            .SelectMany(t => t.Clips.OfType<MediaBackedClip>().Select(c => (Track: t, Clip: c)))
            .Where(p => p.Clip.LinkId is { } link && track.Clips.OfType<MediaBackedClip>().Any(c => c.LinkId == link))
            .ToList();

    public TimelineEditResult DeleteTrack(Guid trackId)
    {
        if (GetDeleteTrackBlockReason(trackId) is { } blocked) return TimelineEditResult.Fail(blocked);
        var track = FindTrack(trackId)!;

        // D032 R5: the clips linked to the track's clips lose their link in the same step (a locked one refused above).
        var partners = LinkedPartners(track);

        // The clips and dissolves stay on the track object, so Undo brings them back exactly (D027 §3).
        IUndoableCommand command = new RemoveTrackCommand(Sequence, track);
        if (partners.Count > 0)
            command = new CompositeCommand(command.Description, partners
                .Select(p => (IUndoableCommand)new SetClipLinkCommand(p.Clip, ClipLinkState.Capture(p.Clip),
                    ClipLinkState.Capture(p.Clip) with { LinkId = null }, command.Description))
                .Prepend(command));
        // D064 §4: a ducking listening to the deleted track is turned off in the same step (Undo brings it back).
        var listening = Sequence.VideoTracks.Concat(Sequence.AudioTracks).Where(t => t.Ducking?.SpeechTrackId == track.Id).ToList();
        if (listening.Count > 0)
            command = new CompositeCommand(command.Description, listening
                .Select(t => (IUndoableCommand)new SetTrackMixCommand(t, TrackMix.Of(t), TrackMix.Of(t) with { Ducking = null }))
                .Prepend(command));
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult MoveTrack(Guid trackId, int direction)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction), direction, "Use +1 or −1.");
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        // The layer order as the playback snapshot draws it (PlaybackSnapshotBuilder): by Order, equal orders by their
        // place in the list — lowest first.
        var list = track.Type == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks;
        var ordered = list.Select((t, index) => (t, index)).OrderBy(x => x.t.Order).ThenBy(x => x.index).Select(x => x.t).ToList();
        var position = ordered.IndexOf(track);
        var target = position + direction;
        if (target < 0 || target >= ordered.Count) return TimelineEditResult.Unchanged();

        var neighbour = ordered[target];
        if (neighbour.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLockedNeighbour, neighbour.Name, track.Name));

        List<TrackOrderChange> changes;
        if (track.Order != neighbour.Order)
        {
            changes = new() { new(track, track.Order, neighbour.Order), new(neighbour, neighbour.Order, track.Order) };
        }
        else
        {
            // Equal orders (a file from elsewhere) can't be swapped: number the tracks of this kind 0, 1, … in the new
            // order, which keeps every other track where it is drawn.
            (ordered[position], ordered[target]) = (neighbour, track);
            changes = ordered.Select((t, i) => new TrackOrderChange(t, t.Order, i)).Where(c => c.Before != c.After).ToList();
        }

        ExecuteTracked(new NotifyingCommand(new SetTrackOrderCommand(changes), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    // --- Track state (D030 §4) ------------------------------------------------------------
    // Mute and hide are monitoring state, so a locked track takes them too (D030 Q13); the playback snapshot reads the
    // flags, so the Preview and the export follow without any other change.

    public TimelineEditResult SetTrackMuted(Guid trackId, bool muted) => SetTrackState(trackId, TrackStateFlag.Muted, muted);

    public TimelineEditResult SetTrackHidden(Guid trackId, bool hidden)
    {
        if (FindTrack(trackId) is { Type: TrackType.Audio } track)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_AudioTrackNoPicture, track.Name));
        return SetTrackState(trackId, TrackStateFlag.Hidden, hidden);
    }

    public TimelineEditResult SetTrackLocked(Guid trackId, bool locked) => SetTrackState(trackId, TrackStateFlag.Locked, locked);

    public TimelineEditResult SetTrackSolo(Guid trackId, bool solo) => SetTrackState(trackId, TrackStateFlag.Solo, solo);

    // --- Track mix and the master (D064 §1, §4) ----------------------------------------------------------------------

    public TimelineEditResult SetTrackVolume(Guid trackId, double volume, object? gesture = null)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (!double.IsFinite(volume) || volume < ClipPropertyLimits.MinVolume || volume > ClipPropertyLimits.MaxVolume)
            return TimelineEditResult.Fail(string.Format(Messages.Prop_Volume, "0 %", "200 %"));
        if (track.Volume.Equals(volume)) return TimelineEditResult.Unchanged();
        var before = TrackMix.Of(track);
        ExecuteTracked(new NotifyingCommand(new SetTrackMixCommand(track, before, before with { Volume = volume }, gesture),
            _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult SetTrackDucking(Guid trackId, Core.Audio.DuckingSettings? ducking, object? gesture = null)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (ducking is not null)
        {
            if (!ducking.IsValid) return TimelineEditResult.Fail(Messages.Ducking_Invalid);
            if (ducking.SpeechTrackId == track.Id || FindTrack(ducking.SpeechTrackId) is null)
                return TimelineEditResult.Fail(Messages.Ducking_SpeechTrack);
        }
        if (track.Ducking == ducking) return TimelineEditResult.Unchanged();
        var before = TrackMix.Of(track);
        ExecuteTracked(new NotifyingCommand(new SetTrackMixCommand(track, before, before with { Ducking = ducking }, gesture),
            _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult SetMasterVolume(double volume, object? gesture = null)
    {
        if (!double.IsFinite(volume) || volume < ClipPropertyLimits.MinVolume || volume > ClipPropertyLimits.MaxVolume)
            return TimelineEditResult.Fail(string.Format(Messages.Prop_Volume, "0 %", "200 %"));
        if (Sequence.MasterVolume.Equals(volume)) return TimelineEditResult.Unchanged();
        ExecuteTracked(new NotifyingCommand(new SetMasterVolumeCommand(Sequence, Sequence.MasterVolume, volume, gesture),
            _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult BakeDucking(Guid trackId, Core.Audio.DuckingCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (track.Ducking is null) return TimelineEditResult.Unchanged();

        var commands = new List<IUndoableCommand>();
        var changed = new List<Guid>();
        foreach (var clip in track.Clips.OfType<MediaBackedClip>())
        {
            if (clip is VideoClip { IsAudioDetached: true } || AudioProperties.Of(clip) is not { } sound) continue;
            var placement = Core.Playback.AudioPlacement.Of(clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.Speed);
            if (placement.EndSample <= placement.FirstSample || !curve.Affects(placement.FirstSample, placement.EndSample)) continue;

            // The curve's corners inside the clip, its edges and the clip's own points: the product of two curves even in dB
            // between their corners is even in dB between the union of them, so the baked points sound as the ducking did.
            var samples = new SortedSet<long> { placement.FirstSample, placement.EndSample - 1 };
            foreach (var corner in curve.Corners()) if (corner > placement.FirstSample && corner < placement.EndSample - 1) samples.Add(corner);
            var points = new List<Core.Audio.VolumePoint>();
            var times = new HashSet<MediaTime>();
            foreach (var k in samples)
            {
                var time = placement.SourcePositionAt(k);
                if (!times.Add(time)) continue;
                var own = sound.Volumes?.At(time) ?? 1;
                points.Add(new Core.Audio.VolumePoint(Guid.NewGuid(), time,
                    Math.Clamp(own * curve.Gain(k), Core.Audio.VolumeEnvelope.MinGain, Core.Audio.VolumeEnvelope.MaxGain)));
            }
            if (sound.Volumes is { } existing)
                foreach (var point in existing.Points)
                {
                    var k = Core.Playback.AudioTiming.CeilingSample(clip.TimelineStart + new MediaTime(
                        (long)Math.Round((point.Time - clip.SourceIn).Ticks / (double)clip.Speed.ToDecimal())));
                    if (!times.Add(point.Time)) continue;
                    // A point outside the used range is kept as it is (a trim back shows it again, D064 §1).
                    if (k <= placement.FirstSample || k >= placement.EndSample - 1) { points.Add(point); continue; }
                    points.Add(point with { Gain = Math.Clamp(point.Gain * curve.Gain(k), Core.Audio.VolumeEnvelope.MinGain, Core.Audio.VolumeEnvelope.MaxGain) });
                }
            var before = ClipPropertyValues.Capture(clip);
            var after = before with { Audio = sound with { Volumes = Core.Audio.VolumeEnvelope.Of(points) } };
            FollowSettings? followAfter = null;
            if (clip is VideoClip synced && HasOwnSound(synced)) (after, followAfter) = KeyframeVolumeSync.Sync(before, after, synced.Follow, synced.Follow);   // amendment 1
            if (clip is VideoClip withFollow && followAfter is not null && !Equals(withFollow.Follow, followAfter))
                commands.Add(new Commands.SetClipFollowCommand(withFollow, withFollow.Follow, followAfter));
            commands.Add(new SetClipPropertiesCommand(clip, before, after, ClipPropertyValues.Diff(before, after)));
            changed.Add(clip.Id);
        }
        var mix = TrackMix.Of(track);
        commands.Add(new SetTrackMixCommand(track, mix, mix with { Ducking = null }));
        ExecuteTracked(new NotifyingCommand(new CompositeCommand("Bake Ducking", commands), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(changed);
    }

    private TimelineEditResult SetTrackState(Guid trackId, TrackStateFlag flag, bool value)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (SetTrackStateCommand.Get(track, flag) == value) return TimelineEditResult.Unchanged();

        ExecuteTracked(new NotifyingCommand(new SetTrackStateCommand(track, flag, value), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    // --- Ripple delete and close gap (D027 §2) ---------------------------------

    public TimelineEditResult RippleDeleteClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clips = WithPartners(plan, selected);   // AQ2: a linked clip goes with its partner
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var rate = plan.Rate;
        var removed = clips.ToHashSet();

        // What each removed clip closes up: its own length — except a linked pair, whose two tracks both close by the
        // length of its target clip (the one named last, the selection's primary; or the one named): so the later clips of
        // both tracks move by the same frames and later linked pairs keep their offset, J / L cuts included (product
        // owner, 2026-10-12, after the Phase 17 closeout; AQ4 / L5).
        long Frames(Clip c) => c.TimelineEnd.ToNearestFrame(rate) - c.TimelineStart.ToNearestFrame(rate);
        var closes = clips.ToDictionary(c => c, Frames);
        foreach (var clip in selected)
        {
            if (PartnerOf(plan, clip) is { } partner && removed.Contains(partner))
                closes[clip] = closes[partner] = Frames(clip);
        }

        foreach (var group in clips.GroupBy(plan.TrackOf))
        {
            // Per track: a clip that is not removed moves left by what the removed clips that end at or before its start
            // close up. Clips on other tracks, the playhead and the markers stay; gaps that are not removed move along.
            var spans = group.Select(c => (End: c.TimelineEnd.ToNearestFrame(rate), Closes: closes[c])).ToList();
            foreach (var clip in group) plan.Remove(clip);
            foreach (var clip in group.Key.Clips.Where(c => !removed.Contains(c)))
            {
                var start = clip.TimelineStart.ToNearestFrame(rate);
                var shift = spans.Where(s => s.End <= start).Sum(s => s.Closes);
                if (shift > 0 && PlanShift(plan, clip, group.Key, -shift) is { } shiftError)
                    return TimelineEditResult.Fail(string.Format(Messages.Edit_CantRippleDelete, shiftError));
            }
        }

        // A removed clip's dissolves go with it; every other dissolve has both clips moved by the same distance and is
        // kept as it is; none is created where clips now meet (D027 §2, D025 §5). The result is validated like any edit.
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantRippleDelete, error));

        Commit(plan, clips.Count == 1 ? "Ripple Delete Clip" : "Ripple Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult CloseGap(Guid trackId, MediaTime at)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var frame = at.ToFrameFloor(rate);
        var spans = track.Clips.Select(c => (Clip: c, Start: c.TimelineStart.ToNearestFrame(rate), End: c.TimelineEnd.ToNearestFrame(rate))).ToList();
        if (frame < 0 || spans.Any(s => s.Start <= frame && frame < s.End))
            return TimelineEditResult.Fail(Messages.Edit_NoGapThere);
        var following = spans.Where(s => s.Start > frame).ToList();
        if (following.Count == 0)
            return TimelineEditResult.Fail(Messages.Edit_NoGapNoClipFollows);
        var gapEnd = following.Min(s => s.Start);
        var gapStart = spans.Where(s => s.End <= frame).Select(s => s.End).DefaultIfEmpty(0).Max();

        // Every clip from the gap's end on moves left by the gap's length; a dissolve's two clips move together.
        foreach (var s in following)
        {
            if (PlanShift(plan, s.Clip, track, gapStart - gapEnd) is { } shiftError)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CantCloseGap, shiftError));
        }
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantCloseGap, error));

        Commit(plan, "Close Gap");
        return TimelineEditResult.Ok(following.Select(s => s.Clip.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult CloseGapBefore(Guid clipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clip = clips[0];
        var rate = plan.Rate;
        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var track = plan.TrackOf(clip);
        if (startFrame == 0 || track.Clips.Any(c => c != clip && c.TimelineEnd.ToNearestFrame(rate) == startFrame))
            return TimelineEditResult.Fail(Messages.Edit_NoGapBefore);
        return CloseGap(track.Id, MediaTime.FromFrame(startFrame - 1, rate));
    }

    // --- Markers (D027 §6) ------------------------------------------------------

    /// <summary>The marker on <paramref name="frame"/> of the current grid, if any (markers keep their time when the frame
    /// rate changes, so the nearest frame decides).</summary>
    private Marker? MarkerOnFrame(long frame) =>
        Sequence.Markers.FirstOrDefault(m => m.Position.ToNearestFrame(FrameRate) == frame);

    public TimelineEditResult AddMarker(MediaTime at)
    {
        var frame = Math.Max(0, at.ToNearestFrame(FrameRate));
        if (MarkerOnFrame(frame) is not null) return TimelineEditResult.Fail(Messages.Edit_MarkerExists);

        var marker = new Marker { Position = MediaTime.FromFrame(frame, FrameRate) };
        ExecuteTracked(new NotifyingCommand(new AddMarkerCommand(Sequence, marker), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, MarkerId = marker.Id };
    }

    public TimelineEditResult RemoveMarkerAt(MediaTime at)
    {
        if (MarkerOnFrame(at.ToNearestFrame(FrameRate)) is not { } marker)
            return TimelineEditResult.Fail(Messages.Edit_NoMarkerAtPlayhead);

        ExecuteTracked(new NotifyingCommand(new RemoveMarkerCommand(Sequence, marker), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, MarkerId = marker.Id };
    }

    public TimelineEditResult SetMarkerLabel(Guid markerId, string label)
    {
        if (Sequence.Markers.FirstOrDefault(m => m.Id == markerId) is not { } marker) return TimelineEditResult.Fail(Messages.Edit_NoMarkerAtPlayhead);
        var text = (label ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (text.Length > ITimelineEditService.MaxMarkerLabelLength) text = text[..ITimelineEditService.MaxMarkerLabelLength];
        if (text == marker.Label) return TimelineEditResult.Unchanged();
        ExecuteTracked(new NotifyingCommand(new SetMarkerLabelCommand(marker, marker.Label, text), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, MarkerId = marker.Id };
    }

    public MediaTime? NextMarker(MediaTime from)
    {
        var frame = from.ToNearestFrame(FrameRate);
        return Sequence.Markers.Where(m => m.Position.ToNearestFrame(FrameRate) > frame)
            .OrderBy(m => m.Position.Ticks).FirstOrDefault()?.Position;
    }

    public MediaTime? PreviousMarker(MediaTime from)
    {
        var frame = from.ToNearestFrame(FrameRate);
        return Sequence.Markers.Where(m => m.Position.ToNearestFrame(FrameRate) < frame)
            .OrderBy(m => m.Position.Ticks).LastOrDefault()?.Position;
    }

    // --- Media removal (D027 §4) ------------------------------------------------

    private static bool Uses(Clip clip, Guid mediaAssetId) => clip is MediaBackedClip m && m.MediaAssetId == mediaAssetId;

    public int CountClipsUsing(Guid mediaAssetId) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).Count(c => Uses(c, mediaAssetId));

    public string? GetRemoveMediaBlockReason(Guid mediaAssetId)
    {
        if (FindAsset(mediaAssetId) is not { } asset) return Messages.Edit_MediaGone;
        if (Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.IsLocked && t.Clips.Any(c => Uses(c, mediaAssetId)))
            is { } locked)
            return string.Format(Messages.Edit_UsedOnLockedTrack, asset.FileName, locked.Name);
        return null;
    }

    public TimelineEditResult RemoveMedia(Guid mediaAssetId) => RemoveMediaMany(new[] { mediaAssetId });

    public TimelineEditResult RemoveMediaMany(IReadOnlyList<Guid> mediaAssetIds)
    {
        var ids = mediaAssetIds.Distinct().ToList();
        if (ids.Count == 0) return TimelineEditResult.Fail(Messages.Edit_MediaGone);
        foreach (var id in ids)
            if (GetRemoveMediaBlockReason(id) is { } blocked) return TimelineEditResult.Fail(blocked);
        var assets = ids.Select(id => FindAsset(id)!).ToList();

        var plan = new EditPlan(Sequence, Settings);
        var clips = plan.AllTracks.SelectMany(t => t.Clips).Where(c => ids.Any(id => Uses(c, id))).ToList();
        foreach (var clip in clips) plan.Remove(clip);
        if (CloseMainTrackGaps(plan) is { } magnetError) return TimelineEditResult.Fail(magnetError);   // D069 §6
        plan.ReconcileTransitions();   // the removed clips' dissolves go with them (D025 §5)
        plan.ReconcileLinks();         // and the links of their partners (D032 R5), never on a locked track (AQ12)
        if (plan.ClearedLinks.FirstOrDefault(c => plan.TrackOf(c).IsLocked) is { } lockedPartner)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, plan.TrackOf(lockedPartner).Name));

        // The clips (and dissolves) first, then the assets: no clip ever refers to an asset that isn't in the project.
        var removeAssets = assets
            .Select(a => (IUndoableCommand)new RemoveMediaAssetCommand(Project.MediaAssets, a, _projectService.NotifyMediaAssetsChanged))
            .ToList();
        IUndoableCommand command = clips.Count == 0
            ? (removeAssets.Count == 1 ? removeAssets[0] : new CompositeCommand("Remove Media", removeAssets))
            : new NotifyingCommand(new CompositeCommand("Remove Media", new[] { plan.BuildCommand("Remove Media") }.Concat(removeAssets).ToArray()),
                _projectService.NotifyTimelineChanged);
        ExecuteTracked(command);
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }
}
