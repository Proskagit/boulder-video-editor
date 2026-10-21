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

    /// <summary>The clip's hold markup (D034): its confirmed holds and the route outline's style; null = none. An
    /// immutable value, replaced as a whole by every edit; Split, Duplicate and Paste copy it.</summary>
    public HoldMarkup? HoldMarkup { get; set; }
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
}

/// <summary>A named point of interest on the timeline (not tied to a specific track).</summary>
public sealed class Marker
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaTime Position { get; set; }
    public string Label { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4FC3F7";
}
