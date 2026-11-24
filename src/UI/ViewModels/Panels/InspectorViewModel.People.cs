using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
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

    /// <summary>D074 §5: the video clip of a selected «Боулдер» anchored to it (null for any other selection).</summary>
    private VideoClip? _anchor;

    /// <summary>The clip «Слежение и люди» works on: the selected video clip, or the clip a selected plate is anchored to.</summary>
    private VideoClip? PeopleClip => _clip as VideoClip ?? (_clip is TemplateClip ? _anchor : null);

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

    /// <summary>D061 amendment 1: where the climber stands in the frame on the part under the playhead (the only part
    /// otherwise), canvas pixels from the centre; <see cref="HasFollowOffset"/> — such a part exists.</summary>
    [ObservableProperty] private decimal? _followOffsetX;
    [ObservableProperty] private decimal? _followOffsetY;
    [ObservableProperty] private bool _hasFollowOffset;

    partial void OnFollowOffsetXChanged(decimal? value) => EditFollowOffset(value, (p, v) => p with { OffsetX = v });
    partial void OnFollowOffsetYChanged(decimal? value) => EditFollowOffset(value, (p, v) => p with { OffsetY = v });

    /// <summary>The part whose offset the fields show: the one under the playhead, else the only one; -1 for none.</summary>
    private int FollowOffsetPart(VideoClip clip)
    {
        if (clip.Follow is not { } follow) return -1;
        if (PlayheadSourceTime(clip) is { } at && follow.IndexAt(at) is var here and >= 0) return here;
        return follow.Parts.Length == 1 ? 0 : -1;
    }

    private void EditFollowOffset(decimal? value, Func<FollowPart, double, FollowPart> with)
    {
        if (_syncing || value is not { } v || PeopleClip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
        var index = FollowOffsetPart(clip);
        if (index < 0) return;
        // D063 §4: a part with framing keyframes — the keyframe on the playhead's frame (or a new one there).
        if (follow.Parts[index].Keyframes is not null)
        {
            var owner = new UI.Services.KeyframeOwner(clip.Id, index);
            var frame = KeyframeFrame(clip);
            var shown = KeyframeEditor.ValueAt(clip, owner, frame);
            var moved = with(follow.Parts[index] with { OffsetX = shown.X, OffsetY = shown.Y }, (double)v);
            Apply(KeyframeEditor.SetValue(clip, owner, frame, shown with { X = moved.OffsetX, Y = moved.OffsetY }));
            return;
        }
        var part = with(follow.Parts[index], (double)v);
        if (part == follow.Parts[index]) return;
        Apply(_edit.SetClipFollow(clip.Id, follow.WithPart(index, part)));
    }

    /// <summary>The offset fields from the model (also when the playhead moves to another part).</summary>
    private void RefreshFollowOffset()
    {
        var clip = PeopleClip;
        var index = clip is null ? -1 : FollowOffsetPart(clip);
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            HasFollowOffset = index >= 0;
            // D063 §4: the climber's place on the playhead's frame (the part's keyframes interpolated).
            var shown = index >= 0 ? KeyframeEditor.ValueAt(clip!, new UI.Services.KeyframeOwner(clip!.Id, index), KeyframeFrame(clip!)) : default;
            FollowOffsetX = index >= 0 ? (decimal)shown.X : null;
            FollowOffsetY = index >= 0 ? (decimal)shown.Y : null;
            var part = index >= 0 ? clip!.Follow!.Parts[index] : null;
            FollowDynamicScale = part?.DynamicScale == true;
            FollowScaleIsAuto = part?.ScaleShare is null;
            FollowScalePercent = part is { DynamicScale: true } && ShownShare(clip!, index) is { } share ? Math.Round((decimal)share * 100, 1) : null;
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    // --- D080: «Динамический масштаб» and «Масштаб, %» of the part the offset fields show --------------------------------

    /// <summary>D080: the part's dynamic scale is on.</summary>
    [ObservableProperty] private bool _followDynamicScale;

    /// <summary>D080 (Q1): the climber's height as % of the frame's height — the user's, else the automatic one (null while
    /// the climber is not analysed yet).</summary>
    [ObservableProperty] private decimal? _followScalePercent;

    /// <summary>D080: the shown share is the automatic one («авто»).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FollowScaleAutoCommand))]
    private bool _followScaleIsAuto = true;

    public decimal MinFollowScalePercent => (decimal)FollowZoom.MinShare * 100;
    public decimal MaxFollowScalePercent => (decimal)FollowZoom.MaxShare * 100;

    partial void OnFollowDynamicScaleChanged(bool value) => EditFollowPart(p => p with { DynamicScale = value });

    partial void OnFollowScalePercentChanged(decimal? value)
    {
        if (value is not { } v) return;
        var share = Math.Clamp((double)v / 100, FollowZoom.MinShare, FollowZoom.MaxShare);
        EditFollowPart(p => p with { DynamicScale = true, ScaleShare = Math.Round(share, 4) });
    }

    /// <summary>«Авто»: the automatic share again.</summary>
    [RelayCommand(CanExecute = nameof(CanResetFollowScale))]
    private void FollowScaleAuto() => EditFollowPart(p => p with { ScaleShare = null });

    private bool CanResetFollowScale() => !FollowScaleIsAuto;

    /// <summary>One change of the part the offset fields show (one undo step).</summary>
    private void EditFollowPart(Func<FollowPart, FollowPart> change)
    {
        if (_syncing || PeopleClip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
        var index = FollowOffsetPart(clip);
        if (index < 0) return;
        var part = change(follow.Parts[index]);
        if (part == follow.Parts[index]) return;
        Apply(_edit.SetClipFollow(clip.Id, follow.WithPart(index, part)));
    }

    /// <summary>D080 (the owner's answer to Q3): a new followed part has the dynamic scale on unless the clip was scaled by
    /// hand — then Follow keeps that scale and only centres the climber.</summary>
    private bool NewPartsDynamic(VideoClip clip) =>
        !FollowZoom.IsHandScaled(clip, _canvas, _findAsset?.Invoke(clip.MediaAssetId) is { } asset ? PlaybackSnapshotBuilder.DisplaySize(asset.Metadata) : null);

    /// <summary>The share shown for part <paramref name="index"/>: the user's, else the automatic one of the resolved part.</summary>
    private double? ShownShare(VideoClip clip, int index)
    {
        var part = clip.Follow!.Parts[index];
        if (part.ScaleShare is { } own) return own;
        if (!part.DynamicScale || _people is null || !_people.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var tracks) ||
            _findAsset?.Invoke(clip.MediaAssetId) is not { } asset || PlaybackSnapshotBuilder.DisplaySize(asset.Metadata) is not { } source)
            return null;
        // The playhead moves refresh the fields on every frame: the resolution is computed once per state of the clip.
        var key = (clip.Follow, tracks, VisualProperties.Of(clip), clip.Keyframes, clip.SourceIn, clip.SourceOut, _canvas, source);
        if (_shownShareCache is not { } cached || !Equals(cached.Key, key))
            _shownShareCache = cached = (key, FollowResolution.For(clip, clip.Follow, tracks, _canvas, source));
        return cached.Resolution.Parts.FirstOrDefault(p => p.Index == index)?.Zoom?.Share;
    }

    private ((FollowSettings, PersonTracks, VisualProperties?, TransformKeyframes?, MediaTime, MediaTime, FrameSize, FrameSize) Key, FollowResolution Resolution)? _shownShareCache;

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
        if (_syncing || PeopleClip is not VideoClip clip || RejectWhileLocked()) return;
        if (value == clip.Follow is not null) return;
        Apply(_edit.SetClipFollow(clip.Id, value ? FollowSettings.Whole(SmoothnessAt(FollowSmoothnessIndex), dynamicScale: NewPartsDynamic(clip)) : null,
            hideOutlinesBehindPeople: value ? true : null));
    }

    partial void OnFollowSmoothnessIndexChanged(int value)
    {
        if (_syncing || PeopleClip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
        Apply(_edit.SetClipFollow(clip.Id, follow with { Smoothness = SmoothnessAt(value) }));
    }

    partial void OnHidesOutlinesBehindPeopleChanged(bool value)
    {
        if (_syncing || PeopleClip is not VideoClip clip || RejectWhileLocked()) return;
        Apply(_edit.SetClipHidesOutlinesBehindPeople(clip.Id, value));
    }

    private void Apply(Core.Interfaces.TimelineEditResult result)
    {
        if (result.Success) return;
        _status.ReportWarning(result.Message ?? "");
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
        if (PeopleClip is not VideoClip clip || RejectWhileLocked()) return;
        if (PlayheadSourceTime(clip) is not { } at)
        {
            _status.ReportWarning(Strings.Follow_PlayheadOutside);
            return;
        }
        var from = at <= clip.SourceIn ? (MediaTime?)null : at;
        FollowSettings follow;
        var existing = clip.Follow;
        if (existing is null || existing.IsWholeClip)
            follow = FollowSettings.Of(new FollowPart(from, DynamicScale: NewPartsDynamic(clip)), existing?.Smoothness ?? SmoothnessAt(FollowSmoothnessIndex));
        else if (existing.IndexAt(at) is var inside and >= 0)
        {
            // D078 amendment 6 (the owner): on a followed part the press continues it — the same climber, its end opened to the
            // next part (or the clip's end) and recorded again from here: «Остановить слежение здесь» sets its new end.
            var part = existing.Parts[inside];
            var nextFrom = existing.Parts.Skip(inside + 1).FirstOrDefault()?.From;
            var continued = existing.WithPart(inside, part with { To = nextFrom ?? existing.OpenEnd(inside) })!;
            var extended = Equals(continued, existing) ? Core.Interfaces.TimelineEditResult.Unchanged() : _edit.SetClipFollow(clip.Id, continued);
            if (!extended.Success)
            {
                Apply(extended);
                return;
            }
            _followRecordingClipId = clip.Id;
            _followRecordingFrom = part.From;
            IsRecordingFollow = true;
            _status.Report(Strings.Follow_Continuing);
            FollowPlaybackRequested?.Invoke(this, true);
            return;
        }
        else
        {
            var next = existing.Parts.FirstOrDefault(p => p.From is { } f && f > at)?.From;
            // On the clip's first frame the part opens to the start — up to a part before it (beyond the clip's start).
            if (from is null) from = existing.Parts.LastOrDefault(p => p.To is { } t && t <= at)?.To;
            follow = existing.WithAdded(new FollowPart(from, next, DynamicScale: NewPartsDynamic(clip)));
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
        var clip = PeopleClip;
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
            _status.ReportWarning(Strings.Follow_PartTooShort);
            return;
        }
        if (part.To is { } limit && end >= limit) return;                 // it reached the next part: already ends there
        Apply(_edit.SetClipFollow(clip.Id, follow.WithPart(index, part with { To = end >= clip.SourceOut ? follow.OpenEnd(index) : end })));
    }

    /// <summary>Follow back to one part on the whole clip (the first part's person).</summary>
    [RelayCommand(CanExecute = nameof(HasFollowRange))]
    private void FollowWholeClip()
    {
        if (PeopleClip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked()) return;
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
        if (PeopleClip is not VideoClip { Follow: { } follow } clip || RejectWhileLocked() || PlayheadSourceTime(clip) is not { } at) return;
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
        if (PeopleClip is not VideoClip clip) return;
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
        var clip = PeopleClip;
        if (clip is null || clip.Id != clipId || RejectWhileLocked()) return;
        FollowSettings follow;
        if (clip.Follow is not { } existing)
            follow = FollowSettings.Whole(SmoothnessAt(FollowSmoothnessIndex), sourceTime, point, NewPartsDynamic(clip));
        else if (existing.IndexAt(sourceTime) is var index and >= 0)
            follow = existing.WithPart(index, existing.Parts[index] with { TargetTime = sourceTime, TargetPoint = point })!;
        else
        {
            _status.ReportWarning(Strings.Follow_PickOutsideParts);
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
        if (PeopleClip is VideoClip clip) _people?.Cancel(clip.MediaAssetId);
    }

    [RelayCommand]
    private void RetryPeopleAnalysis()
    {
        if (PeopleClip is VideoClip clip) _people?.Retry(clip.MediaAssetId);
    }

    /// <summary>Fills the section from the clip and the analysis (also after every timeline change and analysis event).</summary>
    private void RefreshPeople()
    {
        var clip = PeopleClip;
        HasPeopleSection = clip is not null && _people is not null;
        if (_followRecordingClipId is { } recording &&
            (clip?.Id != recording || clip.Follow is not { } follow || !follow.Parts.Any(p => p.From == _followRecordingFrom)))
        {
            _followRecordingClipId = null;     // another clip, Follow off, or the started part undone / changed: it is over
            IsRecordingFollow = false;
        }
        HasFollowRange = clip?.Follow is { IsWholeClip: false };
        HasFollowPartHere = clip?.Follow is { } here && PlayheadSourceTime(clip) is { } now && here.IndexAt(now) >= 0;
        RefreshFollowOffset();
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
