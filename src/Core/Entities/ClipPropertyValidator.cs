using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using static AiVideoEditor.Core.Entities.ClipPropertyLimits;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// Checks a <see cref="ClipPropertyChange"/> against the clip's kind and
/// <see cref="ClipPropertyLimits"/>. Returns a short user-facing reason for the first problem,
/// or null when the change can be applied. Every number must be finite. The same rules apply
/// to edits (<see cref="ITimelineEditService.SetClipProperties"/>) and to clips loaded from a
/// project file (<see cref="ValidateCurrent"/>).
/// </summary>
public static class ClipPropertyValidator
{
    public static string? Validate(Clip clip, ClipPropertyChange change)
    {
        if (change.Visual is { } visual)
        {
            if (VisualProperties.Of(clip) is null) return "Audio clips have no picture properties.";
            if (ValidateVisual(visual, allowCrop: clip is not TextClip) is { } error) return error;
        }

        if (change.Audio is { } audio)
        {
            if (AudioProperties.Of(clip) is null) return $"{KindName(clip)} clips have no sound.";
            if (!InRange(audio.Volume, MinVolume, MaxVolume))
                return $"Volume must be between {Pct(MinVolume)} and {Pct(MaxVolume)}.";
        }

        if (change.Text is { } text)
        {
            if (clip is not TextClip) return "Only text clips have text properties.";
            if (ValidateText(text) is { } error) return error;
        }

        return null;
    }

    /// <summary>Checks the values a clip currently holds, e.g. right after it was read from a file.</summary>
    public static string? ValidateCurrent(Clip clip) => Validate(clip, new ClipPropertyChange
    {
        Visual = VisualProperties.Of(clip),
        Audio = AudioProperties.Of(clip),
        Text = TextProperties.Of(clip)
    });

    /// <summary>Checks picture properties on their own; <paramref name="allowCrop"/> is false for text.</summary>
    public static string? ValidateVisual(VisualProperties v, bool allowCrop)
    {
        if (!InRange(v.PositionX, -MaxPositionMagnitude, MaxPositionMagnitude) ||
            !InRange(v.PositionY, -MaxPositionMagnitude, MaxPositionMagnitude))
            return $"Position must be between {Num(-MaxPositionMagnitude)} and {Num(MaxPositionMagnitude)} pixels.";

        if (!InRange(v.Scale, MinScale, MaxScale))
            return $"Scale must be between {Pct(MinScale)} and {Pct(MaxScale)}.";

        if (!InRange(v.RotationDegrees, MinRotationDegrees, MaxRotationDegrees))
            return $"Rotation must be between {Num(MinRotationDegrees)}° and {Num(MaxRotationDegrees)}°.";

        if (!InRange(v.Opacity, MinOpacity, MaxOpacity))
            return $"Opacity must be between {Pct(MinOpacity)} and {Pct(MaxOpacity)}.";

        var crop = v.Crop;
        if (!allowCrop)
            return crop == CropRect.None ? null : "Text clips can't be cropped.";

        if (!IsInset(crop.Left) || !IsInset(crop.Top) || !IsInset(crop.Right) || !IsInset(crop.Bottom))
            return "Each crop edge must be between 0 % and 100 %.";

        if (crop.Left + crop.Right >= MaxCropSum || crop.Top + crop.Bottom >= MaxCropSum)
            return "Crop would leave nothing of the picture.";

        return null;
    }

    private static string? ValidateText(TextProperties t)
    {
        if (t.Text is null) return "Text is missing.";
        if (t.Text.Length > MaxTextLength) return $"Text can be at most {MaxTextLength} characters long.";

        if (string.IsNullOrWhiteSpace(t.FontFamily)) return "Choose a font.";
        if (t.FontFamily.Length > MaxFontFamilyLength) return "The font name is too long.";

        if (!InRange(t.FontSize, MinFontSize, MaxFontSize))
            return $"Font size must be between {Num(MinFontSize)} and {Num(MaxFontSize)}.";

        if (!IsHexColor(t.ColorHex)) return "Color must be written as #RRGGBB.";

        if (!Enum.IsDefined(t.Alignment)) return "Unknown text alignment.";

        return null;
    }

    private static bool InRange(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    private static bool IsInset(double value) => double.IsFinite(value) && value >= 0 && value < 1;

    /// <summary>Exactly <c>#RRGGBB</c> (either case).</summary>
    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pct(double fraction) => Num(fraction * 100) + " %";

    private static string KindName(Clip clip) => clip switch
    {
        ImageClip => "Image",
        TextClip => "Text",
        _ => "These"
    };
}
