using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A status button of «Боулдер» (D045 §1.3): its status, its name and whether it is the template's.</summary>
public sealed partial class TemplateStatusOption(BoulderStatus status, string name) : ObservableObject
{
    public BoulderStatus Status { get; } = status;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A choice button of the heart-rate overlay (Phase 35.5): its look or its corner, its name and whether it is the
/// overlay's.</summary>
public sealed partial class TemplateChoiceOption<T>(T value, string name) : ObservableObject
{
    public T Value { get; } = value;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Phase 29 (D045 §6): a template's own fields — REC / VHS: the date; «Боулдер»: the route, the grade, the status and the
/// route's colour; «Хайлайт»: the title and the subtitle. A typed field applies live and merges with the next change of the
/// same field (D017); a status or a colour swatch is one click, one undo step of its own. «На указатель» sets the moment the
/// status appears to the playhead's frame (D045 §17) — one click, one undo step. Nothing else of a template is edited here
/// (its look and timing are fixed, VHS's strength too — the product owner, 2026-10-21). The heart-rate overlay (Phase 35.5):
/// its look, its corner and its heart's colour, each one click, one undo step; a hint says where its BPM comes from, or why
/// the project gives it none.
/// </summary>
public sealed partial class InspectorViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateDate))]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateBoulder))]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateHighlight))]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateHeartRate))]
    [NotifyPropertyChangedFor(nameof(ShowsHeartRateSettings))]
    private TemplateKind? _templateKind;

    public bool HasTemplate => TemplateKind is not null;
    public bool ShowsTemplateDate => TemplateKind is Core.Entities.TemplateKind.Rec or Core.Entities.TemplateKind.Vhs;
    public bool ShowsTemplateBoulder => TemplateKind == Core.Entities.TemplateKind.Boulder;
    public bool ShowsTemplateHighlight => TemplateKind == Core.Entities.TemplateKind.Highlight;
    public bool ShowsTemplateHeartRate => TemplateKind == Core.Entities.TemplateKind.HeartRate;

    /// <summary>D055 §5: the heart-rate settings (look, corner, heart's colour, hint) — the overlay's, and a green route area's
    /// while it shows the heart rate.</summary>
    public bool ShowsHeartRateSettings => ShowsTemplateHeartRate || (ShowsTemplateBoulder && TemplateShowHeartRate);

    /// <summary>D055 §5: «Показывать пульс» of a «Боулдер» (one click, one undo step).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHeartRateSettings))]
    private bool _templateShowHeartRate = true;

    /// <summary>The heart's colour shown: the overlay's accent, a «Боулдер»'s own heart colour (D055 §5).</summary>
    [ObservableProperty] private string _templateHeartHex = TemplateRules.HeartRateAccentHex;

    /// <summary>The heart-rate overlay: where its BPM comes from, or why the project gives it none.</summary>
    [ObservableProperty] private string _heartRateHint = string.Empty;

    public IReadOnlyList<TemplateChoiceOption<HeartRateLook>> HeartRateLooks { get; } = new TemplateChoiceOption<HeartRateLook>[]
    {
        new(HeartRateLook.Plate, Strings.HeartRateLook_Plate), new(HeartRateLook.Plain, Strings.HeartRateLook_Plain)
    };

    public IReadOnlyList<TemplateChoiceOption<OverlayCorner>> HeartRateCorners { get; } = new TemplateChoiceOption<OverlayCorner>[]
    {
        new(OverlayCorner.TopLeft, Strings.OverlayCorner_TopLeft), new(OverlayCorner.TopRight, Strings.OverlayCorner_TopRight),
        new(OverlayCorner.BottomLeft, Strings.OverlayCorner_BottomLeft), new(OverlayCorner.BottomRight, Strings.OverlayCorner_BottomRight)
    };

    [ObservableProperty] private string _templateDate = string.Empty;
    [ObservableProperty] private string _templateRoute = string.Empty;
    [ObservableProperty] private string _templateGrade = string.Empty;
    [ObservableProperty] private string _templateTitle = string.Empty;
    [ObservableProperty] private string _templateSubtitle = string.Empty;
    [ObservableProperty] private string _templateAccentHex = TemplateRules.DefaultAccentHex;

    /// <summary>«Боулдер» with a status: the moment it appears, as the Inspector shows it (null: no status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateStatusAt))]
    private string? _templateStatusAtText;

    public bool ShowsTemplateStatusAt => TemplateStatusAtText is not null;

    /// <summary>The timeline's playhead (set by the main window); null: none (design time, tests without one).</summary>
    public Func<MediaTime>? Playhead { get; set; }

    public int MaxTemplateDateLength => TemplateRules.MaxDateLength;
    public int MaxTemplateRouteLength => TemplateRules.MaxRouteLength;
    public int MaxTemplateGradeLength => TemplateRules.MaxGradeLength;
    public int MaxTemplateTitleLength => TemplateRules.MaxTitleLength;
    public int MaxTemplateSubtitleLength => TemplateRules.MaxSubtitleLength;

    /// <summary>The route colours of «Боулдер»: the hold markup palette's usual route colours (D034).</summary>
    public IReadOnlyList<ColorSwatch> RouteSwatches { get; } = new ColorSwatch[]
    {
        new("#FFEA00", Strings.Swatch_Yellow), new("#FF6D00", Strings.Swatch_Orange), new("#FF1744", Strings.Swatch_Red),
        new("#FF4081", Strings.Swatch_Pink), new("#D500F9", Strings.Swatch_Violet), new("#2979FF", Strings.Swatch_Blue),
        new("#00E676", Strings.Swatch_Green), new("#FFFFFF", Strings.Swatch_White), new("#000000", Strings.Swatch_Black)
    };

    public IReadOnlyList<TemplateStatusOption> TemplateStatuses { get; } = new TemplateStatusOption[]
    {
        new(BoulderStatus.None, Strings.TemplateStatus_None), new(BoulderStatus.Top, Strings.TemplateStatus_Top),
        new(BoulderStatus.Attempt, Strings.TemplateStatus_Attempt)
    };

    partial void OnTemplateKindChanged(TemplateKind? value) => OnPropertyChanged(nameof(HasTemplate));

    partial void OnTemplateDateChanged(string value) => EditTemplate(p => p with { Date = value }, null);
    partial void OnTemplateRouteChanged(string value) => EditTemplate(p => p with { Route = value }, null);
    partial void OnTemplateGradeChanged(string value) => EditTemplate(p => p with { Grade = value }, null);
    partial void OnTemplateTitleChanged(string value) => EditTemplate(p => p with { Title = value }, null);
    partial void OnTemplateSubtitleChanged(string value) => EditTemplate(p => p with { Subtitle = value }, null);
    partial void OnTemplateShowHeartRateChanged(bool value)
    {
        EditTemplate(p => p with { ShowHeartRate = value }, value ? "Show Heart Rate" : "Hide Heart Rate");
        if (!_syncing) RefreshHeartRateHint();
    }

    [RelayCommand]
    private void SetTemplateStatus(BoulderStatus status) => EditTemplate(p => p with { Status = status }, "Change Route Status");

    [RelayCommand]
    private void SetTemplateAccent(string hex) => EditTemplate(p => p with { AccentHex = hex },
        TemplateKind == Core.Entities.TemplateKind.HeartRate ? "Change Heart Colour" : "Change Route Colour");

    /// <summary>The heart's colour (one click, one undo step): the overlay's accent, a «Боулдер»'s heart colour (D055 §5).</summary>
    [RelayCommand]
    private void SetHeartColour(string hex) => EditTemplate(
        p => TemplateKind == Core.Entities.TemplateKind.HeartRate ? p with { AccentHex = hex } : p with { HeartHex = hex }, "Change Heart Colour");

    [RelayCommand]
    private void SetHeartRateLook(HeartRateLook look) => EditTemplate(p => p with { Look = look }, "Change Heart Rate Look");

    [RelayCommand]
    private void SetHeartRateCorner(OverlayCorner corner) => EditTemplate(p => p with { Corner = corner }, "Move Heart Rate");

    /// <summary>The heart-rate hint: no training, a training whose FIT is not available, or where the value comes from (the
    /// reasons it may still show nothing) — for a green route area (D055 §4) its clip's video and, without a reliable file
    /// time, why.</summary>
    private void RefreshHeartRateHint()
    {
        var area = _clip is TemplateClip { Kind: Core.Entities.TemplateKind.Boulder };
        HeartRateHint = _training?.Training switch
        {
            null => area ? Strings.HeartRate_AreaNoTraining : Strings.HeartRate_NoTraining,
            { IsUsable: false } => Strings.HeartRate_TrainingUnavailable,
            { Workout: { } workout } when area => AreaHint(workout),
            _ => Strings.HeartRate_Hint
        };

        string AreaHint(Core.Telemetry.ClimbingWorkout workout)
        {
            if (_clip is not TemplateClip { AnchorClipId: { } anchorId } || _training!.Project.Timeline.VideoTracks.SelectMany(t => t.Clips)
                    .OfType<VideoClip>().FirstOrDefault(c => c.Id == anchorId) is not { } anchor)
                return Strings.HeartRate_AreaNotAnchored;
            var asset = _training!.Project.MediaAssets.FirstOrDefault(a => a.Id == anchor.MediaAssetId);
            if (asset is not null && Core.Telemetry.FitRouteBinding.FileTimeOf(asset, workout) is not null) return Strings.HeartRate_AreaHint;
            return asset is not null && Core.Telemetry.FitRouteBinding.FileTimesDisagree(asset, workout)
                ? Strings.HeartRate_AreaFileTimesDisagree : Strings.HeartRate_AreaNoFileTime;
        }
    }

    /// <summary>D045 §17: the status appears at the playhead's frame — on the template's clock (the counter's: the time since
    /// its original start, continued over a split). The playhead must be on the template's clip.</summary>
    [RelayCommand]
    private void SetTemplateStatusAtPlayhead()
    {
        if (_clip is not TemplateClip template || Playhead is null) return;
        var rate = _rate;
        var frame = Playhead().ToFrameFloor(rate);
        var start = template.TimelineStart.ToFrameFloor(rate);
        if (frame < start || frame >= template.TimelineEnd.ToFrameFloor(rate))
        {
            _status.Report(Strings.TemplateStatusAt_PlayheadOutside);
            return;
        }
        var at = MediaTime.FromFrame(template.CounterStart.ToNearestFrame(rate) + (frame - start), rate);
        EditTemplate(p => p with { StatusAt = at }, "Set Status Moment");
    }

    /// <summary>The status moment as the Inspector shows it: seconds on the template's clock, and a warning when it lies after
    /// the clip's end (the status then doesn't appear).</summary>
    private static string? StatusAtText(TemplateClip template)
    {
        if (template.Kind != Core.Entities.TemplateKind.Boulder || template.Status == BoulderStatus.None) return null;
        var seconds = template.StatusAt.TotalSeconds;
        return template.StatusAt >= template.CounterStart + template.Duration
            ? string.Format(Strings.TemplateStatusAt_Beyond, seconds)
            : string.Format(Strings.TemplateStatusAt_Value, seconds);
    }

    /// <summary>A template field change; <paramref name="gesture"/> names a discrete choice's own undo step (null: merges).</summary>
    private void EditTemplate(Func<TemplateProperties, TemplateProperties> change, string? gesture)
    {
        if (_syncing || _clip is null || TemplateProperties.Of(_clip) is not { } current || RejectWhileLocked()) return;
        var next = change(current);
        if (next == current) { SyncTemplate(); return; }   // a click on the status already shown: the buttons show the model
        Apply(new ClipPropertyChange { Template = next, Gesture = gesture is null ? null : new ClipPropertyGesture(gesture) });
    }

    /// <summary>Fills the template's fields from the shown clip (inside <see cref="SyncFromModel"/>, or after a click).</summary>
    private void SyncTemplate()
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            if (_clip is not TemplateClip template)
            {
                TemplateKind = null;
                TemplateStatusAtText = null;
                return;
            }
            TemplateKind = template.Kind;
            TemplateDate = template.Date;
            TemplateRoute = template.Route;
            TemplateGrade = template.Grade;
            TemplateTitle = template.Title;
            TemplateSubtitle = template.Subtitle;
            TemplateAccentHex = template.AccentHex;
            TemplateShowHeartRate = template.ShowHeartRate;
            TemplateHeartHex = template.Kind == Core.Entities.TemplateKind.HeartRate ? template.AccentHex : template.HeartHex;
            TemplateStatusAtText = StatusAtText(template);
            foreach (var option in TemplateStatuses) option.IsSelected = option.Status == template.Status;
            foreach (var option in HeartRateLooks) option.IsSelected = option.Value == template.Look;
            foreach (var option in HeartRateCorners) option.IsSelected = option.Value == template.Corner;
            if (template.Kind is Core.Entities.TemplateKind.HeartRate or Core.Entities.TemplateKind.Boulder) RefreshHeartRateHint();
        }
        finally
        {
            _syncing = was;
        }
    }
}
