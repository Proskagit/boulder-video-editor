namespace AiVideoEditor.Core.Entities;

/// <summary>The motion a text's appearance or disappearance adds to its fade (D043 §1). <see cref="None"/> is the plain
/// «Проявление» (or «Нет» when the fade is 0): the D025 fade alone.</summary>
public enum TextMotionKind
{
    None,

    /// <summary>«Выезд»: the text slides in from (or out toward) <see cref="TextMotion.Side"/>.</summary>
    Slide,

    /// <summary>«Увеличение»: the text grows from 70 % to 100 % (or shrinks to 70 %) around its box's centre.</summary>
    Zoom
}

/// <summary>The side a sliding text comes from (In) or goes to (Out).</summary>
public enum TextMotionSide
{
    Bottom,
    Top,
    Left,
    Right
}

/// <summary>An appearance or disappearance motion (D043 §1). Its duration is the clip's fade (In: <c>FadeIn</c>, Out:
/// <c>FadeOut</c>); <see cref="Side"/> matters only for <see cref="TextMotionKind.Slide"/>. The default is no motion.</summary>
public readonly record struct TextMotion(TextMotionKind Kind, TextMotionSide Side = TextMotionSide.Bottom)
{
    public static readonly TextMotion None = default;
}

/// <summary>«Обводка» (D043 §2): drawn outside the glyphs under the fill, round joins. <see cref="Thickness"/> is the
/// visible width outside the glyphs as a fraction of the font size. Switched off it keeps its values.</summary>
public readonly record struct TextOutline(bool IsEnabled, string ColorHex, double Thickness)
{
    public const double DefaultThickness = 0.08;

    /// <summary>Off, black, 8 % — what a text without an outline holds.</summary>
    public static readonly TextOutline Default = new(false, "#000000", DefaultThickness);
}

/// <summary>«Подложка» (D043 §2): a slightly rounded rectangle around the text box with a padding of
/// <see cref="PaddingFactor"/> × the font size, at <see cref="Opacity"/>. Switched off it keeps its values.</summary>
public readonly record struct TextBackground(bool IsEnabled, string ColorHex, double Opacity)
{
    public const double DefaultOpacity = 0.6;

    /// <summary>The padding around the text box, × the font size.</summary>
    public const double PaddingFactor = 0.25;

    /// <summary>The corner radius, × the font size.</summary>
    public const double CornerFactor = 0.12;

    /// <summary>Off, black, 60 % — what a text without a background holds.</summary>
    public static readonly TextBackground Default = new(false, "#000000", DefaultOpacity);
}
