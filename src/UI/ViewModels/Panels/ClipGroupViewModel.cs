using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A colour of the group's hold outline: «Авто» (null) or a palette colour.</summary>
public sealed record GroupColorOption(string Label, string? Hex);

/// <summary>
/// D058 §3: the Inspector of several selected clips — five groups of properties only (the background, Fill / Fit, the
/// volume and mute, the opacity, the hold outline's style), each applied to every selected clip it fits as <b>one</b> Undo
/// step (<see cref="ITimelineEditService.EditAsOneStep"/>). A linked pair (D032) is one item: its picture takes the
/// picture's properties, its sound the sound's. A group is offered only when every item has a clip it fits (the outline:
/// every item has a video clip and some of them have marked holds); values that differ show as «—» (an empty field, no
/// choice, an undetermined check box) until the user sets one. The single clip's Inspector is not changed by it.
/// </summary>
public sealed partial class ClipGroupViewModel : ViewModelBase
{
    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly Func<bool> _isLocked;
    private readonly Func<Guid, MediaAsset?> _asset;
    private bool _syncing;

    private IReadOnlyList<IReadOnlyList<Clip>> _items = Array.Empty<IReadOnlyList<Clip>>();
    private IReadOnlyList<VideoClip> _videos = Array.Empty<VideoClip>();
    private IReadOnlyList<Clip> _framed = Array.Empty<Clip>();
    private IReadOnlyList<Clip> _visuals = Array.Empty<Clip>();
    private IReadOnlyList<Clip> _sounds = Array.Empty<Clip>();
    private IReadOnlyList<(VideoClip Clip, HoldMarkup Route)> _routes = Array.Empty<(VideoClip, HoldMarkup)>();

    public ClipGroupViewModel(ITimelineEditService edit, StatusService status, Func<bool> isLocked, Func<Guid, MediaAsset?> asset)
    {
        _edit = edit;
        _status = status;
        _isLocked = isLocked;
        _asset = asset;
        ColorOptions = new[] { new GroupColorOption(Strings.Holds_ModeAuto, null) }
            .Concat(RouteColor.Palette.Select(p => new GroupColorOption(p.Hex, p.Hex))).ToList();
    }

    /// <summary>Two items or more are selected (a linked pair is one): the group's Inspector is shown.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string _heading = "";

    [ObservableProperty] private bool _hasBackground;
    [ObservableProperty] private bool _hasFraming;
    [ObservableProperty] private bool _hasOpacity;
    [ObservableProperty] private bool _hasAudio;
    [ObservableProperty] private bool _hasHoldStyle;

    /// <summary>None of the five groups fits every selected item.</summary>
    [ObservableProperty] private bool _hasNothing;

    /// <summary>0 black, 1 blurred, −1 mixed.</summary>
    [ObservableProperty] private int _backgroundIndex = -1;

    public IReadOnlyList<string> Backgrounds { get; } = new[] { Strings.Background_Black, Strings.Background_Blur };

    /// <summary>Null: the clips differ (or the field is being typed).</summary>
    [ObservableProperty] private decimal? _opacityPercent;

    [ObservableProperty] private decimal? _volumePercent;

    /// <summary>Null: some are muted, some not.</summary>
    [ObservableProperty] private bool? _isMuted;

    [ObservableProperty] private decimal? _strokeWidth;

    /// <summary>Index in <see cref="ColorOptions"/>: 0 «Авто», then the palette; −1 mixed or a colour outside it.</summary>
    [ObservableProperty] private int _colorIndex = -1;

    /// <summary>0 auto, 1 on, 2 off; −1 mixed.</summary>
    [ObservableProperty] private int _keylineIndex = -1;

    public IReadOnlyList<GroupColorOption> ColorOptions { get; }

    public IReadOnlyList<string> KeylineModes { get; } = new[] { Strings.Holds_ModeAuto, Strings.Holds_KeylineOnItem, Strings.Holds_KeylineOffItem };

    public decimal MinOpacityPercent => (decimal)ClipPropertyLimits.MinOpacity * 100;
    public decimal MaxOpacityPercent => (decimal)ClipPropertyLimits.MaxOpacity * 100;
    public decimal MinStrokeWidth => (decimal)HoldMarkupRules.MinStrokeWidth1080;
    public decimal MaxStrokeWidth => (decimal)HoldMarkupRules.MaxStrokeWidth1080;

    /// <summary>The selected clips (link partners included), from the timeline; fewer than two items deactivates.</summary>
    public void Show(IReadOnlyList<Clip> selected)
    {
        _items = selected.GroupBy(c => (c as MediaBackedClip)?.LinkId ?? c.Id).Select(g => (IReadOnlyList<Clip>)g.ToList()).ToList();
        IsActive = _items.Count >= 2;
        if (!IsActive)
        {
            _videos = Array.Empty<VideoClip>();
            _framed = _visuals = _sounds = Array.Empty<Clip>();
            _routes = Array.Empty<(VideoClip, HoldMarkup)>();
            return;
        }

        Heading = string.Format(Strings.Inspector_GroupHeading, _items.Count);
        _videos = Every(i => i.OfType<VideoClip>().FirstOrDefault())?.Cast<VideoClip>().ToList() ?? (IReadOnlyList<VideoClip>)Array.Empty<VideoClip>();
        _framed = Every(i => i.FirstOrDefault(c => c is VideoClip or ImageClip)) ?? Array.Empty<Clip>();
        _visuals = Every(i => i.FirstOrDefault(c => VisualProperties.Of(c) is not null)) ?? Array.Empty<Clip>();
        _sounds = Every(i => i.FirstOrDefault(HasSound)) ?? Array.Empty<Clip>();
        _routes = _videos.SelectMany(v => v.Routes.Where(r => r.Holds.Length > 0).Select(r => (v, r))).ToList();

        HasBackground = _videos.Count > 0;
        HasFraming = _framed.Count > 0;
        HasOpacity = _visuals.Count > 0;
        HasAudio = _sounds.Count > 0;
        HasHoldStyle = _videos.Count > 0 && _routes.Count > 0;
        HasNothing = !(HasBackground || HasFraming || HasOpacity || HasAudio || HasHoldStyle);
        Load();
    }

    /// <summary>For every item its clip <paramref name="pick"/> chooses; null when some item has none.</summary>
    private IReadOnlyList<Clip>? Every(Func<IReadOnlyList<Clip>, Clip?> pick)
    {
        var picked = _items.Select(pick).ToList();
        return picked.All(c => c is not null) ? picked.Select(c => c!).ToList() : null;
    }

    /// <summary>A clip that carries sound: an audio clip, or a video clip that has its own (not detached, D032 R12, and
    /// a file with an audio stream as far as is known) — as the single clip's Inspector decides it.</summary>
    private bool HasSound(Clip clip) => clip is AudioClip
        || (clip is VideoClip { IsAudioDetached: false } video && _asset(video.MediaAssetId)?.Metadata is not { AudioCodec: null });

    private static T? Same<T>(IEnumerable<T> values) where T : struct
    {
        var list = values.Distinct().ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private void Load()
    {
        _syncing = true;
        try
        {
            BackgroundIndex = Same(_videos.Select(v => v.Background)) is { } background ? (background == ClipBackground.Blur ? 1 : 0) : -1;
            OpacityPercent = Same(_visuals.Select(c => Math.Round((decimal)VisualProperties.Of(c)!.Value.Opacity * 100, 1)));
            VolumePercent = Same(_sounds.Select(c => Math.Round((decimal)AudioProperties.Of(c)!.Value.Volume * 100, 1)));
            IsMuted = Same(_sounds.Select(c => AudioProperties.Of(c)!.Value.IsMuted));
            StrokeWidth = Same(_routes.Select(r => (decimal)r.Route.StrokeWidth1080));
            var colours = _routes.Select(r => r.Route.ColorMode == HoldColorMode.Auto ? "auto" : r.Route.ColorHex.ToUpperInvariant()).Distinct().ToList();
            ColorIndex = colours.Count == 1
                ? ColorOptions.ToList().FindIndex(o => (o.Hex?.ToUpperInvariant() ?? "auto") == colours[0])
                : -1;
            KeylineIndex = Same(_routes.Select(r => (int)r.Route.Keyline)) ?? -1;
        }
        finally
        {
            _syncing = false;
        }
    }

    private string Key(string property, IEnumerable<Clip> clips) => property + ":" + string.Join(",", clips.Select(c => c.Id));

    /// <summary>One step over the group; a refusal is reported and the fields show the model again.</summary>
    private void Apply(string description, string? mergeKey, Func<IEnumerable<TimelineEditResult>> edits)
    {
        if (_isLocked())
        {
            Load();   // an export runs: nothing changes, the fields show the model again
            return;
        }
        var result = _edit.EditAsOneStep(description, mergeKey, () => edits().ToList());
        if (!result.Success) _status.Report(result.Message ?? Strings.Inspector_ClipNotChanged);
        Load();
    }

    partial void OnBackgroundIndexChanged(int value)
    {
        if (_syncing || value < 0) return;
        var background = value == 1 ? ClipBackground.Blur : ClipBackground.Black;
        Apply("Change Background (Group)", null, () => _videos.Select(v => _edit.SetClipBackground(v.Id, background)));
    }

    [RelayCommand]
    private void FillFrame() => Frame(ClipFraming.Fill);

    [RelayCommand]
    private void FitFrame() => Frame(ClipFraming.Fit);

    private void Frame(ClipFraming framing) =>
        Apply(framing == ClipFraming.Fill ? "Fill Frame (Group)" : "Fit in Frame (Group)", null,
            () => _framed.Select(c => _edit.FrameClip(c.Id, framing)));

    partial void OnOpacityPercentChanged(decimal? value)
    {
        if (_syncing || value is not { } percent) return;
        var opacity = (double)(InspectorViewModel.WholePercent(percent) / 100m);
        Apply("Change Opacity (Group)", Key("opacity", _visuals), () => _visuals.Select(c =>
            _edit.SetClipProperties(c.Id, new ClipPropertyChange { Visual = VisualProperties.Of(c)!.Value with { Opacity = opacity } })));
    }

    partial void OnVolumePercentChanged(decimal? value)
    {
        if (_syncing || value is not { } percent) return;
        var volume = (double)(InspectorViewModel.WholePercent(percent) / 100m);
        Apply("Change Volume (Group)", Key("volume", _sounds), () => _sounds.Select(c =>
            _edit.SetClipProperties(c.Id, new ClipPropertyChange { Audio = AudioProperties.Of(c)!.Value with { Volume = volume } })));
    }

    partial void OnIsMutedChanged(bool? value)
    {
        if (_syncing || value is not { } muted) return;
        Apply(muted ? "Mute (Group)" : "Unmute (Group)", null, () => _sounds.Select(c =>
            _edit.SetClipProperties(c.Id, new ClipPropertyChange { Audio = AudioProperties.Of(c)!.Value with { IsMuted = muted } })));
    }

    partial void OnStrokeWidthChanged(decimal? value)
    {
        if (_syncing || value is not { } width) return;
        var whole = (double)Math.Round(width, MidpointRounding.AwayFromZero);
        EditStyle("Change Outline Width (Group)", Key("stroke", _videos), s => s with { StrokeWidth1080 = whole });
    }

    partial void OnColorIndexChanged(int value)
    {
        if (_syncing || value < 0 || value >= ColorOptions.Count) return;
        var hex = ColorOptions[value].Hex;
        EditStyle("Change Outline Colour (Group)", null, s => hex is null
            ? s with { ColorMode = HoldColorMode.Auto, ManualColorHex = null }
            : s with { ColorMode = HoldColorMode.Manual, ManualColorHex = hex });
    }

    partial void OnKeylineIndexChanged(int value)
    {
        if (_syncing || value < 0) return;
        EditStyle("Change Keyline (Group)", null, s => s with { Keyline = (KeylineMode)value });
    }

    /// <summary>Every marked route of the group's video clips (D053: a clip may have several), each by its own style.</summary>
    private void EditStyle(string description, string? mergeKey, Func<HoldStyle, HoldStyle> change) =>
        Apply(description, mergeKey, () => _routes.Select(r => _edit.SetHoldStyle(r.Clip.Id, change(HoldStyle.Of(r.Route)), r.Route.Id)));
}
