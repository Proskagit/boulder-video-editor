using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>What the interface calls the library's transitions and their directions (D044 §1, §6), the glyph of a zone on
/// the timeline, and a duration as the seconds of its whole frames with the frame count (D044 §5).</summary>
public static class TransitionNames
{
    public static string Name(string typeId) => typeId switch
    {
        TransitionRules.DipToBlack => Strings.TransitionType_DipToBlack,
        TransitionRules.Flash => Strings.TransitionType_Flash,
        TransitionRules.Zoom => Strings.TransitionType_Zoom,
        TransitionRules.Push => Strings.TransitionType_Push,
        TransitionRules.Crossfade => Strings.Transition_Crossfade,
        _ => Strings.TransitionType_CrossDissolve
    };

    public static string Name(TransitionDirection direction) => direction switch
    {
        TransitionDirection.FromLeft => Strings.TransitionDirection_FromLeft,
        TransitionDirection.FromTop => Strings.TransitionDirection_FromTop,
        TransitionDirection.FromBottom => Strings.TransitionDirection_FromBottom,
        _ => Strings.TransitionDirection_FromRight
    };

    /// <summary>The mark drawn in a zone on the timeline: one per type.</summary>
    public static string Glyph(string typeId) => typeId switch
    {
        TransitionRules.DipToBlack => "◐",
        TransitionRules.Flash => "✦",
        TransitionRules.Zoom => "⊕",
        TransitionRules.Push => "⇢",
        _ => "⨯"
    };

    /// <summary>The seconds of <paramref name="frames"/> at <paramref name="rate"/>, to the hundredth (as D043's fields).</summary>
    public static decimal SecondsOf(long frames, FrameRate rate) => Math.Round((decimal)frames * rate.Denominator / rate.Numerator, 2);

    /// <summary>"0,29 с · 7 кадров": the seconds the stored frames really last and their count — never a precision the
    /// frames lack (D044 §5).</summary>
    public static string Duration(long frames, FrameRate rate) =>
        string.Format(Strings.Transition_DurationValue, SecondsOf(frames, rate).ToString("0.##", CultureInfo.CurrentCulture),
            Plural.Format(frames, Strings.Plural_Frames));
}
