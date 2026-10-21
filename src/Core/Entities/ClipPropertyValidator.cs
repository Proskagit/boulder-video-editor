using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
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
            if (VisualProperties.Of(clip) is null) return Messages.Prop_AudioNoPicture;
            if (ValidateVisual(visual, allowCrop: clip is not TextClip) is { } error) return error;
        }

        if (change.Audio is { } audio)
        {
            if (AudioProperties.Of(clip) is null) return string.Format(Messages.Prop_NoSound, KindName(clip));
            if (!InRange(audio.Volume, MinVolume, MaxVolume))
                return string.Format(Messages.Prop_Volume, Pct(MinVolume), Pct(MaxVolume));
        }

        if (change.Text is { } text)
        {
            if (clip is not TextClip) return Messages.Prop_OnlyText;
            if (ValidateText(text) is { } error) return error;
        }

        if (change.Template is { } template)
        {
            if (clip is not TemplateClip) return Messages.Prop_OnlyTemplate;
            if (TemplateRules.Validate(template) is { } error) return error;
        }

        return null;
    }

    /// <summary>Checks the values a clip currently holds, e.g. right after it was read from a file.</summary>
    public static string? ValidateCurrent(Clip clip) => Validate(clip, new ClipPropertyChange
    {
        Visual = VisualProperties.Of(clip),
        Audio = AudioProperties.Of(clip),
        Text = TextProperties.Of(clip),
        Template = TemplateProperties.Of(clip)
    });

    /// <summary>Checks picture properties on their own; <paramref name="allowCrop"/> is false for text.</summary>
    public static string? ValidateVisual(VisualProperties v, bool allowCrop)
    {
        if (!InRange(v.PositionX, -MaxPositionMagnitude, MaxPositionMagnitude) ||
            !InRange(v.PositionY, -MaxPositionMagnitude, MaxPositionMagnitude))
            return string.Format(Messages.Prop_Position, Num(-MaxPositionMagnitude), Num(MaxPositionMagnitude));

        if (!InRange(v.Scale, MinScale, MaxScale))
            return string.Format(Messages.Prop_Scale, Pct(MinScale), Pct(MaxScale));

        if (!InRange(v.RotationDegrees, MinRotationDegrees, MaxRotationDegrees))
            return string.Format(Messages.Prop_Rotation, Num(MinRotationDegrees), Num(MaxRotationDegrees));

        if (!InRange(v.Opacity, MinOpacity, MaxOpacity))
            return string.Format(Messages.Prop_Opacity, Pct(MinOpacity), Pct(MaxOpacity));

        var crop = v.Crop;
        if (!allowCrop)
            return crop == CropRect.None ? null : Messages.Prop_TextNoCrop;

        if (!IsInset(crop.Left) || !IsInset(crop.Top) || !IsInset(crop.Right) || !IsInset(crop.Bottom))
            return Messages.Prop_CropRange;

        if (crop.Left + crop.Right >= MaxCropSum || crop.Top + crop.Bottom >= MaxCropSum)
            return Messages.Prop_CropNothing;

        return null;
    }

    private static string? ValidateText(TextProperties t)
    {
        if (t.Text is null) return Messages.Prop_TextMissing;
        if (t.Text.Length > MaxTextLength) return string.Format(Messages.Prop_TextLength, MaxTextLength);

        if (string.IsNullOrWhiteSpace(t.FontFamily)) return "Choose a font.";
        if (t.FontFamily.Length > MaxFontFamilyLength) return Messages.Prop_FontNameLong;

        if (!InRange(t.FontSize, MinFontSize, MaxFontSize))
            return string.Format(Messages.Prop_FontSize, Num(MinFontSize), Num(MaxFontSize));

        if (!IsHexColor(t.ColorHex)) return Messages.Prop_ColorFormat;

        if (!Enum.IsDefined(t.Alignment)) return Messages.Prop_UnknownAlignment;

        // D043: the outline, the background and the motions — checked whether they are on or off (their values are kept).
        if (!IsHexColor(t.Outline.ColorHex) || !IsHexColor(t.Background.ColorHex)) return Messages.Prop_ColorFormat;
        if (!InRange(t.Outline.Thickness, MinOutlineThickness, MaxOutlineThickness))
            return string.Format(Messages.Prop_OutlineThickness, Pct(MinOutlineThickness), Pct(MaxOutlineThickness));
        if (!InRange(t.Background.Opacity, MinBackgroundOpacity, MaxBackgroundOpacity))
            return string.Format(Messages.Prop_BackgroundOpacity, Pct(MinBackgroundOpacity), Pct(MaxBackgroundOpacity));
        if (!IsMotion(t.MotionIn) || !IsMotion(t.MotionOut)) return Messages.Prop_UnknownMotion;

        return null;
    }

    private static bool IsMotion(TextMotion m) => Enum.IsDefined(m.Kind) && Enum.IsDefined(m.Side);

    private static bool InRange(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    private static bool IsInset(double value) => double.IsFinite(value) && value >= 0 && value < 1;

    /// <summary>Exactly <c>#RRGGBB</c> (either case).</summary>
    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pct(double fraction) => Num(fraction * 100) + " %";

    private static string KindName(Clip clip) => clip switch
    {
        ImageClip => Messages.Kind_Image,
        TextClip => Messages.Kind_Text,
        TemplateClip => Messages.Kind_Template,
        _ => Messages.Kind_Other
    };
}
