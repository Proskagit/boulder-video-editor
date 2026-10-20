using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A colour of the swatch rows (D043 §3): its <c>#RRGGBB</c> and its name (the tooltip).</summary>
public sealed record ColorSwatch(string Hex, string Name);

/// <summary>What a row of the «Анимация» block shows (D043 §1): the fade alone, or the fade with a motion.</summary>
public enum TextAppearanceKind
{
    None,
    Fade,
    Slide,
    Zoom
}

/// <summary>
/// Phase 27 (D043 §1–§4): the text clip's «Анимация» block (in place of the fades for text), the outline, the background,
/// bold, the colour swatches and the 3 × 3 placement grid. A discrete choice — a kind, a direction, a swatch, a switch, bold, a
/// grid cell — is one undo step of its own (a <see cref="ClipPropertyGesture"/>, so two clicks never merge); a typed or dragged
/// value — a duration, a thickness, an opacity — merges with the next change of the same value (D017). A duration is the
/// clip's fade (Q2): set in seconds from <see cref="TextAnimationLimits.MinSeconds"/> to
/// <see cref="TextAnimationLimits.MaxSeconds"/>, stored as whole frames (D025); a stored value outside that range is shown as
/// it is and kept until it is changed.
/// </summary>
public sealed partial class InspectorViewModel
{
    public IReadOnlyList<string> AppearanceKinds { get; } =
        new[] { Strings.TextMotion_None, Strings.TextMotion_Fade, Strings.TextMotion_Slide, Strings.TextMotion_Zoom };

    public IReadOnlyList<string> MotionSides { get; } =
        new[] { Strings.TextMotion_FromBottom, Strings.TextMotion_FromTop, Strings.TextMotion_FromLeft, Strings.TextMotion_FromRight };

    public IReadOnlyList<ColorSwatch> Swatches { get; } = new ColorSwatch[]
    {
        new("#FFFFFF", Strings.Swatch_White), new("#000000", Strings.Swatch_Black), new("#FFD600", Strings.Swatch_Yellow),
        new("#FF8C00", Strings.Swatch_Orange), new("#E53935", Strings.Swatch_Red), new("#43A047", Strings.Swatch_Green),
        new("#4FC3F7", Strings.Swatch_LightBlue), new("#F06292", Strings.Swatch_Pink)
    };

    /// <summary>The fades section for every clip kind but text, whose «Анимация» block holds its fades (Q2).</summary>
    [ObservableProperty] private bool _showsFades;

    // --- «Анимация» -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearTimed))]
    [NotifyPropertyChangedFor(nameof(IsAppearSlide))]
    private int _appearKindIndex;

    [ObservableProperty] private int _appearSideIndex;
    [ObservableProperty] private decimal? _appearSeconds;
    [ObservableProperty] private decimal _minAppearSeconds = (decimal)TextAnimationLimits.MinSeconds;
    [ObservableProperty] private decimal _maxAppearSeconds = (decimal)TextAnimationLimits.MaxSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisappearTimed))]
    [NotifyPropertyChangedFor(nameof(IsDisappearSlide))]
    private int _disappearKindIndex;

    [ObservableProperty] private int _disappearSideIndex;
    [ObservableProperty] private decimal? _disappearSeconds;
    [ObservableProperty] private decimal _minDisappearSeconds = (decimal)TextAnimationLimits.MinSeconds;
    [ObservableProperty] private decimal _maxDisappearSeconds = (decimal)TextAnimationLimits.MaxSeconds;

    public bool IsAppearTimed => AppearKindIndex != (int)TextAppearanceKind.None;
    public bool IsAppearSlide => AppearKindIndex == (int)TextAppearanceKind.Slide;
    public bool IsDisappearTimed => DisappearKindIndex != (int)TextAppearanceKind.None;
    public bool IsDisappearSlide => DisappearKindIndex == (int)TextAppearanceKind.Slide;

    partial void OnAppearKindIndexChanged(int value) => EditMotionKind(appear: true);
    partial void OnDisappearKindIndexChanged(int value) => EditMotionKind(appear: false);
    partial void OnAppearSideIndexChanged(int value) => EditMotionKind(appear: true);
    partial void OnDisappearSideIndexChanged(int value) => EditMotionKind(appear: false);
    partial void OnAppearSecondsChanged(decimal? value) => EditMotionSeconds(appear: true, value);
    partial void OnDisappearSecondsChanged(decimal? value) => EditMotionSeconds(appear: false, value);

    /// <summary>A kind or a direction chosen: the motion and, from «Нет» or to it, the fade — one step.</summary>
    private void EditMotionKind(bool appear)
    {
        if (_syncing || _clip is null || TextProperties.Of(_clip) is not { } text || RejectWhileLocked()) return;
        var kindIndex = appear ? AppearKindIndex : DisappearKindIndex;
        var sideIndex = appear ? AppearSideIndex : DisappearSideIndex;
        if (kindIndex < 0 || sideIndex < 0) return;                     // the list is being refilled
        var kind = (TextAppearanceKind)kindIndex;
        var side = (TextMotionSide)sideIndex;

        var current = appear ? _clip.FadeIn : _clip.FadeOut;
        var clipFrames = TransitionRules.ClipFrames(_clip, _rate);
        long frames = kind == TextAppearanceKind.None ? 0 : TransitionRules.Frames(current, _rate);
        if (kind != TextAppearanceKind.None && frames == 0)
            frames = Math.Clamp(MediaTime.FromSeconds(appear ? TextAnimationLimits.DefaultInSeconds : TextAnimationLimits.DefaultOutSeconds)
                .ToNearestFrame(_rate), 1, Math.Max(1, clipFrames));
        var motion = kind switch
        {
            TextAppearanceKind.Slide => new TextMotion(TextMotionKind.Slide, side),
            TextAppearanceKind.Zoom => new TextMotion(TextMotionKind.Zoom, side),
            _ => TextMotion.None
        };
        var fade = FadeProperties.Of(_clip);
        var length = MediaTime.FromFrame(frames, _rate);
        var change = new ClipPropertyChange
        {
            Text = appear ? text with { MotionIn = motion } : text with { MotionOut = motion },
            Fade = appear ? fade with { FadeIn = length } : fade with { FadeOut = length },
            Gesture = new ClipPropertyGesture(appear ? "Change Appearance" : "Change Disappearance")
        };
        Apply(change);
    }

    /// <summary>A duration typed or dragged (seconds): the fade in whole frames; merges with the next one (D017).</summary>
    private void EditMotionSeconds(bool appear, decimal? value)
    {
        if (_syncing || value is not { } seconds || _clip is null || RejectWhileLocked()) return;
        if (seconds < (decimal)TextAnimationLimits.MinSeconds || seconds > (decimal)TextAnimationLimits.MaxSeconds)
        {
            _status.Report(string.Format(Strings.Inspector_MotionDurationRange, TextAnimationLimits.MinSeconds, TextAnimationLimits.MaxSeconds));
            ShowModelAfterRejection();
            return;
        }
        var frames = (long)Math.Round(seconds * _rate.Numerator / _rate.Denominator, MidpointRounding.AwayFromZero);
        frames = Math.Clamp(frames, 1, Math.Max(1, TransitionRules.ClipFrames(_clip, _rate)));
        var fade = FadeProperties.Of(_clip);
        var length = MediaTime.FromFrame(frames, _rate);
        Apply(new ClipPropertyChange { Fade = appear ? fade with { FadeIn = length } : fade with { FadeOut = length } });
    }

    /// <summary>Seconds of <paramref name="frames"/> at the project rate, to the hundredth.</summary>
    private decimal SecondsOf(long frames) => Math.Round((decimal)frames * _rate.Denominator / _rate.Numerator, 2);

    private static TextAppearanceKind KindOf(MediaTime fade, TextMotion motion) => fade <= MediaTime.Zero
        ? TextAppearanceKind.None
        : motion.Kind switch
        {
            TextMotionKind.Slide => TextAppearanceKind.Slide,
            TextMotionKind.Zoom => TextAppearanceKind.Zoom,
            _ => TextAppearanceKind.Fade
        };

    // --- bold, outline, background ---------------------------------------------------------------------------------

    [ObservableProperty] private bool _isBold;
    [ObservableProperty] private bool _isOutlineOn;
    [ObservableProperty] private decimal? _outlineThicknessPercent;
    [ObservableProperty] private string _outlineColorSwatch = "#000000";
    [ObservableProperty] private bool _isBackgroundOn;
    [ObservableProperty] private decimal? _backgroundOpacityPercent;
    [ObservableProperty] private string _backgroundColorSwatch = "#000000";

    public decimal MinOutlineThicknessPercent => (decimal)ClipPropertyLimits.MinOutlineThickness * 100;
    public decimal MaxOutlineThicknessPercent => (decimal)ClipPropertyLimits.MaxOutlineThickness * 100;
    public decimal MinBackgroundOpacityPercent => (decimal)ClipPropertyLimits.MinBackgroundOpacity * 100;
    public decimal MaxBackgroundOpacityPercent => (decimal)ClipPropertyLimits.MaxBackgroundOpacity * 100;

    partial void OnIsBoldChanged(bool value) => EditStyle(t => t with { Bold = value }, "Change Bold");
    partial void OnIsOutlineOnChanged(bool value) => EditStyle(t => t with { Outline = t.Outline with { IsEnabled = value } }, "Change Outline");
    partial void OnIsBackgroundOnChanged(bool value) => EditStyle(t => t with { Background = t.Background with { IsEnabled = value } }, "Change Text Background");

    partial void OnOutlineThicknessPercentChanged(decimal? value)
    {
        if (_syncing || value is not { } percent) return;
        var whole = WholePercent(percent);
        EditStyle(t => t with { Outline = t.Outline with { Thickness = (double)(whole / 100m) } }, gesture: null);
        if (whole != percent) ShowModelAfterRejection();
    }

    partial void OnBackgroundOpacityPercentChanged(decimal? value)
    {
        if (_syncing || value is not { } percent) return;
        var whole = WholePercent(percent);
        EditStyle(t => t with { Background = t.Background with { Opacity = (double)(whole / 100m) } }, gesture: null);
        if (whole != percent) ShowModelAfterRejection();
    }

    [RelayCommand] private void SetTextColor(string hex) => EditStyle(t => t with { ColorHex = hex }, "Change Text Color");

    [RelayCommand] private void SetOutlineColor(string hex) => EditStyle(t => t with { Outline = t.Outline with { ColorHex = hex } }, "Change Outline");

    [RelayCommand] private void SetBackgroundColor(string hex) =>
        EditStyle(t => t with { Background = t.Background with { ColorHex = hex } }, "Change Text Background");

    /// <summary>A text style change; <paramref name="gesture"/> names a discrete choice's own undo step (null: merges).</summary>
    private void EditStyle(Func<TextProperties, TextProperties> change, string? gesture)
    {
        if (_syncing || _clip is null || TextProperties.Of(_clip) is not { } current || RejectWhileLocked()) return;
        Apply(new ClipPropertyChange { Text = change(current), Gesture = gesture is null ? null : new ClipPropertyGesture(gesture) });
    }

    // --- the placement grid ------------------------------------------------------------------------------------------

    /// <summary>A grid cell "Row,Column" (e.g. "Bottom,Centre"): the text box placed there (D043 §3), one step.</summary>
    [RelayCommand]
    private void Place(string cell)
    {
        if (_clip is null || TextProperties.Of(_clip) is not { } text || VisualProperties.Of(_clip) is not { } visual
            || !_canvas.IsValid || _textBoxes is null || RejectWhileLocked()) return;
        var parts = cell.Split(',');
        if (parts.Length != 2 || !Enum.TryParse<TextGridRow>(parts[0], out var row) || !Enum.TryParse<TextGridColumn>(parts[1], out var column))
            throw new ArgumentException($"Unknown grid cell '{cell}'.", nameof(cell));
        var (width, height) = _textBoxes.Measure(text);
        var (x, y) = TextPlacement.Position(_canvas, text, visual, width, height, row, column);
        Apply(new ClipPropertyChange { Visual = visual with { PositionX = x, PositionY = y }, Gesture = new ClipPropertyGesture("Place Text") });
    }

    private FrameSize _canvas;
    private readonly ITextBoxMeasurer? _textBoxes;

    private void Apply(ClipPropertyChange change)
    {
        if (_clip is null) return;
        var result = _edit.SetClipProperties(_clip.Id, change);
        if (!result.Success)
        {
            _status.Report(result.Message ?? Strings.Inspector_ClipNotChanged);
            ShowModelAfterRejection(); // the fields show what the model really has
        }
    }

    /// <summary>Fills the D043 fields from <paramref name="clip"/> (under the sync guard of the caller).</summary>
    private void SyncTextStyle(TextClip clip)
    {
        var text = TextProperties.Of(clip)!.Value;
        AppearKindIndex = (int)KindOf(clip.FadeIn, text.MotionIn);
        AppearSideIndex = (int)text.MotionIn.Side;
        DisappearKindIndex = (int)KindOf(clip.FadeOut, text.MotionOut);
        DisappearSideIndex = (int)text.MotionOut.Side;
        var appear = SecondsOf(TransitionRules.Frames(clip.FadeIn, _rate));
        var disappear = SecondsOf(TransitionRules.Frames(clip.FadeOut, _rate));
        MinAppearSeconds = Math.Min((decimal)TextAnimationLimits.MinSeconds, appear);
        MaxAppearSeconds = Math.Max((decimal)TextAnimationLimits.MaxSeconds, appear);
        MinDisappearSeconds = Math.Min((decimal)TextAnimationLimits.MinSeconds, disappear);
        MaxDisappearSeconds = Math.Max((decimal)TextAnimationLimits.MaxSeconds, disappear);
        AppearSeconds = appear;
        DisappearSeconds = disappear;

        IsBold = text.Bold;
        IsOutlineOn = text.Outline.IsEnabled;
        OutlineThicknessPercent = (decimal)text.Outline.Thickness * 100m;
        OutlineColorSwatch = text.Outline.ColorHex;
        IsBackgroundOn = text.Background.IsEnabled;
        BackgroundOpacityPercent = (decimal)text.Background.Opacity * 100m;
        BackgroundColorSwatch = text.Background.ColorHex;
    }
}
