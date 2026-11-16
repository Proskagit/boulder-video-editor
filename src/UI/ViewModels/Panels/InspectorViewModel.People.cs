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

    /// <summary>D061 amendment 1: «Следить отсюда» plays the timeline (true), «Остановить слежение здесь» pauses it (false) —
    /// wired by the main window to the Preview.</summary>
    public event EventHandler<bool>? FollowPlaybackRequested;

    /// <summary>The shortest followed part «Остановить слежение здесь» accepts.</summary>
    public static readonly MediaTime MinFollowPart = MediaTime.FromSeconds(0.5);

    /// <summary>The clip whose followed part is being set while it plays («Следить отсюда» pressed, «Остановить» not yet).</summary>
    private Guid? _followRecordingClipId;
    private MediaTime? _followRecordingFrom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopFollowHereCommand))]
    private bool _isRecordingFollow;

    [ObservableProperty] private string _followRangeText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FollowWholeClipCommand))]
    private bool _hasFollowRange;

    /// <summary>A followed part lies under the playhead («Удалить участок слежения»).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteFollowPartCommand))]
    private bool _hasFollowPartHere;

    [ObservableProperty] private bool _hasPeopleSection;
    [ObservableProperty] private bool _followEnabled;
    [ObservableProperty] private int _followSmoothnessIndex = 1;
    [ObservableProperty] private string _followStatusText = "";
    [ObservableProperty] private bool _followStatusIsProblem;
    [ObservableProperty] private bool _hidesOutlinesBehindPeople;
    [ObservableProperty] private string _occlusionStatusText = "";
    [ObservableProperty] private bool _occlusionStatusIsProblem;
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
        Apply(_edit.SetClipFollow(clip.Id, value ? FollowSettings.Whole(SmoothnessAt(FollowSmoothnessIndex)) : null,
            hideOutlinesBehindPeople: value ? true : null));
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

    /// <summary>
    /// D061 amendment 1 (like Insta360 Studio): «Следить за скалолазом отсюда» — a new followed part from the playhead's frame
    /// (to the next part or the clip's end; Follow switched on also switches Occlusion on, one undo step) and the timeline
    /// plays: the part's people are analysed from that frame on, chunk by chunk ahead of the playback, so the camera follows
    /// at once. «Остановить слежение здесь» ends the part at the frame then shown and pauses. Each part is independent (its
    /// own person — two routes, two climbers) and is a strip on the clip in the timeline (its edges drag).
    /// </summary>
    [RelayCommand]
    private void FollowFromHere()
    {
        if (_clip is not VideoClip clip || RejectWhileLocked()) return;
        if (PlayheadSourceTime(clip) is not { } at)
        {
            _status.Report(Strings.Follow_PlayheadOutside);
            return;
        }
        var from = at <= clip.SourceIn ? (MediaTime?)null : at;
        FollowSettings follow;
        var existing = clip.Follow;
        if (existing is null || existing.IsWholeClip)
            follow = FollowSettings.Of(new FollowPart(from), existing?.Smoothness ?? SmoothnessAt(FollowSmoothnessIndex));
        else if (existing.IndexAt(at) >= 0)
        {
            _status.Report(Strings.Follow_AlreadyFollowedHere);
            return;
        }
        else
        {
            var next = existing.Parts.FirstOrDefault(p => p.From is { } f && f > at)?.From;
            // On the clip's first frame the part opens to the start — up to a part before it (beyond the clip's start).
            if (from is null) from = existing.Parts.LastOrDefault(p => p.To is { } t && t <= at)?.To;
            follow = existing.WithAdded(new FollowPart(from, next));
        }
        var result = _edit.SetClipFollow(clip.Id, follow, hideOutlinesBehindPeople: clip.Follow is null ? true : null);
        if (!result.Success)
        {
            Apply(result);
            return;
        }
        _followRecordingClipId = clip.Id;
        _followRecordingFrom = from;
        IsRecordingFollow = true;
        _status.Report(Strings.Follow_Recording);
        FollowPlaybackRequested?.Invoke(this, true);
    }

    [RelayCommand(CanExecute = nameof(IsRecordingFollow))]
    private void StopFollowHere()
    {
        var clip = _clip as VideoClip;
        var recordedFrom = _followRecordingFrom;
        IsRecordingFollow = false;
        _followRecordingClipId = null;
        FollowPlaybackRequested?.Invoke(this, false);
        if (clip?.Follow is not { } follow || RejectWhileLocked()) return;
        var index = follow.Parts.IndexOf(follow.Parts.FirstOrDefault(p => p.From == recordedFrom)!);
        if (index < 0) return;
        var part = follow.Parts[index];
        var from = part.From ?? clip.SourceIn;
        // The playhead past the clip (it played on): the part runs to its end.
        var at = PlayheadSourceTime(clip) ?? (Playhead is { } playhead && playhead() >= clip.TimelineEnd ? clip.SourceOut : (MediaTime?)null);
        if (at is not { } end || end - from < MinFollowPart)
        {
            _status.Report(Strings.Follow_PartTooShort);
            return;
        }
        if (part.To is { } limit && end >= limit) return;                 // it reached the next part: already ends there
        Apply(_edit.SetClipFollow(clip.Id, follow.WithPart(index, part with { To = end >= clip.SourceOut ? follow.OpenEnd(index) : end })));
    }

    /// <summary>Follow back to one part on the whole clip (the first part's person).</summary>
    [RelayCommand(CanExecute = nameof(HasFollowRange))]
    private void FollowWholeClip()
    {
        if (_clip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
        // The person of the part under the playhead, else of the first part shown in the clip.
        var here = PlayheadSourceTime(clip) is { } at ? follow.IndexAt(at) : -1;
        var first = here >= 0 ? follow.Parts[here]
            : follow.Parts.FirstOrDefault(p => p.RangeIn(clip.SourceIn, clip.SourceOut) is not null) ?? follow.Parts[0];
        Apply(_edit.SetClipFollow(clip.Id, FollowSettings.Of(first with { From = null, To = null }, follow.Smoothness)));
    }

    /// <summary>Removes the followed part under the playhead (the last one: Follow off).</summary>
    [RelayCommand(CanExecute = nameof(HasFollowPartHere))]
    private void DeleteFollowPart()
    {
        if (_clip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked() || PlayheadSourceTime(clip) is not { } at) return;
        var index = follow.IndexAt(at);
        if (index < 0) return;
        Apply(_edit.SetClipFollow(clip.Id, follow.WithPart(index, null)));
    }

    /// <summary>The source time of the playhead's frame in <paramref name="clip"/>; null outside the clip.</summary>
    private MediaTime? PlayheadSourceTime(VideoClip clip)
    {
        if (Playhead is null) return null;
        var position = Playhead();
        if (position < clip.TimelineStart || position >= clip.TimelineEnd) return null;
        var source = Core.Playback.HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, position.ToFrameFloor(_rate), _rate);
        return source < clip.SourceIn ? clip.SourceIn : source >= clip.SourceOut ? null : source;
    }

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
        FollowSettings follow;
        if (clip.Follow is not { } existing)
            follow = FollowSettings.Whole(SmoothnessAt(FollowSmoothnessIndex), sourceTime, point);
        else if (existing.IndexAt(sourceTime) is var index and >= 0)
            follow = existing.WithPart(index, existing.Parts[index] with { TargetTime = sourceTime, TargetPoint = point })!;
        else
        {
            _status.Report(Strings.Follow_PickOutsideParts);
            return;
        }
        var result = _edit.SetClipFollow(clip.Id, follow);
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
        if (_followRecordingClipId is { } recording &&
            (clip?.Id != recording || clip.Follow is not { } follow || !follow.Parts.Any(p => p.From == _followRecordingFrom)))
        {
            _followRecordingClipId = null;     // another clip, Follow off, or the started part undone / changed: it is over
            IsRecordingFollow = false;
        }
        HasFollowRange = clip?.Follow is { IsWholeClip: false };
        HasFollowPartHere = clip?.Follow is { } here && PlayheadSourceTime(clip) is { } now && here.IndexAt(now) >= 0;
        FollowRangeText = clip?.Follow is { } shown ? RangeText(clip, shown) : "";
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
        // Live (D061 amendment 1): the climber can be picked on any frame already analysed.
        CanPickPerson = clip.Follow is not null && tracks.State is PeopleAnalysisState.Ready or PeopleAnalysisState.Analysing;

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
        var (text, problem) = resolution.State switch
        {
            FollowState.NeedsTarget => (Strings.Follow_NeedsTarget, true),
            FollowState.TargetNotFound => (Strings.Follow_TargetNotFound, true),
            FollowState.Unavailable when resolution.Unavailable == FollowUnavailable.OutsideRange => (Strings.Follow_OutsideRange, false),
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
        // D061 amendment 1: a part that does not follow while another one does.
        if (resolution.State == FollowState.Following && resolution.Problem is { } part)
        {
            var why = part.State switch
            {
                FollowState.NeedsTarget => Strings.Follow_NeedsTarget,
                FollowState.TargetNotFound => Strings.Follow_TargetNotFound,
                _ => part.Unavailable == FollowUnavailable.Rotation ? Strings.Follow_Rotation : Strings.Follow_NoRoom
            };
            text += " " + string.Format(CultureInfo.CurrentCulture, Strings.Follow_PartProblem, GapText(clip, new FollowGap(part.From, part.To)), why);
            problem = true;
        }
        // Nobody picked and several people: the most prominent one is followed — the user may pick another.
        return resolution.Parts.Any(p => p.AutoChosen) ? (text + " " + Strings.Follow_AutoChosen, problem) : (text, problem);
    }

    /// <summary>«Участки слежения: 0:12–0:40, 1:05–1:30» in the clip's timeline time, or «на весь клип».</summary>
    private string RangeText(VideoClip clip, FollowSettings follow)
    {
        if (follow.IsWholeClip) return Strings.Follow_WholeClip;
        var parts = follow.Parts.Select(p => p.RangeIn(clip.SourceIn, clip.SourceOut)).Where(r => r is not null).Select(r => r!.Value)
            .Select(r => GapText(clip, new FollowGap(r.From, r.To))).ToList();
        return parts.Count == 0 ? Strings.Follow_OutsideRange
            : string.Format(CultureInfo.CurrentCulture, Strings.Follow_Part, string.Join(", ", parts));
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
