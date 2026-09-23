using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// Builds a <see cref="PlaybackSnapshot"/> from the live project. Must run on the thread that
/// owns the project model (the UI thread); the result is safe to share with any thread.
/// Rules (D010): the topmost visible video track wins the picture; hidden tracks still
/// contribute audio; muted tracks/clips contribute no audio; TextClips are transparent
/// (Phase 7); only speed 1.0 is playable.
/// </summary>
public static class PlaybackSnapshotBuilder
{
    public static PlaybackSnapshot Build(Project project, long version)
    {
        var sequence = project.Timeline;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var used = new Dictionary<Guid, PlaybackAsset>();

        var layers = ImmutableArray.CreateBuilder<VideoLayer>();
        var audio = ImmutableArray.CreateBuilder<AudioSpan>();

        var videoTracks = sequence.VideoTracks
            .Select((track, index) => (track, index))
            .OrderByDescending(t => t.track.Order).ThenByDescending(t => t.index)
            .Select(t => t.track);

        foreach (var track in videoTracks)
        {
            var spans = ImmutableArray.CreateBuilder<PictureSpan>();
            foreach (var clip in track.Clips.OrderBy(c => c.TimelineStart))
            {
                if (clip is not MediaBackedClip media)
                    continue; // TextClip: transparent until Phase 7

                assets.TryGetValue(media.MediaAssetId, out var asset);
                var (status, reason) = PictureStatus(media, asset);
                Remember(asset, used);
                if (!track.IsHidden)
                    spans.Add(new PictureSpan(clip.Id, media.MediaAssetId, status, clip.TimelineStart, clip.TimelineEnd, media.SourceIn, reason));

                if (media is VideoClip video && !track.IsMuted && asset?.Metadata?.AudioCodec is not null)
                {
                    var (audioStatus, audioReason) = AudioStatus(media, asset);
                    audio.Add(new AudioSpan(clip.Id, media.MediaAssetId, audioStatus, clip.TimelineStart, clip.TimelineEnd,
                        media.SourceIn, video.Volume, audioReason));
                }
            }
            if (!track.IsHidden)
                layers.Add(new VideoLayer(track.Id, spans.ToImmutable()));
        }

        foreach (var track in sequence.AudioTracks)
        {
            if (track.IsMuted) continue;
            foreach (var clip in track.Clips.OfType<AudioClip>().OrderBy(c => c.TimelineStart))
            {
                if (clip.IsMuted) continue;
                assets.TryGetValue(clip.MediaAssetId, out var asset);
                Remember(asset, used);
                var (status, reason) = AudioStatus(clip, asset);
                audio.Add(new AudioSpan(clip.Id, clip.MediaAssetId, status, clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.Volume, reason));
            }
        }

        return new PlaybackSnapshot(version, project.Settings.FrameRate, sequence.Duration(),
            layers.ToImmutable(), audio.ToImmutable(), used.ToImmutableDictionary());
    }

    /// <summary>
    /// Everything about one asset that <see cref="Build"/> depends on. Two captures that are equal
    /// for every asset the timeline uses produce the same snapshot, so a media change that leaves
    /// them equal (e.g. an unused asset finished analysis) needs no rebuild.
    /// </summary>
    public readonly record struct AssetState(
        bool InProject, string? FilePath, MediaKind Kind, bool IsMissing, bool HasMetadata, bool HasAudio,
        MediaTime StartTime, FrameRate? NominalFrameRate);

    /// <summary>States of the assets referenced by media-backed clips on any track (hidden
    /// tracks included — their clips still contribute audio).</summary>
    public static ImmutableDictionary<Guid, AssetState> CaptureAssetStates(Project project)
    {
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var states = ImmutableDictionary.CreateBuilder<Guid, AssetState>();
        foreach (var clip in project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks)
                     .SelectMany(t => t.Clips).OfType<MediaBackedClip>())
        {
            if (states.ContainsKey(clip.MediaAssetId)) continue;
            states[clip.MediaAssetId] = assets.TryGetValue(clip.MediaAssetId, out var a)
                ? new AssetState(true, a.FilePath, a.Kind, a.IsMissing, a.Metadata is not null, a.Metadata?.AudioCodec is not null,
                    a.Metadata?.StartTime ?? MediaTime.Zero, SourceFrameSelector.NominalRate(a.Metadata))
                : new AssetState(false, null, default, false, false, false, MediaTime.Zero, null);
        }
        return states.ToImmutable();
    }

    /// <summary>True when the two captures differ for any asset.</summary>
    public static bool AssetStatesDiffer(ImmutableDictionary<Guid, AssetState> a, ImmutableDictionary<Guid, AssetState> b) =>
        a.Count != b.Count || a.Any(pair => !b.TryGetValue(pair.Key, out var other) || other != pair.Value);

    private static (SpanStatus, string?) PictureStatus(MediaBackedClip clip, MediaAsset? asset)
    {
        if (Unavailable(asset) is { } offline) return (SpanStatus.Offline, offline);
        if (clip.Speed != 1.0) return (SpanStatus.Unsupported, "Only speed 1.0 can be played.");
        return (clip, asset!.Kind) switch
        {
            (VideoClip, MediaKind.Video) when asset.Metadata is null => (SpanStatus.Offline, "Media information is not available."),
            (VideoClip, MediaKind.Video) => (SpanStatus.Video, null),
            (ImageClip, MediaKind.Image) => (SpanStatus.StillImage, null),
            _ => (SpanStatus.Unsupported, $"A {clip.GetType().Name} cannot play {asset.Kind} media.")
        };
    }

    private static (SpanStatus, string?) AudioStatus(MediaBackedClip clip, MediaAsset? asset)
    {
        if (Unavailable(asset) is { } offline) return (SpanStatus.Offline, offline);
        if (clip.Speed != 1.0) return (SpanStatus.Unsupported, "Only speed 1.0 can be played.");
        if (asset!.Metadata is null) return (SpanStatus.Offline, "Media information is not available.");
        return asset.Kind is MediaKind.Audio or MediaKind.Video
            ? (SpanStatus.Audio, null)
            : (SpanStatus.Unsupported, $"{asset.Kind} media has no audio.");
    }

    private static string? Unavailable(MediaAsset? asset) =>
        asset is null ? "The media is not in the project."
        : asset.IsMissing ? "The media file is missing."
        : null;

    private static void Remember(MediaAsset? asset, Dictionary<Guid, PlaybackAsset> used)
    {
        if (asset is null || used.ContainsKey(asset.Id)) return;
        used[asset.Id] = new PlaybackAsset(asset.Id, asset.FilePath, asset.Kind,
            asset.Metadata?.StartTime ?? MediaTime.Zero, SourceFrameSelector.NominalRate(asset.Metadata));
    }
}
