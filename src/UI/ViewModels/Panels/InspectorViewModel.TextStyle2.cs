using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>D075 amendment 2: what a line of the styles' list is.</summary>
public enum TextStyleChoiceKind { Default, Saved, SaveNew }

/// <summary>D075 amendment 2: a line of the styles' list.</summary>
public sealed record TextStyleChoice(string Name, SavedTextStyle? Style, TextStyleChoiceKind Kind)
{
    public override string ToString() => Name;
}

/// <summary>
/// D075 amendment 1 — the text's italic, «Тень», «Интервал», «Ширина» (its block's width, also dragged in the Preview) and the
/// saved styles (the user's own, global, in <see cref="UserSettings.TextStyles"/>). A switch, a swatch or a style applied is an
/// undo step of its own; a typed or dragged value merges with the next change of the same value (D017).
/// </summary>
public sealed partial class InspectorViewModel
{
    [ObservableProperty] private bool _isItalic;
    [ObservableProperty] private decimal? _lineSpacing;
    [ObservableProperty] private string _boxWidthText = "";
    [ObservableProperty] private bool _isShadowOn;
    [ObservableProperty] private string _shadowColorSwatch = "#000000";
    [ObservableProperty] private decimal? _shadowOpacityPercent;
    [ObservableProperty] private decimal? _shadowBlurPercent;
    [ObservableProperty] private decimal? _shadowOffsetXPercent;
    [ObservableProperty] private decimal? _shadowOffsetYPercent;

    public decimal MinLineSpacing => (decimal)ClipPropertyLimits.MinLineSpacing;
    public decimal MaxLineSpacing => (decimal)ClipPropertyLimits.MaxLineSpacing;
    public decimal MaxShadowBlurPercent => (decimal)ClipPropertyLimits.MaxShadowBlur * 100;
    public decimal MaxShadowOffsetPercent => (decimal)ClipPropertyLimits.MaxShadowOffset * 100;
    public decimal MinShadowOffsetPercent => -(decimal)ClipPropertyLimits.MaxShadowOffset * 100;

    partial void OnIsItalicChanged(bool value) => EditStyle(t => t with { Italic = value }, "Change Italic");
    partial void OnIsShadowOnChanged(bool value) => EditStyle(t => t with { Shadow = t.Shadow with { IsEnabled = value } }, "Change Shadow");

    partial void OnLineSpacingChanged(decimal? value)
    {
        if (_syncing || value is not { } spacing) return;
        EditStyle(t => t with { LineSpacing = (double)Math.Round(spacing, 2) }, gesture: null);
    }

    /// <summary>«Ширина» typed: canvas pixels; empty — no wrapping (as wide as the longest line).</summary>
    partial void OnBoxWidthTextChanged(string value)
    {
        if (_syncing) return;
        var text = value.Trim();
        if (text.Length == 0) EditStyle(t => t with { BoxWidth = null }, gesture: null);
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
                 double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out width))
            EditStyle(t => t with { BoxWidth = Math.Round(width) }, gesture: null);
    }

    partial void OnShadowOpacityPercentChanged(decimal? value) => EditShadow(value, (s, v) => s with { Opacity = v });
    partial void OnShadowBlurPercentChanged(decimal? value) => EditShadow(value, (s, v) => s with { Blur = v });
    partial void OnShadowOffsetXPercentChanged(decimal? value) => EditShadow(value, (s, v) => s with { OffsetX = v });
    partial void OnShadowOffsetYPercentChanged(decimal? value) => EditShadow(value, (s, v) => s with { OffsetY = v });

    private void EditShadow(decimal? percent, Func<TextShadow, double, TextShadow> set)
    {
        if (_syncing || percent is not { } p) return;
        var whole = WholePercent(p);
        EditStyle(t => t with { Shadow = set(t.Shadow, (double)(whole / 100m)) }, gesture: null);
        if (whole != p) ShowModelAfterRejection();
    }

    [RelayCommand] private void SetShadowColor(string hex) => EditStyle(t => t with { Shadow = t.Shadow with { ColorHex = hex } }, "Change Shadow");

    /// <summary>Fills the D075 fields from <paramref name="text"/> (under the sync guard of the caller).</summary>
    private void SyncTextStyle2(TextProperties text)
    {
        IsItalic = text.Italic;
        LineSpacing = (decimal)text.LineSpacing;
        BoxWidthText = text.BoxWidth is { } width ? width.ToString("0", CultureInfo.InvariantCulture) : "";
        IsShadowOn = text.Shadow.IsEnabled;
        ShadowColorSwatch = text.Shadow.ColorHex;
        ShadowOpacityPercent = Math.Round((decimal)text.Shadow.Opacity * 100m);
        ShadowBlurPercent = Math.Round((decimal)text.Shadow.Blur * 100m);
        ShadowOffsetXPercent = Math.Round((decimal)text.Shadow.OffsetX * 100m);
        ShadowOffsetYPercent = Math.Round((decimal)text.Shadow.OffsetY * 100m);
        if (StyleChoices.Count == 0) RefreshSavedStyles();
        SelectedStyleChoice = ChoiceOfShown();
    }

    // --- the saved styles ----------------------------------------------------------------------------------------------

    private IUserSettingsService? _settings;

    /// <summary>The global settings that keep the saved styles (set by the shell; null — no saved styles).</summary>
    public IUserSettingsService? Settings
    {
        get => _settings;
        set
        {
            if (_settings is not null) _settings.Changed -= OnSettingsChanged;
            _settings = value;
            if (_settings is not null) _settings.Changed += OnSettingsChanged;
            RefreshSavedStyles();
            OnPropertyChanged(nameof(HasSavedStyleStore));
        }
    }

    public bool HasSavedStyleStore => _settings is not null;

    public ObservableCollection<SavedTextStyle> SavedTextStyles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTextStyleCommand))]
    private string _newTextStyleName = "";

    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshSavedStyles();

    private void RefreshSavedStyles()
    {
        SavedTextStyles.Clear();
        foreach (var style in _settings?.Current.TextStyles ?? Array.Empty<SavedTextStyle>()) SavedTextStyles.Add(style);
        var was = _syncing;
        _syncing = true;
        try
        {
            StyleChoices.Clear();
            StyleChoices.Add(new TextStyleChoice(Strings.TextStyle_Default, null, TextStyleChoiceKind.Default));
            foreach (var style in SavedTextStyles) StyleChoices.Add(new TextStyleChoice(style.Name, style, TextStyleChoiceKind.Saved));
            if (_settings is not null) StyleChoices.Add(new TextStyleChoice(Strings.TextStyle_SaveNew, null, TextStyleChoiceKind.SaveNew));
            SelectedStyleChoice = ChoiceOfShown();
        }
        finally
        {
            _syncing = was;
        }
    }

    // --- D075 amendment 2: the styles as one list ------------------------------------------------------------------------

    /// <summary>«По умолчанию», the saved styles, «＋ Сохранить новый стиль…».</summary>
    public ObservableCollection<TextStyleChoice> StyleChoices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedTextStyleCommand))]
    private TextStyleChoice? _selectedStyleChoice;

    /// <summary>A choice of the list: the default look or a saved style applied (one step each), or the shown look saved as a new
    /// style (then chosen).</summary>
    partial void OnSelectedStyleChoiceChanged(TextStyleChoice? value)
    {
        if (_syncing || value is null || _clip is null) return;
        switch (value.Kind)
        {
            case TextStyleChoiceKind.Default:
                EditStyle(DefaultLook, "Apply Text Style");
                break;
            case TextStyleChoiceKind.Saved:
                ApplyTextStyle(value.Style);
                break;
            case TextStyleChoiceKind.SaveNew:
                SaveTextStyle();   // the list is refilled by the settings' change, the new style chosen
                break;
        }
        var was = _syncing;
        _syncing = true;
        try { SelectedStyleChoice = ChoiceOfShown(); }
        finally { _syncing = was; }
    }

    /// <summary>The look a new text starts with (D043 §4): Segoe UI at the canvas's size, white, bold, centred, the black
    /// background; no italic, outline or shadow; the words, the place, the width and the timing kept.</summary>
    private TextProperties DefaultLook(TextProperties t) => t with
    {
        FontFamily = new TextClip().FontFamily, FontSize = ProjectSettingsRules.NewTextFontSize(_canvas.IsValid ? _canvas.Height : 1080), ColorHex = "#FFFFFF",
        Alignment = TextAlignment.Center, Bold = true, Italic = false, LineSpacing = 1.0, Outline = TextOutline.Default,
        Background = TextBackground.Default with { IsEnabled = true }, Shadow = TextShadow.Default
    };

    /// <summary>The choice whose look the shown text has (a saved style, else the default look), or none.</summary>
    private TextStyleChoice? ChoiceOfShown()
    {
        if (_clip is null || TextProperties.Of(_clip) is not { } text) return null;
        foreach (var choice in StyleChoices.Where(c => c.Kind == TextStyleChoiceKind.Saved))
            if (choice.Style!.ApplyTo(text) == text) return choice;
        return DefaultLook(text) == text ? StyleChoices.FirstOrDefault(c => c.Kind == TextStyleChoiceKind.Default) : null;
    }

    private bool CanDeleteSelectedTextStyle() => SelectedStyleChoice is { Kind: TextStyleChoiceKind.Saved };

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedTextStyle))]
    private void DeleteSelectedTextStyle() => DeleteTextStyle(SelectedStyleChoice?.Style);

    private bool CanSaveTextStyle() => _settings is not null;

    /// <summary>«Сохранить стиль»: the shown text's look under the typed name (empty — «Стиль N»); a name taken is replaced.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTextStyle))]
    private void SaveTextStyle()
    {
        if (_settings is null || _clip is null || TextProperties.Of(_clip) is not { } text) return;
        var name = NewTextStyleName.Trim();
        if (name.Length == 0)
        {
            // D075 amendment 2: a name that says what the style is — «Segoe UI 54 · жирный · курсив» — unique (a number added).
            var taken = _settings.Current.TextStyles.Select(x => x.Name).ToHashSet();
            var parts = new List<string> { string.Format(CultureInfo.InvariantCulture, "{0} {1:0}", text.FontFamily, text.FontSize) };
            if (text.Bold) parts.Add(Strings.TextStyle_Bold);
            if (text.Italic) parts.Add(Strings.TextStyle_Italic);
            if (text.Shadow.IsEnabled) parts.Add(Strings.TextStyle_Shadow);
            var basis = string.Join(" · ", parts);
            name = basis;
            for (var n = 2; taken.Contains(name); n++) name = $"{basis} ({n})";
        }
        var style = SavedTextStyle.Of(name, text);
        _settings.Update(s => s with { TextStyles = s.TextStyles.Where(x => x.Name != name).Append(style).ToList() });
        NewTextStyleName = "";
        _status.Report(string.Format(Strings.TextStyle_Saved, name));
    }

    /// <summary>A saved style applied to the shown text — its look only (the words, the place and the timing stay); one step.</summary>
    [RelayCommand]
    private void ApplyTextStyle(SavedTextStyle? style)
    {
        if (style is null) return;
        EditStyle(t => style.ApplyTo(t), "Apply Text Style");
    }

    [RelayCommand]
    private void DeleteTextStyle(SavedTextStyle? style)
    {
        if (style is null || _settings is null) return;
        _settings.Update(s => s with { TextStyles = s.TextStyles.Where(x => x.Name != style.Name).ToList() });
    }
}
