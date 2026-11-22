using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Timeline;

/// <summary>Individual clip properties, used to tell which ones an edit changed (for merging
/// consecutive edits into one Undo step and for the step's description).</summary>
[Flags]
public enum ClipPropertyFields : long
{
    None = 0,
    PositionX = 1 << 0,
    PositionY = 1 << 1,
    Scale = 1 << 2,
    Rotation = 1 << 3,
    Opacity = 1 << 4,
    CropLeft = 1 << 5,
    CropTop = 1 << 6,
    CropRight = 1 << 7,
    CropBottom = 1 << 8,
    Volume = 1 << 9,
    Mute = 1 << 10,
    Text = 1 << 11,
    FontFamily = 1 << 12,
    FontSize = 1 << 13,
    Color = 1 << 14,
    Alignment = 1 << 15,
    FadeIn = 1 << 16,
    FadeOut = 1 << 17,

    // D043: text style and motion.
    Bold = 1 << 18,
    OutlineEnabled = 1 << 19,
    OutlineColor = 1 << 20,
    OutlineThickness = 1 << 21,
    BackgroundEnabled = 1 << 22,
    BackgroundColor = 1 << 23,
    BackgroundOpacity = 1 << 24,
    MotionIn = 1 << 25,
    MotionOut = 1 << 26,

    // D045: template fields.
    TemplateDate = 1L << 27,
    TemplateRoute = 1L << 28,
    TemplateGrade = 1L << 29,
    TemplateStatus = 1L << 30,
    TemplateAccent = 1L << 31,
    TemplateTitle = 1L << 32,
    TemplateSubtitle = 1L << 33,
    TemplateStatusAt = 1L << 34,

    // Phase 35.5: the heart-rate overlay's look and corner.
    TemplateLook = 1L << 35,
    TemplateCorner = 1L << 36,

    // D055 §5: a «Боулдер»'s heart rate shown or not and its heart's colour.
    TemplateShowHeartRate = 1L << 37,
    TemplateHeart = 1L << 38,

    // D063: the transform keyframes — a keyframe added, removed, moved or its easing changed (Keyframes), or the values of
    // the same keyframes changed (KeyframeX / Y / Scale).
    Keyframes = 1L << 39,
    KeyframeX = 1L << 40,
    KeyframeY = 1L << 41,
    KeyframeScale = 1L << 42,

    // D063 §C: «Боулдер»'s route plate corner.
    TemplatePlateCorner = 1L << 43,

    // D064: the volume points (added, removed or moved: VolumePoints; only the gains of the same points: VolumePointValues),
    // a video clip's sound-only fades and the clean-up.
    VolumePoints = 1L << 44,
    VolumePointValues = 1L << 45,
    SoundFadeIn = 1L << 46,
    SoundFadeOut = 1L << 47,
    Cleanup = 1L << 48,

    // D073: «Боулдер»'s stated attempts, a template's language, «Итоги»'s own background.
    TemplateAttempts = 1L << 49,
    TemplateLanguage = 1L << 50,
    TemplateBackground = 1L << 51,

    Template = TemplateDate | TemplateRoute | TemplateGrade | TemplateStatus | TemplateAccent | TemplateTitle | TemplateSubtitle |
               TemplateStatusAt | TemplateLook | TemplateCorner | TemplateShowHeartRate | TemplateHeart | TemplatePlateCorner |
               TemplateAttempts | TemplateLanguage | TemplateBackground,

    KeyframeValues = KeyframeX | KeyframeY | KeyframeScale,

    Crop = CropLeft | CropTop | CropRight | CropBottom
}

/// <summary>
/// Absolute snapshot of every editable (non-timing) property of a clip; a group is null when it
/// doesn't apply to the clip's kind. Commands store before/after snapshots, so Undo and Redo
/// write back exactly the values that were captured — nothing is recomputed.
/// </summary>
public readonly record struct ClipPropertyValues(VisualProperties? Visual, AudioProperties? Audio, TextProperties? Text,
    FadeProperties? Fade = null, TemplateProperties? Template = null, KeyframeValues? Keyframes = null)
{
    public static ClipPropertyValues Capture(Clip clip) =>
        new(VisualProperties.Of(clip), AudioProperties.Of(clip), TextProperties.Of(clip), FadeProperties.Of(clip), TemplateProperties.Of(clip),
            KeyframeValues.Of(clip));

    public void ApplyTo(Clip clip)
    {
        if (Visual is { } v)
        {
            switch (clip)
            {
                case VideoClip c:
                    (c.PositionX, c.PositionY, c.Scale, c.RotationDegrees, c.Opacity, c.Crop) =
                        (v.PositionX, v.PositionY, v.Scale, v.RotationDegrees, v.Opacity, v.Crop);
                    break;
                case ImageClip c:
                    (c.PositionX, c.PositionY, c.Scale, c.RotationDegrees, c.Opacity, c.Crop) =
                        (v.PositionX, v.PositionY, v.Scale, v.RotationDegrees, v.Opacity, v.Crop);
                    break;
                case TextClip c: // text has no crop
                    (c.PositionX, c.PositionY, c.Scale, c.RotationDegrees, c.Opacity) =
                        (v.PositionX, v.PositionY, v.Scale, v.RotationDegrees, v.Opacity);
                    break;
                default:
                    throw new InvalidOperationException($"{clip.GetType().Name} has no picture properties.");
            }
        }

        if (Audio is { } a)
        {
            switch (clip)
            {
                case VideoClip c:
                    (c.Volume, c.IsMuted, c.Volumes, c.SoundFadeIn, c.SoundFadeOut, c.Cleanup) =
                        (a.Volume, a.IsMuted, a.Volumes, a.SoundFadeIn, a.SoundFadeOut, Core.Audio.AudioCleanup.Normalize(a.Cleanup));
                    break;
                case AudioClip c:
                    (c.Volume, c.IsMuted, c.Volumes, c.Cleanup) = (a.Volume, a.IsMuted, a.Volumes, Core.Audio.AudioCleanup.Normalize(a.Cleanup));
                    break;
                default: throw new InvalidOperationException($"{clip.GetType().Name} has no audio properties.");
            }
        }

        if (Text is { } t)
        {
            if (clip is not TextClip c)
                throw new InvalidOperationException($"{clip.GetType().Name} has no text properties.");
            (c.Text, c.FontFamily, c.FontSize, c.ColorHex, c.Alignment) = (t.Text, t.FontFamily, t.FontSize, t.ColorHex, t.Alignment);
            (c.Bold, c.Outline, c.Background, c.MotionIn, c.MotionOut) = (t.Bold, t.Outline, t.Background, t.MotionIn, t.MotionOut);
        }

        if (Fade is { } f)
            (clip.FadeIn, clip.FadeOut) = (f.FadeIn, f.FadeOut);

        if (Template is { } p)
        {
            if (clip is not TemplateClip c)
                throw new InvalidOperationException($"{clip.GetType().Name} has no template fields.");
            (c.Date, c.Route, c.Grade, c.Status, c.AccentHex, c.Title, c.Subtitle, c.StatusAt, c.Look, c.Corner) =
                (p.Date, p.Route, p.Grade, p.Status, p.AccentHex, p.Title, p.Subtitle, p.StatusAt, p.Look, p.Corner);
            (c.ShowHeartRate, c.HeartHex, c.PlateCorner) = (p.ShowHeartRate, p.HeartHex, p.PlateCorner);
            (c.Attempts, c.Language, c.OwnBackground) = (p.Attempts, p.Language, p.OwnBackground);
        }

        if (Keyframes is { } k)
        {
            if (clip is not VideoClip c)
                throw new InvalidOperationException($"{clip.GetType().Name} has no transform keyframes.");
            c.Keyframes = k.Keys;
        }
    }

    /// <summary>The properties whose values differ between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static ClipPropertyFields Diff(ClipPropertyValues a, ClipPropertyValues b)
    {
        var fields = ClipPropertyFields.None;

        if (a.Visual is { } va && b.Visual is { } vb)
        {
            if (!va.PositionX.Equals(vb.PositionX)) fields |= ClipPropertyFields.PositionX;
            if (!va.PositionY.Equals(vb.PositionY)) fields |= ClipPropertyFields.PositionY;
            if (!va.Scale.Equals(vb.Scale)) fields |= ClipPropertyFields.Scale;
            if (!va.RotationDegrees.Equals(vb.RotationDegrees)) fields |= ClipPropertyFields.Rotation;
            if (!va.Opacity.Equals(vb.Opacity)) fields |= ClipPropertyFields.Opacity;
            if (!va.Crop.Left.Equals(vb.Crop.Left)) fields |= ClipPropertyFields.CropLeft;
            if (!va.Crop.Top.Equals(vb.Crop.Top)) fields |= ClipPropertyFields.CropTop;
            if (!va.Crop.Right.Equals(vb.Crop.Right)) fields |= ClipPropertyFields.CropRight;
            if (!va.Crop.Bottom.Equals(vb.Crop.Bottom)) fields |= ClipPropertyFields.CropBottom;
        }

        if (a.Audio is { } aa && b.Audio is { } ab)
        {
            if (!aa.Volume.Equals(ab.Volume)) fields |= ClipPropertyFields.Volume;
            if (aa.IsMuted != ab.IsMuted) fields |= ClipPropertyFields.Mute;
            if (aa.Volumes != ab.Volumes) fields |= VolumePointDiff(aa.Volumes, ab.Volumes);
            if (aa.SoundFadeIn != ab.SoundFadeIn) fields |= ClipPropertyFields.SoundFadeIn;
            if (aa.SoundFadeOut != ab.SoundFadeOut) fields |= ClipPropertyFields.SoundFadeOut;
            if (Core.Audio.AudioCleanup.Normalize(aa.Cleanup) != Core.Audio.AudioCleanup.Normalize(ab.Cleanup)) fields |= ClipPropertyFields.Cleanup;
        }

        if (a.Text is { } ta && b.Text is { } tb)
        {
            if (!string.Equals(ta.Text, tb.Text, StringComparison.Ordinal)) fields |= ClipPropertyFields.Text;
            if (!string.Equals(ta.FontFamily, tb.FontFamily, StringComparison.Ordinal)) fields |= ClipPropertyFields.FontFamily;
            if (!ta.FontSize.Equals(tb.FontSize)) fields |= ClipPropertyFields.FontSize;
            if (!string.Equals(ta.ColorHex, tb.ColorHex, StringComparison.Ordinal)) fields |= ClipPropertyFields.Color;
            if (ta.Alignment != tb.Alignment) fields |= ClipPropertyFields.Alignment;
            if (ta.Bold != tb.Bold) fields |= ClipPropertyFields.Bold;
            if (ta.Outline.IsEnabled != tb.Outline.IsEnabled) fields |= ClipPropertyFields.OutlineEnabled;
            if (!string.Equals(ta.Outline.ColorHex, tb.Outline.ColorHex, StringComparison.Ordinal)) fields |= ClipPropertyFields.OutlineColor;
            if (!ta.Outline.Thickness.Equals(tb.Outline.Thickness)) fields |= ClipPropertyFields.OutlineThickness;
            if (ta.Background.IsEnabled != tb.Background.IsEnabled) fields |= ClipPropertyFields.BackgroundEnabled;
            if (!string.Equals(ta.Background.ColorHex, tb.Background.ColorHex, StringComparison.Ordinal)) fields |= ClipPropertyFields.BackgroundColor;
            if (!ta.Background.Opacity.Equals(tb.Background.Opacity)) fields |= ClipPropertyFields.BackgroundOpacity;
            if (ta.MotionIn != tb.MotionIn) fields |= ClipPropertyFields.MotionIn;
            if (ta.MotionOut != tb.MotionOut) fields |= ClipPropertyFields.MotionOut;
        }

        if (a.Fade is { } fa && b.Fade is { } fb)
        {
            if (fa.FadeIn != fb.FadeIn) fields |= ClipPropertyFields.FadeIn;
            if (fa.FadeOut != fb.FadeOut) fields |= ClipPropertyFields.FadeOut;
        }

        if (a.Template is { } pa && b.Template is { } pb)
        {
            if (!string.Equals(pa.Date, pb.Date, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateDate;
            if (!string.Equals(pa.Route, pb.Route, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateRoute;
            if (!string.Equals(pa.Grade, pb.Grade, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateGrade;
            if (pa.Status != pb.Status) fields |= ClipPropertyFields.TemplateStatus;
            if (!string.Equals(pa.AccentHex, pb.AccentHex, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateAccent;
            if (!string.Equals(pa.Title, pb.Title, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateTitle;
            if (!string.Equals(pa.Subtitle, pb.Subtitle, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateSubtitle;
            if (pa.StatusAt != pb.StatusAt) fields |= ClipPropertyFields.TemplateStatusAt;
            if (pa.Look != pb.Look) fields |= ClipPropertyFields.TemplateLook;
            if (pa.Corner != pb.Corner) fields |= ClipPropertyFields.TemplateCorner;
            if (pa.ShowHeartRate != pb.ShowHeartRate) fields |= ClipPropertyFields.TemplateShowHeartRate;
            if (!string.Equals(pa.HeartHex, pb.HeartHex, StringComparison.Ordinal)) fields |= ClipPropertyFields.TemplateHeart;
            if (pa.PlateCorner != pb.PlateCorner) fields |= ClipPropertyFields.TemplatePlateCorner;
            if (pa.Attempts != pb.Attempts) fields |= ClipPropertyFields.TemplateAttempts;
            if (pa.Language != pb.Language) fields |= ClipPropertyFields.TemplateLanguage;
            if (pa.OwnBackground != pb.OwnBackground) fields |= ClipPropertyFields.TemplateBackground;
        }

        if (a.Keyframes is { } ka && b.Keyframes is { } kb && !Equals(ka.Keys, kb.Keys))
            fields |= KeyframeDiff(ka.Keys, kb.Keys);

        return fields;
    }

    /// <summary>D064: <see cref="ClipPropertyFields.VolumePointValues"/> when only the gains of the same points changed (a drag
    /// of one point's value merges), else <see cref="ClipPropertyFields.VolumePoints"/>.</summary>
    private static ClipPropertyFields VolumePointDiff(Core.Audio.VolumeEnvelope? a, Core.Audio.VolumeEnvelope? b)
    {
        if (a is null || b is null || a.Count != b.Count) return ClipPropertyFields.VolumePoints;
        for (var i = 0; i < a.Count; i++)
            if (a.Points[i].Id != b.Points[i].Id || a.Points[i].Time != b.Points[i].Time) return ClipPropertyFields.VolumePoints;
        return ClipPropertyFields.VolumePointValues;
    }

    /// <summary>D063: which values of the same keyframes changed, or <see cref="ClipPropertyFields.Keyframes"/> when a
    /// keyframe was added, removed, moved or re-eased.</summary>
    private static ClipPropertyFields KeyframeDiff(TransformKeyframes? a, TransformKeyframes? b)
    {
        if (a is null || b is null || a.Count != b.Count) return ClipPropertyFields.Keyframes;
        var fields = ClipPropertyFields.None;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a.Keys[i], b.Keys[i]);
            if (x.Id != y.Id || x.Time != y.Time || x.Easing != y.Easing) return ClipPropertyFields.Keyframes;
            if (!x.X.Equals(y.X)) fields |= ClipPropertyFields.KeyframeX;
            if (!x.Y.Equals(y.Y)) fields |= ClipPropertyFields.KeyframeY;
            if (!x.Scale.Equals(y.Scale)) fields |= ClipPropertyFields.KeyframeScale;
        }
        return fields;
    }
}
