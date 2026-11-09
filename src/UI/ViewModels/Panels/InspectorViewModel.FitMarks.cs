using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Phase 37 (D051 §8.2) in the Inspector of a video clip: the FIT marks of its video — «Разметить трассу от метки» on this
/// clip, «Переместить метку к плейхеду», «Скрыть метку / отклонить подъём» — and, for a clip with a Duration hold
/// animation, its explicit link: the linked climb, «Связать с FIT-подъёмом» (an active climb without a link, of any video)
/// and «Снять FIT-связь». Every action is one Undo step of the edit service; a refusal is reported.
/// </summary>
public sealed partial class InspectorViewModel
{
    private IReadOnlyList<FitMark> _videoMarks = Array.Empty<FitMark>();
    private IReadOnlyList<FitMark> _linkableMarks = Array.Empty<FitMark>();

    private IFitMarkEditService? FitEdit => _edit as IFitMarkEditService;

    /// <summary>The video clip's video has FIT marks.</summary>
    [ObservableProperty] private bool _showsFitMarks;

    /// <summary>The marks of this clip's video, «Подъём N · чч:мм:сс · предложена».</summary>
    public ObservableCollection<string> FitMarkItems { get; } = new();

    [ObservableProperty] private int _selectedFitMarkIndex = -1;

    partial void OnSelectedFitMarkIndexChanged(int value) => NotifyFitCommands();

    /// <summary>The clip has a Duration hold animation and the training has marks: its link is shown.</summary>
    [ObservableProperty] private bool _showsFitLink;

    /// <summary>«Связана с FIT-подъёмом N» or «Не связана с FIT-подъёмом».</summary>
    [ObservableProperty] private string _fitLinkText = "";

    /// <summary>The active climbs without a link this animation can be linked to.</summary>
    public ObservableCollection<string> LinkableClimbs { get; } = new();

    [ObservableProperty] private int _selectedLinkableIndex = -1;

    partial void OnSelectedLinkableIndexChanged(int value) => NotifyFitCommands();

    private void RefreshFitMarks()
    {
        var training = _training?.Training;
        var marks = training?.Marks ?? [];
        var clip = _clip as VideoClip;
        var workout = training?.Workout;

        var videoMarks = clip is null ? [] : marks.Where(m => m.MediaAssetId == clip.MediaAssetId).ToList();
        if (!videoMarks.SequenceEqual(_videoMarks))
        {
            var selected = SelectedFitMarkIndex >= 0 && SelectedFitMarkIndex < _videoMarks.Count ? _videoMarks[SelectedFitMarkIndex].Climb : (ClimbIdentity?)null;
            _videoMarks = videoMarks;
            FitMarkItems.Clear();
            foreach (var mark in videoMarks) FitMarkItems.Add(MarkItem(mark, workout));
            SelectedFitMarkIndex = selected is { } c ? videoMarks.FindIndex(m => m.Climb == c) : -1;
        }
        ShowsFitMarks = ShowsTraining && FitEdit is not null && videoMarks.Count > 0;

        // D053: the link of the route the «Зацепки» section shows (the one under the playhead).
        var route = CurrentRoute(clip);
        var animated = route?.Animation is { Mode: HoldAnimationMode.Duration };
        ShowsFitLink = ShowsTraining && FitEdit is not null && animated && marks.Count > 0;
        var linked = LinkedMark(clip, route);
        FitLinkText = linked is null ? Strings.Inspector_FitNotLinked : string.Format(Strings.Inspector_FitLinked, linked.Climb.ClimbNumber);

        var linkable = linked is null ? marks.Where(m => m.LinkedClipId is null).ToList() : [];
        if (!linkable.SequenceEqual(_linkableMarks))
        {
            _linkableMarks = linkable;
            LinkableClimbs.Clear();
            foreach (var mark in linkable) LinkableClimbs.Add(MarkItem(mark, workout));
            SelectedLinkableIndex = -1;
        }
        NotifyFitCommands();
    }

    /// <summary>D053: the markup of the route the «Зацепки» section shows on <paramref name="clip"/>, or null.</summary>
    private HoldMarkup? CurrentRoute(VideoClip? clip)
    {
        if (clip is null) return null;
        if (HoldMarkup is not null) return HoldMarkup.CurrentRouteId is { } id ? RouteOverlays.Find(clip, id) : null;
        // Without the section (a shell without the hold markup): the clip's only marked route (a link needs its animation).
        return clip.Routes.Length == 1 ? clip.Routes[0] : null;
    }

    /// <summary>The mark linked to <paramref name="route"/> of <paramref name="clip"/>, or null.</summary>
    private FitMark? LinkedMark(VideoClip? clip, HoldMarkup? route) => clip is null || route is null ? null
        : _training?.Training?.Marks.FirstOrDefault(m => m.LinkedClipId == clip.Id && FitMarkRules.LinkedRoute(clip, m.LinkedRouteId)?.Id == route.Id);

    private static string MarkItem(FitMark mark, ClimbingWorkout? workout)
    {
        var kind = mark.Position.Kind switch
        {
            FitMarkPositionKind.Clamped => Strings.FitMark_Clamped,
            FitMarkPositionKind.Moved => Strings.FitMark_Moved,
            _ => Strings.FitMark_Proposed
        };
        var start = FitMarkRules.ClimbOf(workout, mark.Climb)?.StartUtc ?? mark.Climb.ClimbStartUtc;
        var item = string.Format(Strings.Inspector_FitMarkItem, mark.Climb.ClimbNumber, TrainingText.WatchClock(start, workout?.UtcOffset), kind);
        return mark.LinkedClipId is null ? item : item + Strings.FitMark_TipLinked;
    }

    private void NotifyFitCommands()
    {
        LayOutFromFitMarkCommand.NotifyCanExecuteChanged();
        MoveFitMarkToPlayheadCommand.NotifyCanExecuteChanged();
        DeclineFitMarkCommand.NotifyCanExecuteChanged();
        LinkFitClimbCommand.NotifyCanExecuteChanged();
        UnlinkFitClimbCommand.NotifyCanExecuteChanged();
    }

    private FitMark? SelectedVideoMark => SelectedFitMarkIndex >= 0 && SelectedFitMarkIndex < _videoMarks.Count ? _videoMarks[SelectedFitMarkIndex] : null;

    private bool CanUseSelectedMark() => IsEditingAllowed && ShowsFitMarks && SelectedVideoMark is not null;

    [RelayCommand(CanExecute = nameof(CanUseSelectedMark))]
    private void LayOutFromFitMark()
    {
        if (SelectedVideoMark is not { } mark || _clip is not VideoClip clip || RejectWhileLocked()) return;
        Report(FitEdit!.LayOutRouteFromMark(mark.Climb, clip.Id), Strings.Inspector_FitLaidOut);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedMark))]
    private void MoveFitMarkToPlayhead()
    {
        if (SelectedVideoMark is not { } mark || _clip is not VideoClip clip || Playhead is null || RejectWhileLocked()) return;
        var frame = Playhead().ToFrameFloor(_rate);
        if (!TrainingAlignment.Covers(clip, frame, _rate))
        {
            _status.Report(Strings.Inspector_AlignPlayheadOutside);
            return;
        }
        Report(FitEdit!.MoveFitMark(mark.Climb, TrainingAlignment.SourceTimeAt(clip, frame, _rate)), Strings.Inspector_FitMoved);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedMark))]
    private void DeclineFitMark()
    {
        if (SelectedVideoMark is not { } mark || RejectWhileLocked()) return;
        Report(FitEdit!.DeclineClimb(mark.Climb), Strings.FitMark_Declined);
    }

    private bool CanLinkFitClimb() => IsEditingAllowed && ShowsFitLink && SelectedLinkableIndex >= 0 && SelectedLinkableIndex < _linkableMarks.Count;

    [RelayCommand(CanExecute = nameof(CanLinkFitClimb))]
    private void LinkFitClimb()
    {
        if (!CanLinkFitClimb() || _clip is not VideoClip clip || RejectWhileLocked()) return;
        Report(FitEdit!.LinkFitClimb(_linkableMarks[SelectedLinkableIndex].Climb, clip.Id, CurrentRoute(clip)?.Id), Strings.Inspector_FitLinkDone);
    }

    private bool CanUnlinkFitClimb() => IsEditingAllowed && ShowsFitLink && _clip is VideoClip clip && LinkedMark(clip, CurrentRoute(clip)) is not null;

    [RelayCommand(CanExecute = nameof(CanUnlinkFitClimb))]
    private void UnlinkFitClimb()
    {
        if (_clip is not VideoClip clip || LinkedMark(clip, CurrentRoute(clip)) is not { } mark || RejectWhileLocked()) return;
        Report(FitEdit!.UnlinkFitClimb(mark.Climb), Strings.Inspector_FitUnlinkDone);
    }

    private void Report(TimelineEditResult result, string success)
    {
        if (!result.Success) _status.Report(result.Message ?? "");
        else if (result.Message is { } note) _status.Report(note);
        else if (!result.NoChange) _status.Report(success);
    }
}
