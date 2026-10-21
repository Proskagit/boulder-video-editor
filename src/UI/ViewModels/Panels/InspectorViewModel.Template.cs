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

/// <summary>
/// Phase 29 (D045 §6): a template's own fields — REC / VHS: the date; «Боулдер»: the route, the grade, the status and the
/// route's colour; «Хайлайт»: the title and the subtitle. A typed field applies live and merges with the next change of the
/// same field (D017); a status or a colour swatch is one click, one undo step of its own. «На указатель» sets the moment the
/// status appears to the playhead's frame (D045 §17) — one click, one undo step. Nothing else of a template is edited here
/// (its look and timing are fixed, VHS's strength too — the product owner, 2026-10-21).
/// </summary>
public sealed partial class InspectorViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateDate))]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateBoulder))]
    [NotifyPropertyChangedFor(nameof(ShowsTemplateHighlight))]
    private TemplateKind? _templateKind;

    public bool HasTemplate => TemplateKind is not null;
    public bool ShowsTemplateDate => TemplateKind is Core.Entities.TemplateKind.Rec or Core.Entities.TemplateKind.Vhs;
    public bool ShowsTemplateBoulder => TemplateKind == Core.Entities.TemplateKind.Boulder;
    public bool ShowsTemplateHighlight => TemplateKind == Core.Entities.TemplateKind.Highlight;

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

    [RelayCommand]
    private void SetTemplateStatus(BoulderStatus status) => EditTemplate(p => p with { Status = status }, "Change Route Status");

    [RelayCommand]
    private void SetTemplateAccent(string hex) => EditTemplate(p => p with { AccentHex = hex }, "Change Route Colour");

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
            TemplateStatusAtText = StatusAtText(template);
            foreach (var option in TemplateStatuses) option.IsSelected = option.Status == template.Status;
        }
        finally
        {
            _syncing = was;
        }
    }
}
