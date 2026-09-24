using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

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
                return $"{KindName(clip)} clips can't go on {(track.Type == TrackType.Video ? "a video" : "an audio")} track.";

            if (state.Start < MediaTime.Zero)
                return "A clip can't start before the beginning of the timeline.";

            if (state.Duration <= MediaTime.Zero)
                return "A clip must be at least one frame long.";

            if (!state.Start.IsOnFrameGrid(rate) || !state.End.IsOnFrameGrid(rate))
                return "Clip edges must lie on the project frame grid.";

            if (clip is MediaBackedClip media)
            {
                if (findAsset(media.MediaAssetId) is not { } asset)
                    return "A clip refers to media that is not in the project.";

                if (clip is ImageClip && !state.Speed.IsNormal)
                    return "Images have no speed.";

                if (state.SourceIn < MediaTime.Zero)
                    return "A clip can't start before the beginning of its source media.";

                var frames = state.End.ToFrameFloor(rate) - state.Start.ToFrameFloor(rate);
                if (!SpeedTiming.Fits(frames, state.SourceOut - state.SourceIn, state.Speed, rate))
                    return "Clip source range doesn't match its duration.";

                if (clip is VideoClip or AudioClip)
                {
                    if (asset.Metadata is not { } metadata || metadata.Duration <= MediaTime.Zero)
                        return $"The duration of {asset.FileName} is unknown.";

                    if (state.SourceOut > metadata.Duration)
                        return $"A clip can't extend past the end of {asset.FileName}.";
                }
            }
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].State.End > ordered[i].State.Start)
                return $"Clips would overlap on track {track.Name}.";
        }

        return null;
    }

    /// <summary>Validates every track of <paramref name="sequence"/> as it currently is.</summary>
    public static string? ValidateSequence(Sequence sequence, FrameRate rate, Func<Guid, MediaAsset?> findAsset)
    {
        foreach (var track in sequence.VideoTracks)
        {
            if (track.Type != TrackType.Video) return $"Track {track.Name} is in the wrong track list.";
            if (ValidateTrack(track, track.Clips.Select(c => (c, ClipState.Capture(c))), rate, findAsset) is { } error) return error;
            if (!IsSorted(track)) return $"Track {track.Name} clips are not sorted.";
        }

        foreach (var track in sequence.AudioTracks)
        {
            if (track.Type != TrackType.Audio) return $"Track {track.Name} is in the wrong track list.";
            if (ValidateTrack(track, track.Clips.Select(c => (c, ClipState.Capture(c))), rate, findAsset) is { } error) return error;
            if (!IsSorted(track)) return $"Track {track.Name} clips are not sorted.";
        }

        return null;
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
        VideoClip => "Video",
        AudioClip => "Audio",
        ImageClip => "Image",
        TextClip => "Text",
        _ => "These"
    };
}
