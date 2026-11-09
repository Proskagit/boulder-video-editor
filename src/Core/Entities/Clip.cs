using System.Collections.Immutable;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// Base type for anything that occupies a span of time on a <see cref="Track"/>.
/// Moving/trimming a clip only ever changes these values (project state) —
/// it never re-encodes the underlying media. Encoding only happens at export time.
/// </summary>
public abstract class Clip
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Position of the clip's start on the timeline.</summary>
    public MediaTime TimelineStart { get; set; }

    /// <summary>How much of the source (or generated content) is used, after trimming.</summary>
    public MediaTime Duration { get; set; }

    public MediaTime TimelineEnd => TimelineStart + Duration;

    public bool IsSelected { get; set; }

    /// <summary>Fade in at the clip's start (D025): a duration (≥ 0) covering
    /// <c>FadeIn.ToNearestFrame(rate)</c> timeline frames, clamped to the clip when rendered. Applies to the
    /// clip's picture and to its own sound.</summary>
    public MediaTime FadeIn { get; set; }

    /// <summary>Fade out at the clip's end (D025), as <see cref="FadeIn"/>.</summary>
    public MediaTime FadeOut { get; set; }

    public List<Effect> Effects { get; } = new();
}

/// <summary>A clip backed by a <see cref="MediaAsset"/> (video, audio or image source).</summary>
public abstract class MediaBackedClip : Clip
{
    public required Guid MediaAssetId { get; set; }

    /// <summary>In-point within the source media (trim left).</summary>
    public MediaTime SourceIn { get; set; }

    /// <summary>Out-point within the source media (trim right). At 1×,
    /// SourceOut − SourceIn == Duration; at other speeds the duration is the whole number of
    /// frames the source range allows (<see cref="SpeedTiming"/>, D022).</summary>
    public MediaTime SourceOut { get; set; }

    /// <summary>Playback speed (D022): exact multiples of 0.05 from 0.25× to 4× for video and audio
    /// clips; always 1× for images.</summary>
    public ClipSpeed Speed { get; set; }

    /// <summary>The explicit link of a video clip and an audio clip (D032 R1): the two clips sharing a value form one
    /// link (<see cref="LinkRules"/>); null when the clip is not linked. Never set on images.</summary>
    public Guid? LinkId { get; set; }
}

public sealed class VideoClip : MediaBackedClip
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; } = 1.0;
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; } = 1.0;

    /// <summary>Linear gain of the clip's own audio (1.0 = unchanged).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Silences the clip's own audio without touching <see cref="Volume"/>.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Crop expressed as normalized (0..1) insets from each edge.</summary>
    public CropRect Crop { get; set; } = CropRect.None;

    /// <summary>The clip's own sound was unlinked into an <see cref="AudioClip"/> (D032 R1, R3): the clip produces no
    /// sound; <see cref="Volume"/> and <see cref="IsMuted"/> are kept but unused.</summary>
    public bool IsAudioDetached { get; set; }

    /// <summary>D053 (format v15): the clip's routes — each an independent hold markup (D034: its confirmed holds, the
    /// outline's style, its animation) with its own identity; empty = none. Immutable values, each replaced as a whole by
    /// every edit of its route; Split, Duplicate and Paste copy them.</summary>
    public ImmutableArray<HoldMarkup> Routes { get; set; } = ImmutableArray<HoldMarkup>.Empty;

    /// <summary>The single route of a clip (the model before D053): its first route, or null. Setting it replaces every
    /// route by that one. Kept for single-route code and tests only — the editor's code addresses routes by
    /// <see cref="HoldMarkup.Id"/> (<see cref="RouteOverlays"/>).</summary>
    [Obsolete("D053: a clip has a list of routes — use Routes / RouteOverlays.")]
    public HoldMarkup? HoldMarkup
    {
        get => Routes.IsDefaultOrEmpty ? null : Routes[0];
        set => Routes = value is null ? ImmutableArray<HoldMarkup>.Empty : ImmutableArray.Create(value);
    }
}

/// <summary>A clip on an audio track: the sound of an audio file, or (D032 R1) the sound of a video file.</summary>
public sealed class AudioClip : MediaBackedClip
{
    public double Volume { get; set; } = 1.0;
    public bool IsMuted { get; set; }
}

public sealed class ImageClip : MediaBackedClip
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; } = 1.0;
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; } = 1.0;
    public CropRect Crop { get; set; } = CropRect.None;
}

/// <summary>A generated (non-media-backed) text overlay clip.</summary>
public sealed class TextClip : Clip
{
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 48;
    public string ColorHex { get; set; } = "#FFFFFF";
    public TextAlignment Alignment { get; set; } = TextAlignment.Center;
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; } = 1.0;
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; } = 1.0;

    /// <summary>D043 §3: the bold weight of the font. A text of an older project is not bold.</summary>
    public bool Bold { get; set; }

    /// <summary>D043 §2: «Обводка» (off by default; its values kept while off).</summary>
    public TextOutline Outline { get; set; } = TextOutline.Default;

    /// <summary>D043 §2: «Подложка» (off by default; its values kept while off).</summary>
    public TextBackground Background { get; set; } = TextBackground.Default;

    /// <summary>D043 §1: the motion of the appearance over <see cref="Clip.FadeIn"/> (none by default).</summary>
    public TextMotion MotionIn { get; set; } = TextMotion.None;

    /// <summary>D043 §1: the motion of the disappearance over <see cref="Clip.FadeOut"/> (none by default).</summary>
    public TextMotion MotionOut { get; set; } = TextMotion.None;
}

/// <summary>
/// A ready-made visual template (D045, Phase 29): a generated clip on a video track that draws its look over everything
/// below its track for its time — REC, VHS, «Боулдер», «Хайлайт». Its appearance and disappearance last
/// <see cref="Clip.FadeIn"/> / <see cref="Clip.FadeOut"/> (set from <see cref="TemplateRules"/>; a split leaves them on the
/// outer edges, D025); it has no picture properties, no sound and no speed. The fields a kind doesn't use are kept as they are.
/// </summary>
public sealed class TemplateClip : Clip
{
    public TemplateKind Kind { get; set; }

    /// <summary>REC / VHS: the counter's value at the clip's start; the right part of a split continues it.</summary>
    public MediaTime CounterStart { get; set; }

    /// <summary>REC / VHS: the date shown (optional; empty hides it).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>«Боулдер»: the route («7») and the grade («7A»), each optional.</summary>
    public string Route { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;

    /// <summary>«Боулдер»: the status the user sets by hand — never detected.</summary>
    public BoulderStatus Status { get; set; }

    /// <summary>«Боулдер»: the moment the status block appears, on the template's own clock (that of
    /// <see cref="CounterStart"/>: the time since the template's original start, continued over a split) — set by the user
    /// from the playhead. Before it only the route block shows; from it the status appears and stays to the template's end.
    /// Never moved by a trim or a split; beyond the template's end the status doesn't appear.</summary>
    public MediaTime StatusAt { get; set; }

    /// <summary>«Боулдер»: the accent (the route's colour, chosen by the user).</summary>
    public string AccentHex { get; set; } = TemplateRules.DefaultAccentHex;

    /// <summary>«Хайлайт»: the title and an optional subtitle.</summary>
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>The heart-rate overlay (Phase 35.5, format v13): its look.</summary>
    public HeartRateLook Look { get; set; } = HeartRateLook.Plate;

    /// <summary>The heart-rate overlay: the corner of the canvas it stands in.</summary>
    public OverlayCorner Corner { get; set; } = OverlayCorner.BottomRight;

    /// <summary>D055 §5 (format v16): whether a «Боулдер» shows the heart rate of its anchored clip on its span — on by
    /// default, also for an older project's areas. With <see cref="Look"/>, <see cref="Corner"/> and
    /// <see cref="HeartHex"/> the area's own heart-rate settings (those of the heart-rate overlay).</summary>
    public bool ShowHeartRate { get; set; } = true;

    /// <summary>D055 §5 (format v16): a «Боулдер»'s heart colour (its <see cref="AccentHex"/> is the route's colour). The
    /// heart-rate overlay keeps its heart in <see cref="AccentHex"/> (Phase 35.5).</summary>
    public string HeartHex { get; set; } = TemplateRules.HeartRateAccentHex;

    /// <summary>D048 §3.5 (format v10): the video clip this template belongs to — moved, copied and deleted with it, its
    /// intervals kept inside it; null: a free template (D045). One-directional: a video clip may have several.</summary>
    public Guid? AnchorClipId { get; set; }

    /// <summary>D053 (format v15): the route of <see cref="AnchorClipId"/> this template belongs to — the template is the
    /// route's green (and lilac); the route's holds and animation, when it has them, are the clip's markup with this id
    /// (<see cref="VideoClip.Routes"/>). Null: the clip's single markup, when it has exactly one (see
    /// <see cref="RouteOverlays.RouteIdOf(TemplateClip, VideoClip)"/>).</summary>
    public Guid? AnchorRouteId { get; set; }

    /// <summary>D053 §6 (format v15): the FIT climb this route's green was created for by the FIT import; null for a route
    /// made by hand. A new import of the same FIT never creates a second route for the same climb.</summary>
    public Core.Telemetry.ClimbIdentity? FitClimb { get; set; }

    /// <summary>D056 §2 (format v17): the bounds a route area made from the FIT was laid out with; an area whose bounds are
    /// still these is laid out again when its video's <c>Z</c> changes. Null: not made from the FIT, or made before v17 (an
    /// older file's area is laid out again only when its bounds are the ones the FIT's rule gives now).</summary>
    public FitAreaLayout? FitLayout { get; set; }
}

/// <summary>D056 §2: how a route area made from the FIT was laid out — its first frame and its end as source times of its
/// anchored clip (kept when the clip moves, trims or splits elsewhere) and its status moment on its own clock.</summary>
public sealed record FitAreaLayout(MediaTime SourceStart, MediaTime SourceEnd, MediaTime StatusAt);

/// <summary>The four templates of Phase 29 (D045 §1; WASTED deferred, §15) and the heart-rate overlay of Phase 35.5 (D050:
/// a heart icon with the BPM of the picture below it, from the project's training).</summary>
public enum TemplateKind
{
    Rec,
    Vhs,
    Boulder,
    Highlight,
    HeartRate
}

/// <summary>The heart-rate overlay's look: on a dark plate, or the icon and the digits alone with a soft shadow.</summary>
public enum HeartRateLook
{
    Plate,
    Plain
}

/// <summary>A corner of the canvas, at the safe margin of the placement grid (D043 §3).</summary>
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>«Боулдер»'s status (D045 §1.3): none, TOP (the route done) or ПОПЫТКА (an attempt's episode).</summary>
public enum BoulderStatus
{
    None,
    Top,
    Attempt
}

public enum TextAlignment
{
    Left,
    Center,
    Right
}

public readonly record struct CropRect(double Left, double Top, double Right, double Bottom)
{
    public static readonly CropRect None = new(0, 0, 0, 0);
}

/// <summary>An applied effect instance on a clip. Parameters are kept generic so new
/// effect types can be added without changing the Inspector or the Clip model.</summary>
public sealed class Effect
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string EffectTypeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>A transition on the cut between two adjacent clips of one video track (D025): A
/// (<see cref="LeftClipId"/>) ends exactly where B (<see cref="RightClipId"/>) starts. The rules are
/// <see cref="TransitionRules"/>.</summary>
public sealed class Transition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TransitionTypeId { get; set; }

    /// <summary>Length of the transition zone: <c>Duration.ToNearestFrame(rate)</c> timeline frames, centred on
    /// the cut.</summary>
    public MediaTime Duration { get; set; }

    /// <summary>The clip left of the cut (A).</summary>
    public Guid LeftClipId { get; set; }

    /// <summary>The clip right of the cut (B).</summary>
    public Guid RightClipId { get; set; }

    /// <summary>The side B comes from — set for a <see cref="TransitionRules.Push"/> only, null for every other type
    /// (D044 §1, §9).</summary>
    public TransitionDirection? Direction { get; set; }
}

/// <summary>The side the incoming clip (B) of a <see cref="TransitionRules.Push"/> comes from (D044 §1).</summary>
public enum TransitionDirection
{
    FromRight,
    FromLeft,
    FromTop,
    FromBottom
}

/// <summary>A named point of interest on the timeline (not tied to a specific track).</summary>
public sealed class Marker
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaTime Position { get; set; }
    public string Label { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4FC3F7";
}
