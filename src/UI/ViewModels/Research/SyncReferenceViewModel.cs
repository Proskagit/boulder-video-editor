using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Research;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Research;

/// <summary>The reference marking rule of Step 35.3R1 (D050 amendment of 2026-11-02) — stored in the study, one for every
/// video; its text is the method's wording, independent of the interface language.</summary>
public static class ReferenceRule
{
    public const string Id = "coros-ref-1";

    public const string Text =
        "Начало подъёма: момент, когда последняя нога отрывается от пола и спортсмен переходит из стартового положения на стену " +
        "(не первое движение рукой и не регистрация часами). Конец подъёма (необязательно): последний момент контакта со стеной " +
        "при спрыгивании (не приземление и не регистрация часами). Обе отметки выбираются вручную по видео; небольшая погрешность " +
        "допустима.";
}

/// <summary>A study video as the window lists it.</summary>
public sealed record ReferenceVideoRow(Guid VideoKey, string Display);

/// <summary>A FIT climb as the window lists it (all of C1–C14, no hint from file names or times).</summary>
public sealed record ReferenceClimbRow(ClimbIdentity Identity, string Display, bool IsConfirmed);

/// <summary>A mark of the selected video.</summary>
public sealed record ReferenceMarkRow(Guid MarkId, string Display);

/// <summary>
/// The Debug-only reference marking of Step 35.3R1 (the first mode of the window «Проверка синхронизации COROS»): marks
/// the start (required) and the end (optional) of a climb on a study video at the ordinary Source viewer's position,
/// confirms a FIT climb by hand or records «not matched», sets the coverage, saves and freezes the study. A video is marked
/// only when it maps to the project's asset (full path and size) and is verified by SHA-256 and unchanged since; the
/// study needs a marking rule first. It never changes the project, the Source viewer or the timeline, and never uses a
/// proposal, a zero hypothesis or the training mapping.
/// </summary>
public sealed partial class SyncReferenceViewModel : ObservableObject
{
    private readonly ISyncStudyService _study;
    private readonly IStudySourceView _source;
    private readonly IProjectService _projects;

    public SyncReferenceViewModel(ISyncStudyService study, IStudySourceView source, IProjectService projects)
    {
        _study = study;
        _source = source;
        _projects = projects;
        _source.Changed += (_, _) => PositionText = string.Format(Strings.Reference_Position, Clock(_source.Position));
        Refresh();
    }

    public ObservableCollection<ReferenceVideoRow> Videos { get; } = new();
    public ObservableCollection<ReferenceClimbRow> Climbs { get; } = new();
    public ObservableCollection<ReferenceMarkRow> Marks { get; } = new();

    [ObservableProperty] private ReferenceVideoRow? _selectedVideo;
    [ObservableProperty] private ReferenceClimbRow? _selectedClimb;
    [ObservableProperty] private ReferenceMarkRow? _selectedMark;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _ruleText = "";
    [ObservableProperty] private bool _hasRule;

    partial void OnSelectedVideoChanged(ReferenceVideoRow? value) => RefreshMarks();

    private SyncStudy? Study => _study.Study;

    // ---- refresh ---------------------------------------------------------------------------------------------------

    public void Refresh()
    {
        if (Study is not { } study)
        {
            RuleText = ReferenceRule.Text;
            HasRule = false;
            return;
        }
        HasRule = study.Draft.MarkingRule is not null;
        RuleText = study.Draft.MarkingRule?.Text ?? ReferenceRule.Text;

        var selectedVideo = SelectedVideo?.VideoKey;
        Videos.Clear();
        foreach (var v in study.Draft.Videos.OrderBy(v => v.OriginalFileName, StringComparer.Ordinal))
        {
            var marks = study.Draft.Marks.Count(m => m.VideoKey == v.VideoKey && m.Withdrawn is null);
            Videos.Add(new ReferenceVideoRow(v.VideoKey, string.Format(Strings.Reference_VideoRow, v.OriginalFileName, Coverage(v.CoverageStatus), marks)));
        }
        SelectedVideo = Videos.FirstOrDefault(r => r.VideoKey == selectedVideo);

        var selectedClimb = SelectedClimb?.Identity;
        Climbs.Clear();
        var offset = study.Draft.Fit.UtcOffsetMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        foreach (var c in study.Draft.Fit.Climbs.OrderBy(c => c.ClimbNumber))
        {
            var holder = study.Draft.ConfirmedHolder(c.Identity);
            var text = string.Format(Strings.Reference_ClimbRow, c.ClimbNumber, TrainingText.WatchClock(c.StartUtc, offset),
                (c.ElapsedTicks / (double)TimeSpan.TicksPerSecond).ToString("0.0", CultureInfo.CurrentCulture));
            if (c.ClimbNumber == 1) text += Strings.Reference_ClimbNotFilmed;     // C1: not on the videos, outside B6
            if (holder is not null) text += string.Format(Strings.Reference_ClimbConfirmedBy, VideoName(holder.VideoKey), Clock(new MediaTime(holder.StartSourceTicks)));
            Climbs.Add(new ReferenceClimbRow(c.Identity, text, holder is not null));
        }
        SelectedClimb = Climbs.FirstOrDefault(r => r.Identity == selectedClimb);
        RefreshMarks();
    }

    private void RefreshMarks()
    {
        var selected = SelectedMark?.MarkId;
        Marks.Clear();
        if (Study is not { } study || SelectedVideo is not { } video) return;
        foreach (var m in study.Draft.Marks.Where(m => m.VideoKey == video.VideoKey).OrderBy(m => m.StartSourceTicks))
        {
            var end = m.EndSourceTicks is { } e ? Clock(new MediaTime(e)) : Strings.Reference_NoEnd;
            var decision = study.Draft.Decision(m.MarkId) switch
            {
                { State: MatchState.Confirmed, Climb: { } c } => string.Format(Strings.Reference_DecisionConfirmed, c.ClimbNumber),
                { State: MatchState.NotMatched, Reason: { } r } => string.Format(Strings.Reference_DecisionNotMatched, Reason(r)),
                { State: MatchState.Rejected } => Strings.Reference_DecisionRejected,
                _ => Strings.Reference_DecisionNone,
            };
            var state = m.State switch
            {
                MarkState.Active => "",
                MarkState.Stale => Strings.Reference_MarkStale,
                _ => Strings.Reference_MarkWithdrawn,
            };
            Marks.Add(new ReferenceMarkRow(m.MarkId, string.Format(Strings.Reference_MarkRow, Clock(new MediaTime(m.StartSourceTicks)), end, decision) + state));
        }
        SelectedMark = Marks.FirstOrDefault(r => r.MarkId == selected);
    }

    // ---- study -----------------------------------------------------------------------------------------------------

    /// <summary>Opens the study file given on the command line (never creates one).</summary>
    public async Task<bool> OpenStudyAsync(string path)
    {
        var result = await _study.OpenStudyAsync(path);
        Message = result.Ok ? string.Format(Strings.Reference_Opened, Path.GetFileName(path)) : string.Format(Strings.Reference_Refused, result.Problem);
        Refresh();
        return result.Ok;
    }

    [RelayCommand]
    private void SetRule()
    {
        if (Study is null) return;
        Report(_study.Editor.SetMarkingRule(ReferenceRule.Id, ReferenceRule.Text));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await _study.SaveStudyAsync(_projects.Current.ProjectFolderPath);
        Message = result.Ok ? Strings.Reference_Saved : string.Format(Strings.Reference_Refused, result.Problem);
    }

    [RelayCommand]
    private async Task VerifyAllAsync()
    {
        if (Study is null) return;
        Message = Strings.Reference_CheckingAll;
        var states = await _study.VerifyAsync();
        var bad = states.Count(s => s.Value != StudyFileState.Verified);
        Message = bad == 0 ? Strings.Reference_AllVerified : string.Format(Strings.Reference_NotAllVerified, bad);
        Refresh();
    }

    [RelayCommand]
    private void Freeze()
    {
        var result = _study.Freeze(Strings.Reference_FreezeNote);
        Message = result.Ok ? string.Format(Strings.Reference_Frozen, result.Revision!.Number) : string.Format(Strings.Reference_Refused, result.Problem);
        Refresh();
    }

    // ---- videos ----------------------------------------------------------------------------------------------------

    /// <summary>Opens the selected video in the ordinary Source viewer — after its SHA-256 check the first time.</summary>
    [RelayCommand]
    private async Task OpenVideoAsync()
    {
        if (Study is not { } study || SelectedVideo is not { } row) return;
        var match = StudyAssetMap.Find(study, row.VideoKey, _projects.Current.MediaAssets);
        if (!match.Ok)
        {
            Message = Blocked(Problem(match.Problem!.Value));
            return;
        }
        if (_study.StateOf(row.VideoKey) != StudyFileState.Verified || !_study.IsFresh(row.VideoKey))
        {
            Message = string.Format(Strings.Reference_Checking, VideoName(row.VideoKey));
            var state = await _study.VerifyAsync(row.VideoKey);
            if (state != StudyFileState.Verified)
            {
                Message = Blocked(string.Format(Strings.Reference_NotVerified, state));
                return;
            }
        }
        Message = _source.Open(match.Asset!) is { } reason ? Blocked(reason) : string.Format(Strings.Reference_Shown, VideoName(row.VideoKey));
    }

    /// <summary>Why the selected video cannot be marked now, or null: a rule, the mapping, the fresh check, shown in Source.</summary>
    private string? MarkingBlocker()
    {
        if (Study is not { } study) return Strings.Reference_NoStudy;
        if (study.Draft.MarkingRule is null) return Strings.Reference_NoRule;
        if (SelectedVideo is not { } row) return Strings.Reference_NoVideo;
        var match = StudyAssetMap.Find(study, row.VideoKey, _projects.Current.MediaAssets);
        if (!match.Ok) return Problem(match.Problem!.Value);
        if (!_study.IsFresh(row.VideoKey)) return Strings.Reference_NotFresh;
        if (_source.Asset is not { } shown || !ReferenceEquals(shown, match.Asset)) return Strings.Reference_NotShown;
        return null;
    }

    // ---- marks -----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void MarkStart()
    {
        if (MarkingBlocker() is { } blocked) { Message = Blocked(blocked); return; }
        var edit = _study.Editor.AddMark(SelectedVideo!.VideoKey, _source.Position.Ticks, null, "", _source.ShownFramePts);
        if (Report(edit) && edit.CreatedId is { } id) SelectedMark = Marks.FirstOrDefault(m => m.MarkId == id);
    }

    [RelayCommand]
    private void MarkEnd()
    {
        if (MarkingBlocker() is { } blocked) { Message = Blocked(blocked); return; }
        if (SelectedMarkOf() is not { } mark) { Message = Strings.Reference_NoMark; return; }
        Report(_study.Editor.UpdateMark(mark.MarkId, mark.StartSourceTicks, _source.Position.Ticks, mark.Note, null, _source.ShownFramePts));
    }

    [RelayCommand]
    private void MoveStart()
    {
        if (MarkingBlocker() is { } blocked) { Message = Blocked(blocked); return; }
        if (SelectedMarkOf() is not { } mark) { Message = Strings.Reference_NoMark; return; }
        Report(_study.Editor.UpdateMark(mark.MarkId, _source.Position.Ticks, mark.EndSourceTicks, mark.Note, _source.ShownFramePts, null));
    }

    [RelayCommand]
    private void ClearEnd()
    {
        if (MarkingBlocker() is { } blocked) { Message = Blocked(blocked); return; }
        if (SelectedMarkOf() is not { } mark) { Message = Strings.Reference_NoMark; return; }
        Report(_study.Editor.UpdateMark(mark.MarkId, mark.StartSourceTicks, null, mark.Note));
    }

    [RelayCommand]
    private void GoToStart() => GoTo(m => m.StartSourceTicks);

    [RelayCommand]
    private void GoToEnd() => GoTo(m => m.EndSourceTicks);

    private void GoTo(Func<ClimbMark, long?> boundary)
    {
        if (SelectedMarkOf() is not { } mark || boundary(mark) is not { } ticks) { Message = Strings.Reference_NoMark; return; }
        var match = StudyAssetMap.Find(Study!, mark.VideoKey, _projects.Current.MediaAssets);
        if (_source.Asset is not { } shown || !ReferenceEquals(shown, match.Asset)) { Message = Blocked(Strings.Reference_NotShown); return; }
        _source.GoTo(new MediaTime(ticks));
    }

    [RelayCommand]
    private void Withdraw()
    {
        if (SelectedMarkOf() is { } mark) Report(_study.Editor.WithdrawMark(mark.MarkId, ""));
    }

    [RelayCommand]
    private void Reconfirm()
    {
        if (SelectedMarkOf() is { } mark) Report(_study.Editor.ReconfirmMark(mark.MarkId, ""));
    }

    // ---- decisions and coverage -------------------------------------------------------------------------------------

    /// <summary>Confirms the selected FIT climb for the selected mark — by hand; refused (naming the holder) when another mark
    /// already confirms that climb (one camera).</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (SelectedMarkOf() is not { } mark) { Message = Strings.Reference_NoMark; return; }
        if (SelectedClimb is not { } climb) { Message = Strings.Reference_NoClimb; return; }
        Report(_study.Editor.DecideMatch(mark.MarkId, MatchState.Confirmed, climb.Identity, null, "", MatchBasis.Manual));
    }

    [RelayCommand]
    private void NotMatched(MatchReason reason)
    {
        if (SelectedMarkOf() is not { } mark) { Message = Strings.Reference_NoMark; return; }
        Report(_study.Editor.DecideMatch(mark.MarkId, MatchState.NotMatched, null, reason, "", MatchBasis.Manual));
    }

    [RelayCommand]
    private void SetCoverage(CoverageState state)
    {
        if (SelectedVideo is { } row) Report(_study.Editor.SetCoverage(row.VideoKey, state, ""));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private ClimbMark? SelectedMarkOf() => SelectedMark is { } row ? Study?.Draft.Mark(row.MarkId) : null;

    /// <summary>Shows the outcome of an edit and refreshes; true when done.</summary>
    private bool Report(StudyEdit edit)
    {
        if (edit.Ok)
        {
            Message = Strings.Reference_Done;
            Refresh();
            return true;
        }
        Message = edit is { Refusal: StudyRefusal.ClimbAlreadyConfirmed, RelatedId: { } holder } && Study?.Draft.Mark(holder) is { } other
            ? string.Format(Strings.Reference_ClimbTaken, VideoName(other.VideoKey), Clock(new MediaTime(other.StartSourceTicks)))
            : string.Format(Strings.Reference_Refused, edit.Refusal);
        return false;
    }

    private string VideoName(Guid videoKey) => Study?.Draft.Video(videoKey)?.OriginalFileName ?? videoKey.ToString();

    private static string Blocked(string reason) => string.Format(Strings.Reference_Blocked, reason);

    private static string Clock(MediaTime t) =>
        TimeSpan.FromTicks(t.Ticks).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

    private static string Problem(StudyAssetProblem problem) => problem switch
    {
        StudyAssetProblem.NotInProject => Strings.Reference_NotInProject,
        StudyAssetProblem.SizeDiffers => Strings.Reference_SizeDiffers,
        StudyAssetProblem.Ambiguous => Strings.Reference_Ambiguous,
        _ => Strings.Reference_NoLocation,
    };

    private static string Coverage(CoverageState state) => state switch
    {
        CoverageState.HasClimbs => Strings.Reference_CoverageHasClimbs,
        CoverageState.ReviewedNoClimbs => Strings.Reference_CoverageNoClimbs,
        _ => Strings.Reference_CoverageUnknown,
    };

    private static string Reason(MatchReason reason) => reason switch
    {
        MatchReason.NotAClimb => Strings.Reference_ReasonNotAClimb,
        MatchReason.NoFitClimb => Strings.Reference_ReasonNoFitClimb,
        MatchReason.Ambiguous => Strings.Reference_ReasonAmbiguous,
        _ => reason.ToString(),
    };
}
