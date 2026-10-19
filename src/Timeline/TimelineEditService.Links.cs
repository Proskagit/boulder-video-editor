using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- Unlink Audio, Link, Unlink (D032 R3, R4, AQ8, AQ9, AQ11) ----------------------------------------------------------
// The link state is a clip property changed through EditPlan (SetLink), validated like every edit (LinkRules, the locked
// tracks) and committed as one undo step. Unlink Audio places its audio clip by the ordinary insert of a clip.
public sealed partial class TimelineEditService
{
    private const string UnlinkAudioDescription = "Unlink Audio";

    public string? GetUnlinkAudioBlockReason(Guid clipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } missing) return missing;
        if (clips[0] is not VideoClip video) return Messages.Link_OnlyVideoSound;
        if (video.IsAudioDetached) return Messages.Link_AlreadyUnlinked;
        if (video.LinkId is not null) return Messages.Link_ClipLinked;
        if (FindAsset(video.MediaAssetId)?.Metadata is not { AudioCodec: not null })
            return Messages.Link_NoSound;
        return CheckEditable(plan, clips);
    }

    public TimelineEditResult UnlinkAudio(Guid videoClipId)
    {
        if (GetUnlinkAudioBlockReason(videoClipId) is { } blocked) return TimelineEditResult.Fail(blocked);

        var plan = new EditPlan(Sequence, Settings);
        Resolve(plan, new[] { videoClipId }, out var clips);
        var video = (VideoClip)clips[0];
        var videoTrack = plan.TrackOf(video);
        var rate = plan.Rate;

        // AQ11: the sound keeps its effective fades — a fade on an edge with a dissolve is not applied to the video clip's
        // sound (D025 PO-8), so the audio clip gets none there; a stored fade that is applied is kept as stored.
        var (fadeIn, fadeOut) = FadeRule.EffectiveFrames(video, rate,
            videoTrack.Transitions.Any(t => t.RightClipId == video.Id), videoTrack.Transitions.Any(t => t.LeftClipId == video.Id));
        var sound = new AudioClip
        {
            MediaAssetId = video.MediaAssetId, Speed = video.Speed, Volume = video.Volume,
            // AQ8: a muted video track played the sound silent; the audio clip stays silent.
            IsMuted = video.IsMuted || videoTrack.IsMuted,
            FadeIn = TransitionRules.Frames(video.FadeIn, rate) == fadeIn ? video.FadeIn : MediaTime.FromFrame(fadeIn, rate),
            FadeOut = TransitionRules.Frames(video.FadeOut, rate) == fadeOut ? video.FadeOut : MediaTime.FromFrame(fadeOut, rate)
        };
        ClipState.Capture(video).ApplyTo(sound);

        // AQ8: the first audio track that takes the sound as it is heard now — unlocked, not muted, free over the clip —
        // otherwise a new audio track in the same step.
        var target = Sequence.AudioTracks.FirstOrDefault(t => !t.IsLocked && !t.IsMuted
            && plan.EffectiveClips(t).All(c => c.State.End <= sound.TimelineStart || c.State.Start >= sound.TimelineEnd));
        var newTrack = target is null ? NewTrack(TrackType.Audio) : null;
        plan.Insert(target ?? newTrack!, sound);
        plan.SetLink(video, ClipLinkState.Capture(video) with { IsAudioDetached = true });

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Link_CantUnlink, error));

        var command = plan.BuildCommand(UnlinkAudioDescription);
        if (newTrack is not null)
            command = new CompositeCommand(UnlinkAudioDescription, new[] { new AddTrackCommand(Sequence, newTrack), command });
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { video.Id, sound.Id },
            string.Format(Messages.Link_SoundUnlinkedTo, (target ?? newTrack)!.Name));
    }

    public TimelineEditResult LinkClips(IReadOnlyCollection<Guid> clipIds)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } missing) return TimelineEditResult.Fail(missing);
        if (clips.Count != 2 || clips.OfType<VideoClip>().Count() != 1 || clips.OfType<AudioClip>().Count() != 1)
            return TimelineEditResult.Fail(Messages.Link_SelectPair);
        if (clips.Cast<MediaBackedClip>().Any(c => c.LinkId is not null))
            return TimelineEditResult.Fail(Messages.Link_AlreadyLinked);
        if (CheckEditable(plan, clips) is { } locked) return TimelineEditResult.Fail(locked);

        var link = Guid.NewGuid();
        foreach (var clip in clips.Cast<MediaBackedClip>())
            plan.SetLink(clip, ClipLinkState.Capture(clip) with { LinkId = link });

        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Link_CantLink, error));
        Commit(plan, "Link Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), Messages.Link_Linked);
    }

    public TimelineEditResult UnlinkClips(IReadOnlyCollection<Guid> clipIds)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } missing) return TimelineEditResult.Fail(missing);

        var linked = WithPartners(plan, clips).OfType<MediaBackedClip>().Where(c => c.LinkId is not null).ToList();
        if (linked.Count == 0) return TimelineEditResult.Unchanged();
        if (CheckEditable(plan, linked) is { } locked) return TimelineEditResult.Fail(locked);   // L5: a locked partner too

        foreach (var clip in linked)
            plan.SetLink(clip, ClipLinkState.Capture(clip) with { LinkId = null });

        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Link_CantUnlinkClips, error));
        Commit(plan, "Unlink Clips");
        return TimelineEditResult.Ok(linked.Select(c => c.Id).ToList(), Messages.Link_Unlinked);
    }

    /// <summary><paramref name="clips"/> with the other clip of each link among them (D032 R5), each once, in order.</summary>
    private static List<Clip> WithPartners(EditPlan plan, IEnumerable<Clip> clips)
    {
        var all = plan.AllTracks.SelectMany(t => t.Clips).ToList();
        var result = new List<Clip>();
        foreach (var clip in clips)
        {
            if (!result.Contains(clip)) result.Add(clip);
            if (LinkRules.PartnerOf(clip, all) is { } partner && !result.Contains(partner)) result.Add(partner);
        }
        return result;
    }

    /// <summary>A new, empty track of <paramref name="type"/> named and ordered as "Add Track" does (not yet added).</summary>
    private Track NewTrack(TrackType type)
    {
        var list = type == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks;
        var prefix = type == TrackType.Video ? "V" : "A";
        var number = list.Count + 1;
        while (list.Any(t => t.Name == $"{prefix}{number}")) number++;

        return new Track
        {
            Type = type,
            Name = $"{prefix}{number}",
            Order = list.Count == 0 ? 0 : list.Max(t => t.Order) + 1
        };
    }
}
