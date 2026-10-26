using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;

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
    /// <summary>Playback of this clip is not supported (the clip kind can't play the media kind).</summary>
    Unsupported
}

/// <summary>What a snapshot needs to know about one media asset.</summary>
public sealed record PlaybackAsset(Guid Id, string FilePath, MediaKind Kind, MediaTime StartTime, FrameRate? NominalFrameRate)
{
    /// <summary>How the picture is converted from HDR (D033 §3); null = the SDR path. Part of decoding: a change (the
    /// background colour probe, T3) reopens the asset's decoders.</summary>
    public Color.HdrConversion? Hdr { get; init; }
}

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

    /// <summary>The clip's speed (D022): part of the timing, so a change is never presentation-only.</summary>
    public ClipSpeed Speed { get; init; }

    /// <summary>The source picture's pixel size from its metadata; null when unknown.</summary>
    public FrameSize? SourceSize { get; init; }

    /// <summary>Effective fade lengths in frames (D025 §2, <see cref="FadeRule.EffectiveFrames"/>): presentation only.</summary>
    public long FadeInFrames { get; init; }
    public long FadeOutFrames { get; init; }

    /// <summary>Set when a dissolve extends the picture past the clip (D025 §3): B from the zone's start, A until the
    /// zone's end — frames taken from the source handles by the unchanged D009/D022 rule (the timing anchor stays
    /// <see cref="TimelineStart"/> / <see cref="SourceIn"/>). Part of the timing: a change is never presentation-only.</summary>
    public MediaTime? ExtendedStart { get; init; }
    public MediaTime? ExtendedEnd { get; init; }

    /// <summary>Where the picture may be shown: the clip, extended into its dissolve zones.</summary>
    public MediaTime ShownStart => ExtendedStart ?? TimelineStart;
    public MediaTime ShownEnd => ExtendedEnd ?? TimelineEnd;

    public bool Contains(MediaTime time) => time >= TimelineStart && time < TimelineEnd;

    /// <summary>The route outline of the clip's hold markup (D034 §6); null without holds. Presentation only.</summary>
    public HoldOutline? Outline { get; init; }
}

/// <summary>
/// What is drawn for a clip's hold markup (D034 §4–§6), renderer-neutral: the contours in normalized coordinates of the
/// display-oriented source frame, the stroke per 1080 px of the canvas's short side, the route colour and whether the
/// keyline is drawn (both resolved when the markup was edited). <see cref="Schedule"/>: the appear / disappear animation
/// on timeline frames (D036), null without one — every contour fully shown.
/// </summary>
public sealed record HoldOutline(ImmutableArray<ImmutableArray<NormalizedPoint>> Polygons, double StrokeWidth1080, string ColorHex, bool Keyline)
{
    public HoldSchedule? Schedule { get; init; }

    /// <summary>The source time each contour was marked at (<see cref="Hold.SourceTime"/>), by index.</summary>
    public ImmutableArray<MediaTime> MarkedAt { get; init; } = ImmutableArray<MediaTime>.Empty;

    /// <summary>The camera's motion over the clip's source (hold tracking); null without it — every contour then stays
    /// where it was marked. Applied per frame by <see cref="CompositionDrawPlan.Outline"/> from the shown frame's own
    /// timestamp, so the Preview and the export draw the same contours on the same picture.</summary>
    public CameraMotionTrack? Motion { get; init; }

    /// <summary>The file's start time (<c>MediaMetadata.StartTime</c>): the origin of the frame timestamps' source time.</summary>
    public MediaTime SourceStartTime { get; init; }

    /// <summary>The outline of <paramref name="markup"/> without the animation (it needs the clip's timing).</summary>
    public static HoldOutline? Of(HoldMarkup? markup) => markup is { Holds.Length: > 0 }
        ? new HoldOutline(markup.Holds.Select(h => h.Points).ToImmutableArray(), markup.StrokeWidth1080, markup.ColorHex, markup.KeylineActive)
        {
            MarkedAt = markup.Holds.Select(h => h.SourceTime).ToImmutableArray()
        }
        : null;

    /// <summary>The outline of <paramref name="markup"/> on a clip at <paramref name="clipStart"/> / <paramref name="sourceIn"/>
    /// / <paramref name="speed"/>, with its animation's schedule (D036) at <paramref name="rate"/>.</summary>
    public static HoldOutline? Of(HoldMarkup? markup, MediaTime clipStart, MediaTime sourceIn, ClipSpeed speed, FrameRate rate)
    {
        if (Of(markup) is not { } outline) return null;
        if (markup!.Animation is not { } animation) return outline;
        var start = HoldAnimationRule.StartFrame(clipStart, sourceIn, speed, animation.StartSourceTime, rate);
        return outline with { Schedule = HoldAnimationRule.Schedule(animation, markup.Holds, start, rate) };
    }
}

/// <summary>A text clip on a visible video track (renderer-neutral content and style, D018).</summary>
public sealed record TextSpan(Guid ClipId, MediaTime TimelineStart, MediaTime TimelineEnd, VisualProperties Visual, TextProperties Text)
{
    /// <summary>Effective fade lengths in frames (D025 §2): presentation only.</summary>
    public long FadeInFrames { get; init; }
    public long FadeOutFrames { get; init; }
}

/// <summary>A template on a visible video track (D045): its kind, fields and the counter's value at its start; the
/// appearance and disappearance are its effective fades (frames, D025 §2).</summary>
public sealed record TemplateSpan(Guid ClipId, MediaTime TimelineStart, MediaTime TimelineEnd, TemplateKind Kind,
    TemplateProperties Fields, MediaTime CounterStart)
{
    public long FadeInFrames { get; init; }
    public long FadeOutFrames { get; init; }
}

/// <summary>
/// A cross dissolve on a visible track (D025 §3): the zone <c>[Start, End)</c> of <see cref="Frames"/> frames around
/// the cut <see cref="Cut"/> — <c>⌊F/2⌋</c> frames of A before it, <c>⌈F/2⌉</c> of B after it. In the zone both clips
/// are layers of the track, A below B, B at its opacity × <c>(j + 1)/(F + 1)</c> for zone frame <c>j</c>.
/// </summary>
public sealed record DissolveZone(Guid LeftClipId, Guid RightClipId, MediaTime Start, MediaTime Cut, MediaTime End, long Frames)
{
    /// <summary>The transition's type (D044 §1): how the zone is drawn (<see cref="TransitionPresentation"/>).</summary>
    public string TypeId { get; init; } = TransitionRules.CrossDissolve;

    /// <summary>A push's direction; null for every other type.</summary>
    public TransitionDirection? Direction { get; init; }

    /// <summary>True when both clips are shown past the cut, from their source handles (Dissolve, Push — D044 §3).</summary>
    public bool ExtendsPictures => TransitionRules.RequiresHandles(TypeId);

    public bool Contains(MediaTime time) => time >= Start && time < End;
}

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

    /// <summary>The clip's speed (D022): part of the timing, so a change is never mix-only.</summary>
    public ClipSpeed Speed { get; init; }

    /// <summary>Effective fade lengths in frames (D025 §2, those of the clip's picture): mix only
    /// (<see cref="AudioFadeEnvelope"/>).</summary>
    public long FadeInFrames { get; init; }
    public long FadeOutFrames { get; init; }

    /// <summary>The crossfade at the clip's start (D032 R9): the clip plays from the zone's start, from its source handle,
    /// fading in over the zone. Null without one.</summary>
    public CrossfadeZone? CrossfadeIn { get; init; }

    /// <summary>The crossfade at the clip's end: the clip plays on to the zone's end, fading out over the zone.</summary>
    public CrossfadeZone? CrossfadeOut { get; init; }

    /// <summary>Where the clip's sound starts: its start, or the start of its crossfade-in zone.</summary>
    public MediaTime PlayStart => CrossfadeIn?.Start ?? TimelineStart;

    /// <summary>Where the clip's sound ends: its end, or the end of its crossfade-out zone.</summary>
    public MediaTime PlayEnd => CrossfadeOut?.End ?? TimelineEnd;
}

/// <summary>A crossfade's zone on an audio track (D032 R9): <c>[Start, End)</c>, the D025 zone of its frames around the
/// cut.</summary>
public readonly record struct CrossfadeZone(MediaTime Start, MediaTime End);

/// <summary>A visible video track: its media clips (<see cref="Spans"/>) and text clips
/// (<see cref="Texts"/>), each in timeline order; no two of them overlap (as on the track).</summary>
public sealed record VideoLayer(Guid TrackId, ImmutableArray<PictureSpan> Spans)
{
    public ImmutableArray<TextSpan> Texts { get; init; } = ImmutableArray<TextSpan>.Empty;

    /// <summary>The track's templates (D045) in timeline order.</summary>
    public ImmutableArray<TemplateSpan> Templates { get; init; } = ImmutableArray<TemplateSpan>.Empty;

    /// <summary>The track's dissolves in timeline order (their zones never overlap, D025).</summary>
    public ImmutableArray<DissolveZone> Dissolves { get; init; } = ImmutableArray<DissolveZone>.Empty;
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
    /// Inside a dissolve zone (D025 §3) the track contributes both clips of the cut, A below B, B at its opacity ×
    /// <c>(j + 1)/(F + 1)</c>; each keeps its fade on its free edge (the cut's edges have none, PO-8).
    /// </summary>
    public ImmutableArray<CompositionLayer> LayersAt(MediaTime time, Func<PictureLayer, bool>? mayOcclude = null)
    {
        var topDown = new List<CompositionLayer>();
        long? frame = null;
        foreach (var layer in VideoLayers) // topmost first
        {
            var zoneAt = layer.Dissolves.Length > 0 ? FindZone(layer.Dissolves, time) : null;
            if (zoneAt is { } library && library.TypeId != TransitionRules.CrossDissolve)
            {
                // D044 §2: the library's other types; true when what they drew hides every track below.
                if (LibraryZoneLayers(layer, library, time, topDown, mayOcclude, ref frame)) break;
                continue;
            }
            if (zoneAt is { } zone)
            {
                // D025 §3: both clips of the cut, A below B; B at its opacity × (j + 1)/(F + 1). B never occludes (the
                // factor is below 1), A may occlude the tracks below as usual.
                frame ??= time.ToFrameFloor(FrameRate);
                var p = FadeRule.Ramp(frame.Value - zone.Start.ToFrameFloor(FrameRate), zone.Frames);
                if (ClipLayer(layer, zone.Cut, time, p, ref frame) is { } b) topDown.Add(b);
                if (ClipLayer(layer, new MediaTime(zone.Cut.Ticks - 1), time, 1.0, ref frame) is { } a)
                {
                    topDown.Add(a);
                    if (a is PictureLayer { OccludesBelow: true } occluder && (mayOcclude?.Invoke(occluder) ?? true)) break;
                }
                continue;
            }

            if (Find(layer.Spans, time) is { } picture)
            {
                if (picture.Visual.Opacity == 0) continue;
                var geometry = picture.SourceSize is { } size ? CompositionMath.Layout(Canvas, size, picture.Visual) : null;
                var fade = FadeAt(picture.TimelineStart, picture.TimelineEnd, picture.FadeInFrames, picture.FadeOutFrames, time, ref frame);
                var pictureLayer = WithOutlineOpacities(new PictureLayer(layer.TrackId, picture, geometry, fade), time, ref frame);
                topDown.Add(pictureLayer);
                if (pictureLayer.OccludesBelow && (mayOcclude?.Invoke(pictureLayer) ?? true)) break;
            }
            else if (FindText(layer.Texts, time) is { } text && text.Visual.Opacity > 0 && !string.IsNullOrWhiteSpace(text.Text.Text))
            {
                var fade = FadeAt(text.TimelineStart, text.TimelineEnd, text.FadeInFrames, text.FadeOutFrames, time, ref frame);
                topDown.Add(new TextLayer(layer.TrackId, text, TextTransformAt(text, time, ref frame), fade));
            }
            else if (layer.Templates.Length > 0 && FindTemplate(layer.Templates, time) is { } template)
            {
                // D045: a template never hides what is below it — it draws over it (and may treat it, VHS).
                frame ??= time.ToFrameFloor(FrameRate);
                topDown.Add(new TemplateLayer(layer.TrackId, template, TemplateClock.At(template, frame.Value, FrameRate)));
            }
        }

        topDown.Reverse();
        return topDown.ToImmutableArray();
    }

    /// <summary>The layer of the clip of <paramref name="layer"/> that covers <paramref name="at"/>, shown at
    /// <paramref name="time"/> (inside a dissolve zone, possibly outside the clip) with its fade × <paramref name="factor"/>;
    /// null when there is none or it draws nothing (opacity 0, empty text).</summary>
    private CompositionLayer? ClipLayer(VideoLayer layer, MediaTime at, MediaTime time, double factor, ref long? frame)
    {
        if (Find(layer.Spans, at) is { } picture)
        {
            if (picture.Visual.Opacity == 0) return null;
            var geometry = picture.SourceSize is { } size ? CompositionMath.Layout(Canvas, size, picture.Visual) : null;
            var fade = FadeAt(picture.TimelineStart, picture.TimelineEnd, picture.FadeInFrames, picture.FadeOutFrames, time, ref frame);
            return WithOutlineOpacities(new PictureLayer(layer.TrackId, picture, geometry, fade * factor), time, ref frame);
        }
        if (FindText(layer.Texts, at) is { } text && text.Visual.Opacity > 0 && !string.IsNullOrWhiteSpace(text.Text.Text))
        {
            var fade = FadeAt(text.TimelineStart, text.TimelineEnd, text.FadeInFrames, text.FadeOutFrames, time, ref frame);
            return new TextLayer(layer.TrackId, text, TextTransformAt(text, time, ref frame), fade * factor);
        }
        return null;
    }

    /// <summary>
    /// The layers of a library zone (D044 §2), added to <paramref name="topDown"/> (topmost first); true when they hide
    /// every track below. Dip to black / Flash and Zoom show only the clip on its own frames — A before the cut, B from it
    /// — Dip to black / Flash with their fill above it; Push shows both clips from their handles, B above A, each shifted
    /// (shifted layers never hide what is below).
    /// </summary>
    private bool LibraryZoneLayers(VideoLayer layer, DissolveZone zone, MediaTime time, List<CompositionLayer> topDown,
        Func<PictureLayer, bool>? mayOcclude, ref long? frame)
    {
        frame ??= time.ToFrameFloor(FrameRate);
        var j = frame.Value - zone.Start.ToFrameFloor(FrameRate);
        var own = time < zone.Cut ? new MediaTime(zone.Cut.Ticks - 1) : zone.Cut;   // the clip whose frame this is

        switch (zone.TypeId)
        {
            case TransitionRules.Push:
            {
                var (motionA, motionB) = TransitionPresentation.PushMotions(j, zone.Frames, zone.Direction ?? TransitionDirection.FromRight, Canvas);
                if (ClipLayer(layer, zone.Cut, time, 1.0, ref frame) is { } b) topDown.Add(b with { Motion = motionB });
                if (ClipLayer(layer, new MediaTime(zone.Cut.Ticks - 1), time, 1.0, ref frame) is { } a) topDown.Add(a with { Motion = motionA });
                return false;
            }
            case TransitionRules.Zoom:
            {
                if (ClipLayer(layer, own, time, 1.0, ref frame) is not { } clip) return false;
                var zoomed = clip with { Motion = new CanvasMotion(TransitionPresentation.ZoomScale(j, zone.Frames), 0, 0) };
                topDown.Add(zoomed);
                return zoomed is PictureLayer { OccludesBelow: true } occluder && (mayOcclude?.Invoke(occluder) ?? true);
            }
            default:   // Dip to black, Flash
            {
                var fill = new SolidLayer(layer.TrackId, TransitionPresentation.FillColor(zone.TypeId),
                    TransitionPresentation.FillOpacity(j, zone.Frames));
                topDown.Add(fill);
                if (fill.OccludesBelow) return true;
                if (ClipLayer(layer, own, time, 1.0, ref frame) is not { } clip) return false;
                topDown.Add(clip);
                return clip is PictureLayer { OccludesBelow: true } occluder && (mayOcclude?.Invoke(occluder) ?? true);
            }
        }
    }

    /// <summary>D036: an animated outline's opacity per hold on the frame of <paramref name="time"/> — the one value the
    /// Preview and the export draw (inside a dissolve zone the frame index simply runs on past the clip).</summary>
    private PictureLayer WithOutlineOpacities(PictureLayer picture, MediaTime time, ref long? frame)
    {
        if (picture.Span.Outline?.Schedule is not { } schedule) return picture;
        frame ??= time.ToFrameFloor(FrameRate);
        return picture with { OutlineOpacities = schedule.OpacitiesAt(frame.Value) };
    }

    private static DissolveZone? FindZone(ImmutableArray<DissolveZone> zones, MediaTime time)
    {
        int lo = 0, hi = zones.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var zone = zones[mid];
            if (time < zone.Start) hi = mid - 1;
            else if (time >= zone.End) lo = mid + 1;
            else return zone;
        }
        return null;
    }

    /// <summary>The fade factor (D025 §2) of a clip at <paramref name="time"/>, the start of a timeline frame: exactly
    /// 1 without a fade. <paramref name="frame"/> caches the frame index of <paramref name="time"/>.</summary>
    /// <summary>A text's D018 transform with its appearance / disappearance motion on the frame of <paramref name="time"/>
    /// (D043 §1, <see cref="TextMotionRule"/>); exactly the D018 transform for a text without motion.</summary>
    private Affine2D TextTransformAt(TextSpan text, MediaTime time, ref long? frame)
    {
        var transform = CompositionMath.TextTransform(Canvas, text.Visual);
        if (text.Text.MotionIn.Kind == TextMotionKind.None && text.Text.MotionOut.Kind == TextMotionKind.None) return transform;
        frame ??= time.ToFrameFloor(FrameRate);
        return TextMotionRule.Apply(transform, text.Text, Canvas, frame.Value, text.TimelineStart.ToFrameFloor(FrameRate),
            text.TimelineEnd.ToFrameFloor(FrameRate), text.FadeInFrames, text.FadeOutFrames);
    }

    private double FadeAt(MediaTime start, MediaTime end, long fadeIn, long fadeOut, MediaTime time, ref long? frame)
    {
        if (fadeIn == 0 && fadeOut == 0) return 1.0;
        frame ??= time.ToFrameFloor(FrameRate);
        return FadeRule.PictureFactor(frame.Value, start.ToFrameFloor(FrameRate), end.ToFrameFloor(FrameRate), fadeIn, fadeOut);
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

    /// <summary>Earliest clip edge — or edge of a fade ramp (D025 §2: a fading picture stops hiding the layers below)
    /// — on any visible track after <paramref name="time"/>: where the visible picture may change;
    /// <see cref="Duration"/> if there is none.</summary>
    public MediaTime NextPictureChange(MediaTime time)
    {
        var next = Duration;
        foreach (var layer in VideoLayers)
        {
            foreach (var span in layer.Spans)
            {
                if (span.TimelineStart > time && span.TimelineStart < next) next = span.TimelineStart;
                if (span.TimelineEnd > time && span.TimelineEnd < next) next = span.TimelineEnd;
                if (span.TimelineEnd <= time) continue;
                foreach (var edge in FadeRule.RampEdges(span.TimelineStart, span.TimelineEnd, span.FadeInFrames, span.FadeOutFrames, FrameRate))
                    if (edge > time && edge < next) next = edge;
            }
            foreach (var zone in layer.Dissolves)   // B appears at the zone's start, A leaves at its end (D025 §3)
            {
                if (zone.Start > time && zone.Start < next) next = zone.Start;
                if (zone.End > time && zone.End < next) next = zone.End;
            }
        }
        return next;
    }

    /// <summary>
    /// True when this snapshot and <paramref name="other"/> describe the same timeline for
    /// decoding — frame rate, duration, visible tracks, the timing and media of every clip, audio
    /// spans and assets — and differ at most in presentation: the mix (<see cref="AudioSpan.Gain"/>,
    /// <see cref="AudioSpan.IsMuted"/>), fades (D025), picture properties (<see cref="PictureSpan.Visual"/>,
    /// <see cref="PictureSpan.SourceSize"/>, the hold outline <see cref="PictureSpan.Outline"/>), text content/style, the canvas and
    /// their version.
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
                !a.Texts.Select(TextTiming).SequenceEqual(b.Texts.Select(TextTiming)) ||
                !a.Templates.Select(TemplateTiming).SequenceEqual(b.Templates.Select(TemplateTiming)) ||
                // A transition changes which frames are decoded; its type and direction alone don't — the shown range of
                // the pictures (compared with the spans) says whether the handles are used (D044 §10).
                !a.Dissolves.Select(Placement).SequenceEqual(b.Dissolves.Select(Placement)))
                return false;
        }

        if (AudioSpans.Length != other.AudioSpans.Length) return false;
        for (var i = 0; i < AudioSpans.Length; i++)
        {
            if (WithoutMix(AudioSpans[i]) != WithoutMix(other.AudioSpans[i])) return false;
        }

        return Assets.Count == other.Assets.Count &&
               Assets.All(pair => other.Assets.TryGetValue(pair.Key, out var asset) && asset == pair.Value);

        static AudioSpan WithoutMix(AudioSpan span) => span with { Gain = 0, IsMuted = false, FadeInFrames = 0, FadeOutFrames = 0 };
        static PictureSpan WithoutPresentation(PictureSpan span) =>
            span with { Visual = VisualProperties.Default, SourceSize = null, FadeInFrames = 0, FadeOutFrames = 0, Outline = null };
        static (Guid, MediaTime, MediaTime) TextTiming(TextSpan span) => (span.ClipId, span.TimelineStart, span.TimelineEnd);
        static (Guid, MediaTime, MediaTime) TemplateTiming(TemplateSpan span) => (span.ClipId, span.TimelineStart, span.TimelineEnd);
        static DissolveZone Placement(DissolveZone zone) => zone with { TypeId = TransitionRules.CrossDissolve, Direction = null };
    }

    private static TemplateSpan? FindTemplate(ImmutableArray<TemplateSpan> spans, MediaTime time)
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
