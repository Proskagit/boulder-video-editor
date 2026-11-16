using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D061 in the Inspector — «Слежение и люди» of a video clip: Follow on / off, its smoothness, the climber's pick in the
/// Preview; the outlines hidden behind people; the people's masks shown in the Preview; the analysis's state, progress,
/// cancel and retry. Each change of the clip is one undo step; the analysis itself is never part of the project.
/// </summary>
public sealed partial class InspectorViewModel
{
    private PeopleAnalysisCoordinator? _people;

    /// <summary>The people analyses (set by the main window); null: no people section.</summary>
    public PeopleAnalysisCoordinator? People
    {
        get => _people;
        set
        {
            if (_people is not null)
            {
                _people.StatusChanged -= OnPeopleChanged;
                _people.AnalysesChanged -= OnPeopleChanged;
            }
            _people = value;
            if (_people is not null)
            {
                _people.StatusChanged += OnPeopleChanged;
                _people.AnalysesChanged += OnPeopleChanged;
            }
            RefreshPeople();
        }
    }

    /// <summary>The Preview starts / ends the pick of the climber for this clip (wired by the main window).</summary>
    public event EventHandler<Guid>? PersonPickRequested;
    public event EventHandler? PersonPickCancelRequested;

    /// <summary>The people's masks shown in the Preview (session only; wired by the main window).</summary>
    public event EventHandler<bool>? ShowPeopleMasksChanged;

    [ObservableProperty] private bool _hasPeopleSection;
    [ObservableProperty] private bool _followEnabled;
    [ObservableProperty] private int _followSmoothnessIndex = 1;
    [ObservableProperty] private string _followStatusText = "";
    [ObservableProperty] private bool _followStatusIsProblem;
    [ObservableProperty] private bool _hidesOutlinesBehindPeople;
    [ObservableProperty] private string _occlusionStatusText = "";
    [ObservableProperty] private bool _occlusionStatusIsProblem;
    [ObservableProperty] private bool _showPeopleMasks;
    [ObservableProperty] private bool _isPeopleAnalysing;
    [ObservableProperty] private double _peopleProgressPercent;
    [ObservableProperty] private bool _canRetryPeopleAnalysis;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickPersonCommand))]
    private bool _canPickPerson;

    [ObservableProperty] private bool _isPickingPerson;

    /// <summary>«Мягкая», «Обычная», «Быстрая» — <see cref="FollowSmoothness"/> in order.</summary>
    public IReadOnlyList<string> FollowSmoothnessNames { get; } = new[] { Strings.Follow_Soft, Strings.Follow_Normal, Strings.Follow_Fast };

    private void OnPeopleChanged(object? sender, EventArgs e) => RefreshPeople();

    partial void OnFollowEnabledChanged(bool value)
    {
        if (_syncing || _clip is not VideoClip clip || RejectWhileLocked()) return;
        if (value == clip.Follow is not null) return;
        Apply(_edit.SetClipFollow(clip.Id, value ? new FollowSettings(Smoothness: SmoothnessAt(FollowSmoothnessIndex)) : null));
    }

    partial void OnFollowSmoothnessIndexChanged(int value)
    {
        if (_syncing || _clip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
        Apply(_edit.SetClipFollow(clip.Id, follow with { Smoothness = SmoothnessAt(value) }));
    }

    partial void OnHidesOutlinesBehindPeopleChanged(bool value)
    {
        if (_syncing || _clip is not VideoClip clip || RejectWhileLocked()) return;
        Apply(_edit.SetClipHidesOutlinesBehindPeople(clip.Id, value));
    }

    partial void OnShowPeopleMasksChanged(bool value) => ShowPeopleMasksChanged?.Invoke(this, value);

    private void Apply(Core.Interfaces.TimelineEditResult result)
    {
        if (result.Success) return;
        _status.Report(result.Message ?? "");
        ShowModelAfterRejection();
    }

    private static FollowSmoothness SmoothnessAt(int index) => index switch
    {
        0 => FollowSmoothness.Soft,
        2 => FollowSmoothness.Fast,
        _ => FollowSmoothness.Normal
    };

    /// <summary>«Выбрать скалолаза»: the Preview shows the people of the shown frame; a click on one picks it.</summary>
    [RelayCommand(CanExecute = nameof(CanPickPerson))]
    private void PickPerson()
    {
        if (_clip is not VideoClip clip) return;
        IsPickingPerson = true;
        PersonPickRequested?.Invoke(this, clip.Id);
    }

    [RelayCommand]
    private void CancelPickPerson()
    {
        IsPickingPerson = false;
        PersonPickCancelRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Preview's pick (wired by the main window): the clip's climber — one undo step.</summary>
    public void OnPersonPicked(Guid clipId, MediaTime sourceTime, NormalizedPoint point)
    {
        IsPickingPerson = false;
        var clip = _clip as VideoClip;
        if (clip is null || clip.Id != clipId || RejectWhileLocked()) return;
        var follow = clip.Follow ?? new FollowSettings(Smoothness: SmoothnessAt(FollowSmoothnessIndex));
        var result = _edit.SetClipFollow(clip.Id, follow with { TargetTime = sourceTime, TargetPoint = point });
        if (result.Success) _status.Report(Strings.Follow_Picked);
        else Apply(result);
    }

    public void OnPersonPickEnded() => IsPickingPerson = false;

    [RelayCommand]
    private void CancelPeopleAnalysis()
    {
        if (_clip is VideoClip clip) _people?.Cancel(clip.MediaAssetId);
    }

    [RelayCommand]
    private void RetryPeopleAnalysis()
    {
        if (_clip is VideoClip clip) _people?.Retry(clip.MediaAssetId);
    }

    /// <summary>Fills the section from the clip and the analysis (also after every timeline change and analysis event).</summary>
    private void RefreshPeople()
    {
        var clip = _clip as VideoClip;
        HasPeopleSection = clip is not null && _people is not null;
        if (clip is null || _people is null)
        {
            IsPeopleAnalysing = false;
            CanRetryPeopleAnalysis = false;
            CanPickPerson = false;
            return;
        }
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            FollowEnabled = clip.Follow is not null;
            FollowSmoothnessIndex = clip.Follow?.Smoothness switch { FollowSmoothness.Soft => 0, FollowSmoothness.Fast => 2, _ => 1 };
            HidesOutlinesBehindPeople = clip.HidesOutlinesBehindPeople;
        }
        finally
        {
            _syncing = wasSyncing;
        }

        var tracks = _people.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks);
        var masks = _people.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks);
        var running = tracks.State == PeopleAnalysisState.Analysing ? tracks : masks.State == PeopleAnalysisState.Analysing ? masks : (PeopleAnalysisStatus?)null;
        IsPeopleAnalysing = tracks.IsBusy || masks.IsBusy;
        PeopleProgressPercent = running is { } r ? Math.Round(r.Progress * 100) : 0;
        CanRetryPeopleAnalysis = tracks.State is PeopleAnalysisState.Failed or PeopleAnalysisState.Cancelled ||
                                 masks.State is PeopleAnalysisState.Failed or PeopleAnalysisState.Cancelled;
        CanPickPerson = clip.Follow is not null && tracks.State == PeopleAnalysisState.Ready;

        (FollowStatusText, FollowStatusIsProblem) = clip.Follow is null ? ("", false) : FollowStatus(clip, clip.Follow, tracks);
        (OcclusionStatusText, OcclusionStatusIsProblem) = !clip.HidesOutlinesBehindPeople ? ("", false) : OcclusionStatus(clip, tracks, masks);
    }

    private (string, bool) FollowStatus(VideoClip clip, FollowSettings follow, PeopleAnalysisStatus tracks)
    {
        switch (tracks.State)
        {
            case PeopleAnalysisState.Waiting:
                return (Strings.People_Waiting, false);
            case PeopleAnalysisState.Analysing:
                return (string.Format(CultureInfo.CurrentCulture, Strings.People_Analysing, (int)Math.Floor(tracks.Progress * 100)), false);
            case PeopleAnalysisState.Cancelled:
                return (Strings.People_Cancelled, true);
            case PeopleAnalysisState.Failed:
                return (tracks.Message ?? Strings.People_Failed, true);
        }
        if (_people?.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var analysed) != true || _findAsset?.Invoke(clip.MediaAssetId) is not { } asset ||
            PlaybackSnapshotBuilder.DisplaySize(asset.Metadata) is not { } source)
            return (Strings.People_Waiting, false);
        var resolution = FollowResolution.For(clip, follow, analysed!, _canvas, source);
        return resolution.State switch
        {
            FollowState.NeedsTarget => (Strings.Follow_NeedsTarget, true),
            FollowState.TargetNotFound => (Strings.Follow_TargetNotFound, true),
            FollowState.Unavailable => (resolution.Unavailable switch
            {
                FollowUnavailable.NoRoom => Strings.Follow_NoRoom,
                FollowUnavailable.Rotation => Strings.Follow_Rotation,
                _ => Strings.Follow_TargetNotFound
            }, true),
            _ => resolution.Gaps.IsEmpty
                ? (string.Format(CultureInfo.CurrentCulture, Strings.Follow_Following, (int)Math.Round(resolution.SeenFraction * 100)), false)
                : (string.Format(CultureInfo.CurrentCulture, Strings.Follow_FollowingWithGaps, (int)Math.Round(resolution.SeenFraction * 100),
                    resolution.Gaps.Length, string.Join(", ", resolution.Gaps.Take(4).Select(g => GapText(clip, g)))), false)
        };
    }

    /// <summary>A loss in the clip's timeline time («0:12–0:15»).</summary>
    private string GapText(VideoClip clip, FollowGap gap)
    {
        string At(MediaTime source)
        {
            var timeline = clip.TimelineStart.TotalSeconds + (source - clip.SourceIn).TotalSeconds / (double)clip.Speed.ToDecimal();
            var t = TimeSpan.FromSeconds(Math.Max(0, timeline));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
        return $"{At(gap.Start)}–{At(gap.End)}";
    }

    private static (string, bool) OcclusionStatus(VideoClip clip, PeopleAnalysisStatus tracks, PeopleAnalysisStatus masks)
    {
        var status = tracks.State != PeopleAnalysisState.Ready ? tracks : masks;
        return status.State switch
        {
            PeopleAnalysisState.Waiting => (Strings.People_Waiting, false),
            PeopleAnalysisState.Analysing => (string.Format(CultureInfo.CurrentCulture,
                tracks.State == PeopleAnalysisState.Ready ? Strings.People_Segmenting : Strings.People_Analysing, (int)Math.Floor(status.Progress * 100)), false),
            PeopleAnalysisState.Cancelled => (Strings.People_Cancelled, true),
            PeopleAnalysisState.Failed => (status.Message ?? Strings.People_Failed, true),
            PeopleAnalysisState.Ready => (clip.Routes.IsDefaultOrEmpty ? Strings.Occlusion_ReadyNoRoutes : Strings.Occlusion_Ready, false),
            _ => ("", false)
        };
    }
}
