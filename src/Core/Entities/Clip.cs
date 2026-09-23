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

    public List<Effect> Effects { get; } = new();
}

/// <summary>A clip backed by a <see cref="MediaAsset"/> (video, audio or image source).</summary>
public abstract class MediaBackedClip : Clip
{
    public required Guid MediaAssetId { get; set; }

    /// <summary>In-point within the source media (trim left).</summary>
    public MediaTime SourceIn { get; set; }

    /// <summary>Out-point within the source media (trim right). At Speed 1.0,
    /// SourceOut - SourceIn == Duration.</summary>
    public MediaTime SourceOut { get; set; }

    /// <summary>Playback speed multiplier. 1.0 = normal. Timeline editing currently
    /// supports only 1.0 and rejects edits of clips with any other speed.</summary>
    public double Speed { get; set; } = 1.0;
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
}

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

/// <summary>A transition between two adjacent clips on the same track.</summary>
public sealed class Transition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TransitionTypeId { get; set; }
    public MediaTime Duration { get; set; }
}

/// <summary>A named point of interest on the timeline (not tied to a specific track).</summary>
public sealed class Marker
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaTime Position { get; set; }
    public string Label { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4FC3F7";
}
