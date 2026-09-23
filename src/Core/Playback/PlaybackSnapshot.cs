using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
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
/// <see cref="Visual"/> and <see cref="SourceSize"/> only affect composition (D018), never decoding.
/// </summary>
public sealed record PictureSpan(
    Guid ClipId, Guid AssetId, SpanStatus Status, MediaTime TimelineStart, MediaTime TimelineEnd, MediaTime SourceIn, string? Reason = null)
{
    public VisualProperties Visual { get; init; } = VisualProperties.Default;

    /// <summary>The source picture's pixel size from its metadata; null when unknown.</summary>
    public FrameSize? SourceSize { get; init; }

    public bool Contains(MediaTime time) => time >= TimelineStart && time < TimelineEnd;
}

/// <summary>A text clip on a visible video track (renderer-neutral content and style, D018).</summary>
public sealed record TextSpan(Guid ClipId, MediaTime TimelineStart, MediaTime TimelineEnd, VisualProperties Visual, TextProperties Text);

/// <summary>An audible source: a VideoClip with an audio stream (on any video track, hidden or
/// not) or an AudioClip. Clips on muted tracks are left out. A muted clip stays in the snapshot
/// with <see cref="IsMuted"/> set (mixed at zero gain), so its reader keeps running and unmuting
/// — like a volume change — only changes the mix, never the decoding. <see cref="Gain"/> is the
/// clip's volume either way.</summary>
public sealed record AudioSpan(
    Guid ClipId, Guid AssetId, SpanStatus Status, MediaTime TimelineStart, MediaTime TimelineEnd, MediaTime SourceIn, double Gain,
    string? Reason = null, bool IsMuted = false)
{
    /// <summary>The gain the mixer applies: 0 while muted, the volume otherwise.</summary>
    public double EffectiveGain => IsMuted ? 0 : Gain;
}

/// <summary>A visible video track: its media clips (<see cref="Spans"/>) and text clips
/// (<see cref="Texts"/>), each in timeline order; no two of them overlap (as on the track).</summary>
public sealed record VideoLayer(Guid TrackId, ImmutableArray<PictureSpan> Spans)
{
    public ImmutableArray<TextSpan> Texts { get; init; } = ImmutableArray<TextSpan>.Empty;
}

/// <summary>
/// Immutable copy of everything the background playback pipeline needs, built on the UI
/// thread by <see cref="PlaybackSnapshotBuilder"/>. The pipeline never reads the mutable
/// <see cref="Project"/> / <see cref="Sequence"/>.
/// </summary>
public sealed class PlaybackSnapshot
{
    public PlaybackSnapshot(long version, FrameRate frameRate, MediaTime duration,
        ImmutableArray<VideoLayer> videoLayers, ImmutableArray<AudioSpan> audioSpans,
        ImmutableDictionary<Guid, PlaybackAsset> assets, FrameSize canvas)
    {
        if (!canvas.IsValid) throw new ArgumentOutOfRangeException(nameof(canvas), "The canvas must have a positive size.");
        SnapshotVersion = version;
        Canvas = canvas;
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

    /// <summary>The project canvas (<see cref="ProjectSettings.FrameWidth"/> × <see cref="ProjectSettings.FrameHeight"/>).</summary>
    public FrameSize Canvas { get; }

    /// <summary>Visible video tracks, topmost first. Hidden tracks are not included.</summary>
    public ImmutableArray<VideoLayer> VideoLayers { get; }

    public ImmutableArray<AudioSpan> AudioSpans { get; }

    public ImmutableDictionary<Guid, PlaybackAsset> Assets { get; }

    /// <summary>
    /// The visible layers at <paramref name="time"/>, bottom to top (D018). Per visible track the clip
    /// covering the time (half-open <c>[start, end)</c>) becomes a layer, unless it is fully
    /// transparent (opacity 0) or empty text. Walking down from the top, everything below a layer
    /// that <see cref="PictureLayer.OccludesBelow"/> is left out. <paramref name="mayOcclude"/> lets
    /// playback veto an occluder that turned out not to deliver a picture at run time (a decode error
    /// or a file that vanished shows a placeholder, which never hides the layers below).
    /// </summary>
    public ImmutableArray<CompositionLayer> LayersAt(MediaTime time, Func<PictureLayer, bool>? mayOcclude = null)
    {
        var topDown = new List<CompositionLayer>();
        foreach (var layer in VideoLayers) // topmost first
        {
            if (Find(layer.Spans, time) is { } picture)
            {
                if (picture.Visual.Opacity == 0) continue;
                var geometry = picture.SourceSize is { } size ? CompositionMath.Layout(Canvas, size, picture.Visual) : null;
                var pictureLayer = new PictureLayer(layer.TrackId, picture, geometry);
                topDown.Add(pictureLayer);
                if (pictureLayer.OccludesBelow && (mayOcclude?.Invoke(pictureLayer) ?? true)) break;
            }
            else if (FindText(layer.Texts, time) is { } text && text.Visual.Opacity > 0 && !string.IsNullOrWhiteSpace(text.Text.Text))
            {
                topDown.Add(new TextLayer(layer.TrackId, text, CompositionMath.TextTransform(Canvas, text.Visual)));
            }
        }

        topDown.Reverse();
        return topDown.ToImmutableArray();
    }

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

    /// <summary>
    /// True when this snapshot and <paramref name="other"/> describe the same timeline for
    /// decoding — frame rate, duration, visible tracks, the timing and media of every clip, audio
    /// spans and assets — and differ at most in presentation: the mix (<see cref="AudioSpan.Gain"/>,
    /// <see cref="AudioSpan.IsMuted"/>), picture properties (<see cref="PictureSpan.Visual"/>,
    /// <see cref="PictureSpan.SourceSize"/>), text content/style, the canvas and their version.
    /// Playback can then keep every decoder.
    /// </summary>
    public bool DiffersOnlyInPresentation(PlaybackSnapshot other)
    {
        if (FrameRate != other.FrameRate || Duration != other.Duration) return false;

        if (VideoLayers.Length != other.VideoLayers.Length) return false;
        for (var i = 0; i < VideoLayers.Length; i++)
        {
            var (a, b) = (VideoLayers[i], other.VideoLayers[i]);
            if (a.TrackId != b.TrackId ||
                !a.Spans.Select(WithoutPresentation).SequenceEqual(b.Spans.Select(WithoutPresentation)) ||
                !a.Texts.Select(TextTiming).SequenceEqual(b.Texts.Select(TextTiming)))
                return false;
        }

        if (AudioSpans.Length != other.AudioSpans.Length) return false;
        for (var i = 0; i < AudioSpans.Length; i++)
        {
            if (WithoutMix(AudioSpans[i]) != WithoutMix(other.AudioSpans[i])) return false;
        }

        return Assets.Count == other.Assets.Count &&
               Assets.All(pair => other.Assets.TryGetValue(pair.Key, out var asset) && asset == pair.Value);

        static AudioSpan WithoutMix(AudioSpan span) => span with { Gain = 0, IsMuted = false };
        static PictureSpan WithoutPresentation(PictureSpan span) => span with { Visual = VisualProperties.Default, SourceSize = null };
        static (Guid, MediaTime, MediaTime) TextTiming(TextSpan span) => (span.ClipId, span.TimelineStart, span.TimelineEnd);
    }

    private static TextSpan? FindText(ImmutableArray<TextSpan> spans, MediaTime time)
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
