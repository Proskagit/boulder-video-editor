namespace AiVideoEditor.Core.Entities;

/// <summary>
/// Picture properties of a clip that has a picture (video, image, text). Values are absolute
/// and stored exactly as given — nothing is derived or rounded. Text clips have no crop
/// (always <see cref="CropRect.None"/>).
/// </summary>
public readonly record struct VisualProperties(
    double PositionX, double PositionY, double Scale, double RotationDegrees, double Opacity, CropRect Crop)
{
    /// <summary>Centred, fitted, unrotated, opaque, uncropped — the values of a new clip.</summary>
    public static readonly VisualProperties Default = new(0, 0, 1, 0, 1, CropRect.None);

    /// <summary>The clip's current values, or null when the clip has no picture (audio).</summary>
    public static VisualProperties? Of(Clip clip) => clip switch
    {
        VideoClip v => new VisualProperties(v.PositionX, v.PositionY, v.Scale, v.RotationDegrees, v.Opacity, v.Crop),
        ImageClip i => new VisualProperties(i.PositionX, i.PositionY, i.Scale, i.RotationDegrees, i.Opacity, i.Crop),
        TextClip t => new VisualProperties(t.PositionX, t.PositionY, t.Scale, t.RotationDegrees, t.Opacity, CropRect.None),
        _ => null
    };
}

/// <summary>Sound properties of a clip that can carry audio (video, audio). Mute is its own
/// state: muting never changes <see cref="Volume"/>.</summary>
public readonly record struct AudioProperties(double Volume, bool IsMuted)
{
    /// <summary>The clip's current values, or null when the clip has no audio (image, text).</summary>
    public static AudioProperties? Of(Clip clip) => clip switch
    {
        VideoClip v => new AudioProperties(v.Volume, v.IsMuted),
        AudioClip a => new AudioProperties(a.Volume, a.IsMuted),
        _ => null
    };
}

/// <summary>Content and style of a text clip. <see cref="Text"/> may span several lines.</summary>
public readonly record struct TextProperties(string Text, string FontFamily, double FontSize, string ColorHex, TextAlignment Alignment)
{
    /// <summary>The clip's current values, or null for clips that aren't text.</summary>
    public static TextProperties? Of(Clip clip) => clip is TextClip t
        ? new TextProperties(t.Text, t.FontFamily, t.FontSize, t.ColorHex, t.Alignment)
        : null;
}

/// <summary>Allowed ranges for clip properties (all inclusive unless noted). Shared by the
/// edit service's validation and the Inspector's input limits.</summary>
public static class ClipPropertyLimits
{
    /// <summary>Position offsets in canvas pixels.</summary>
    public const double MaxPositionMagnitude = 100_000;

    public const double MinScale = 0.01;
    public const double MaxScale = 10;

    public const double MinRotationDegrees = -360;
    public const double MaxRotationDegrees = 360;

    public const double MinOpacity = 0;
    public const double MaxOpacity = 1;

    /// <summary>Each crop inset is in [0, 1); opposite insets together stay below 1 so
    /// part of the picture remains.</summary>
    public const double MaxCropSum = 1;

    /// <summary>Linear gain: 0 = silent, 1 = unchanged, 2 = +6 dB (200 %).</summary>
    public const double MinVolume = 0;
    public const double MaxVolume = 2;

    /// <summary>Font size in canvas pixels.</summary>
    public const double MinFontSize = 1;
    public const double MaxFontSize = 1000;

    public const int MaxFontFamilyLength = 256;
    public const int MaxTextLength = 10_000;
}
