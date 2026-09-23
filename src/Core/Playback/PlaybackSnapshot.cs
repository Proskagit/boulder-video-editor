using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>Why (or whether) a clip can be played, decided when the snapshot is built.
/// Decode failures are not known here; they are reported at run time.</summary>
public enum SpanStatus
{
    /// <summary>Video decoded as a stream of frames.</summary>
    Video,
    /// <summary>A still image: one decoded frame held for the whole span.</summary>
    StillImage,
    /// <summary>Audio decoded as a stream (audio spans only).</summary>
    Audio,
    /// <summary>The asset is absent or unavailable (not in the project, missing file, no metadata).</summary>
    Offline,
    /// <summary>Playback of this clip is not supported (e.g. Speed ≠ 1, wrong media kind).</summary>
    Unsupported
}

/// <summary>What a snapshot needs to know about one media asset.</summary>
public sealed record PlaybackAsset(Guid Id, string FilePath, MediaKind Kind, MediaTime StartTime, FrameRate? NominalFrameRate);

/// <summary>
/// One clip on a visible video track, as a logical span. It is not split where higher tracks
/// cover it: which span is visible at a time is resolved by <see cref="PlaybackSnapshot.PictureAt"/>.
/// The source frame shown is chosen at run time from <see cref="SourceIn"/> (D009).
/// </summary>
public sealed record PictureSpan(
    Guid ClipId, Guid AssetId, SpanStatus Status, MediaTime TimelineStart, MediaTime TimelineEnd, MediaTime SourceIn, string? Reason = null)
{
    public bool Contains(MediaTime time) => time >= TimelineStart && time < TimelineEnd;
}

/// <summary>An audible source: a VideoClip with an audio stream (on any video track, hidden or
/// not) or an AudioClip. Muted sources are left out.</summary>
public sealed record AudioSpan(
    Guid ClipId, Guid AssetId, SpanStatus Status, MediaTime TimelineStart, MediaTime TimelineEnd, MediaTime SourceIn, double Gain, string? Reason = null);

/// <summary>A visible video track: its clips in timeline order (non-overlapping, as on the track).</summary>
public sealed record VideoLayer(Guid TrackId, ImmutableArray<PictureSpan> Spans);

/// <summary>
/// Immutable copy of everything the background playback pipeline needs, built on the UI
/// thread by <see cref="PlaybackSnapshotBuilder"/>. The pipeline never reads the mutable
/// <see cref="Project"/> / <see cref="Sequence"/>.
/// </summary>
public sealed class PlaybackSnapshot
{
    public PlaybackSnapshot(long version, FrameRate frameRate, MediaTime duration,
        ImmutableArray<VideoLayer> videoLayers, ImmutableArray<AudioSpan> audioSpans,
        ImmutableDictionary<Guid, PlaybackAsset> assets)
    {
        SnapshotVersion = version;
        FrameRate = frameRate;
        Duration = duration;
        VideoLayers = videoLayers;
        AudioSpans = audioSpans;
        Assets = assets;
    }

    /// <summary>Increases with every rebuild; results computed for an older version are discarded.</summary>
    public long SnapshotVersion { get; }

    /// <summary>Project frame rate (timeline grid).</summary>
    public FrameRate FrameRate { get; }

    public MediaTime Duration { get; }

    /// <summary>Visible video tracks, topmost first. Hidden tracks are not included.</summary>
    public ImmutableArray<VideoLayer> VideoLayers { get; }

    public ImmutableArray<AudioSpan> AudioSpans { get; }

    public ImmutableDictionary<Guid, PlaybackAsset> Assets { get; }

    /// <summary>The clip whose picture is shown at <paramref name="time"/>: the one on the topmost
    /// visible track covering it (half-open <c>[start, end)</c>), or null for a gap.</summary>
    public PictureSpan? PictureAt(MediaTime time)
    {
        foreach (var layer in VideoLayers)
        {
            if (Find(layer.Spans, time) is { } span)
                return span;
        }
        return null;
    }

    /// <summary>Earliest clip edge on any visible track after <paramref name="time"/> — where the
    /// visible picture may change; <see cref="Duration"/> if there is none.</summary>
    public MediaTime NextPictureChange(MediaTime time)
    {
        var next = Duration;
        foreach (var layer in VideoLayers)
        {
            foreach (var span in layer.Spans)
            {
                if (span.TimelineStart > time && span.TimelineStart < next) next = span.TimelineStart;
                if (span.TimelineEnd > time && span.TimelineEnd < next) next = span.TimelineEnd;
            }
        }
        return next;
    }

    private static PictureSpan? Find(ImmutableArray<PictureSpan> spans, MediaTime time)
    {
        int lo = 0, hi = spans.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var span = spans[mid];
            if (time < span.TimelineStart) hi = mid - 1;
            else if (time >= span.TimelineEnd) lo = mid + 1;
            else return span;
        }
        return null;
    }
}
