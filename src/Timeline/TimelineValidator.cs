using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Checks the timeline invariants for one track's (effective) contents. Returns a
/// short user-facing reason for the first violation, or null when valid:
/// <list type="number">
/// <item>clip kind matches the track type;</item>
/// <item>start ≥ 0, both edges exactly on the frame grid, at least one frame long;</item>
/// <item>no two clips overlap (touching edges is fine);</item>
/// <item>media-backed clips: SourceIn ≥ 0, the duration is the whole number of frames the source
/// range allows at the clip's speed (<see cref="SpeedTiming"/>; at 1× the edits keep the exact
/// SourceOut = SourceIn + Duration, D014/D022), images at 1× only, and for video/audio
/// SourceOut ≤ the source duration;</item>
/// <item>media-backed clips reference an existing asset.</item>
/// </list>
/// </summary>
public static class TimelineValidator
{
    public static string? ValidateTrack(
        Track track,
        IEnumerable<(Clip Clip, ClipState State)> clips,
        FrameRate rate,
        Func<Guid, MediaAsset?> findAsset)
    {
        var ordered = clips.OrderBy(c => c.State.Start.Ticks).ToList();

        foreach (var (clip, state) in ordered)
        {
            if (!IsCompatible(clip, track.Type))
                return string.Format(track.Type == TrackType.Video ? Messages.Validate_KindNotOnVideo : Messages.Validate_KindNotOnAudio, KindName(clip));

            if (state.Start < MediaTime.Zero)
                return Messages.Validate_BeforeTimelineStart;

            if (state.Duration <= MediaTime.Zero)
                return Messages.Validate_MinOneFrame;

            if (!state.Start.IsOnFrameGrid(rate) || !state.End.IsOnFrameGrid(rate))
                return Messages.Validate_OnGrid;

            if (clip is MediaBackedClip media)
            {
                if (findAsset(media.MediaAssetId) is not { } asset)
                    return Messages.Validate_MediaNotInProject;

                if (clip is ImageClip && !state.Speed.IsNormal)
                    return Messages.Validate_ImagesNoSpeed;

                if (state.SourceIn < MediaTime.Zero)
                    return Messages.Validate_BeforeSourceStart;

                var frames = state.End.ToFrameFloor(rate) - state.Start.ToFrameFloor(rate);
                if (!SpeedTiming.Fits(frames, state.SourceOut - state.SourceIn, state.Speed, rate))
                    return Messages.Validate_RangeMismatch;

                if (clip is VideoClip or AudioClip)
                {
                    if (asset.Metadata is not { } metadata || metadata.Duration <= MediaTime.Zero)
                        return string.Format(Messages.Validate_DurationUnknown, asset.FileName);

                    if (state.SourceOut > metadata.Duration)
                        return string.Format(Messages.Validate_PastEndOf, asset.FileName);
                }
            }
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].State.End > ordered[i].State.Start)
                return string.Format(Messages.Validate_Overlap, track.Name);
        }

        return null;
    }

    /// <summary>Validates every track of <paramref name="sequence"/> as it currently is.</summary>
    public static string? ValidateSequence(Sequence sequence, FrameRate rate, Func<Guid, MediaAsset?> findAsset)
    {
        foreach (var track in sequence.VideoTracks)
        {
            if (track.Type != TrackType.Video) return string.Format(Messages.Validate_WrongList, track.Name);
            if (ValidateTrack(track, track.Clips.Select(c => (c, ClipState.Capture(c))), rate, findAsset) is { } error) return error;
            if (!IsSorted(track)) return string.Format(Messages.Validate_NotSorted, track.Name);
            if (TransitionRules.ValidateTrack(track, rate) is { } transitionError) return transitionError;
        }

        foreach (var track in sequence.AudioTracks)
        {
            if (track.Type != TrackType.Audio) return string.Format(Messages.Validate_WrongList, track.Name);
            if (ValidateTrack(track, track.Clips.Select(c => (c, ClipState.Capture(c))), rate, findAsset) is { } error) return error;
            if (!IsSorted(track)) return string.Format(Messages.Validate_NotSorted, track.Name);
            if (TransitionRules.ValidateTrack(track, rate) is { } transitionError) return transitionError;
        }

        return LinkRules.Validate(sequence);
    }

    public static bool IsCompatible(Clip clip, TrackType trackType) =>
        clip is AudioClip ? trackType == TrackType.Audio : trackType == TrackType.Video;

    private static bool IsSorted(Track track)
    {
        for (var i = 1; i < track.Clips.Count; i++)
            if (track.Clips[i - 1].TimelineStart > track.Clips[i].TimelineStart)
                return false;
        return true;
    }

    private static string KindName(Clip clip) => clip switch
    {
        VideoClip => Messages.Kind_Video,
        AudioClip => Messages.Kind_Audio,
        ImageClip => Messages.Kind_Image,
        TextClip => Messages.Kind_Text,
        TemplateClip => Messages.Kind_Template,
        _ => Messages.Kind_Other
    };
}
