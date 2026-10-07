using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Bottom Timeline panel: a projection of the real <see cref="Sequence"/> plus
/// view state (zoom, playhead, selection, drag previews). It never mutates tracks
/// or clips itself — every edit goes through <see cref="ITimelineEditService"/>,
/// and the panel refreshes from <see cref="IProjectService.TimelineChanged"/>
/// (which also fires on Undo/Redo). Pointer gestures arrive from the view as
/// content-space X coordinates; all time math is done here via
/// <see cref="TimelineCoordinateMapper"/> and the exact frame grid.
/// </summary>
public sealed partial class TimelineViewModel : ViewModelBase
{
    public const double RulerHeight = 24;
    public const double SnapTolerancePixels = 8;
    public const double DragThresholdPixels = 3;
    private const double ZoomStep = 1.25;
    private static readonly MediaTime TrailingSpace = MediaTime.FromSeconds(30);

    private readonly IProjectService _projectService;
    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly ILogger<TimelineViewModel> _logger;
    private readonly EditingLock _editingLock;
    private readonly InOutRangeService _inOut;
    private readonly WaveformCoordinator? _waveforms;
    private readonly IDialogService? _dialogs;

    private readonly Dictionary<Guid, TimelineClipViewModel> _clipViewModels = new();
    private readonly Dictionary<Track, TimelineTrackViewModel> _trackViewModels = new();
    private readonly Dictionary<Guid, TimelineTransitionViewModel> _transitionViewModels = new();
    private Guid? _selectedTransitionId;

    /// <summary>Length of a dissolve added with the Dissolve command (the longest that fits when less fits, D025).</summary>
    public static readonly MediaTime DefaultDissolveDuration = MediaTime.FromSeconds(1);
    private readonly List<Guid> _selection = new(); // last = primary

    private Gesture? _gesture;
    private double _viewportWidth;
    private double _scrollOffsetX;

    public TimelineViewModel(
        IProjectService projectService,
        ITimelineEditService edit,
        StatusService status,
        ILogger<TimelineViewModel> logger,
        EditingLock? editingLock = null,
        WaveformCoordinator? waveforms = null,
        IDialogService? dialogs = null,
        InOutRangeService? inOut = null)
    {
        _inOut = inOut ?? new InOutRangeService(projectService);
        _inOut.RangeChanged += (_, _) => RefreshRange();
        _projectService = projectService;
        _edit = edit;
        _status = status;
        _logger = logger;
        _editingLock = editingLock ?? new EditingLock();
        _editingLock.PropertyChanged += (_, _) => OnEditingLockChanged();

        _projectService.TimelineChanged += (_, _) => Refresh();
        _projectService.ProjectChanged += (_, _) => OnProjectReplaced();
        _projectService.MediaAssetsChanged += (_, _) =>
        {
            RefreshClipNames();
            RefreshWaveforms(); // a relinked file's old waveform is gone (D026 §3) — not only replaced by a new one
        };
        _waveforms = waveforms;
        _dialogs = dialogs;
        if (waveforms is not null)
            waveforms.WaveformReady += (_, _) => RefreshWaveforms();

        _pixelsPerSecond = Sequence.ZoomPixelsPerSecond;
        _snappingEnabled = Sequence.SnappingEnabled;
        Refresh();
    }

    private Sequence Sequence => _projectService.Current.Timeline;
    public FrameRate FrameRate => _edit.FrameRate;
    public MediaTime Playhead => Sequence.PlayheadPosition;
    public MediaTime SequenceDuration => Sequence.Duration();

    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = new();
    public ObservableCollection<TimelineRulerTickViewModel> RulerTicks { get; } = new();

    /// <summary>The sequence's markers on the ruler (D027 §6), laid out at the current zoom.</summary>
    public ObservableCollection<TimelineMarkerViewModel> Markers { get; } = new();

    [ObservableProperty] private double _pixelsPerSecond;
    [ObservableProperty] private double _contentWidth;
    [ObservableProperty] private double _tracksHeight;
    [ObservableProperty] private double _playheadX;
    [ObservableProperty] private string _frameRateDisplay = "";
    [ObservableProperty] private bool _snappingEnabled;
    [ObservableProperty] private bool _isSnapIndicatorVisible;
    [ObservableProperty] private double _snapIndicatorX;

    /// <summary>No clip on any track: the view shows how to add one (D024 Step 9.8).</summary>
    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RippleDeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    private bool _hasSelection;

    /// <summary>A dissolve is selected (and no clip).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _hasTransitionSelection;

    /// <summary>The selected dissolve changed (null = none selected).</summary>
    public event EventHandler<TimelineTransitionSelection?>? TransitionSelectionChanged;

    public double PlayheadHeight => RulerHeight + TracksHeight;

    /// <summary>Playhead moved or the sequence duration may have changed.</summary>
    public event EventHandler? PlayheadChanged;

    /// <summary>The user moved the playhead (click, drag, frame step, Home/End). Raised only for
    /// user actions — never for <see cref="ShowPlaybackPosition"/> or a timeline refresh — so
    /// playback can seek without feeding its own position back into a seek.</summary>
    public event EventHandler<MediaTime>? SeekRequested;

    /// <summary>Primary selected clip changed (null = no timeline selection).</summary>
    public event EventHandler<TimelineClipSelection?>? SelectionChanged;

    /// <summary>The view should scroll horizontally to this content X (after layout).</summary>
    public event EventHandler<double>? ScrollRequested;

    partial void OnSnappingEnabledChanged(bool value) => Sequence.SnappingEnabled = value;

    partial void OnTracksHeightChanged(double value) => OnPropertyChanged(nameof(PlayheadHeight));

    // --- Projection ---------------------------------------------------------------

    /// <summary>Another project became current (New / Open / Recover). Selection and any drag
    /// belong to the old one; zoom and snapping are session state of the new sequence (D015),
    /// so they are read from it rather than carried over.</summary>
    private void OnProjectReplaced()
    {
        CancelGesture();
        _selection.Clear();
        // The new project's clips are new objects, and the same project opened again has the same ids: view models of
        // the previous project must not be reused for them.
        _clipViewModels.Clear();
        _transitionViewModels.Clear();
        _selectedTransitionId = null;
        Clipboard = null;   // copied clips refer to the previous project's media and tracks (D027 §5)
        PixelsPerSecond = Sequence.ZoomPixelsPerSecond;
        SnappingEnabled = Sequence.SnappingEnabled;
        Refresh();
    }

    private void Refresh()
    {
        // Video top-down as the playback snapshot composites it (PlaybackSnapshotBuilder: equal orders by their place in
        // the list, the later one on top), audio by order.
        var desiredTracks = Sequence.VideoTracks.Select((t, index) => (t, index))
            .OrderByDescending(x => x.t.Order).ThenByDescending(x => x.index).Select(x => x.t)
            .Concat(Sequence.AudioTracks.OrderBy(t => t.Order))
            .ToList();

        if (!desiredTracks.SequenceEqual(Tracks.Select(t => t.Track)))
        {
            Tracks.Clear();
            foreach (var track in desiredTracks)
            {
                if (!_trackViewModels.TryGetValue(track, out var vm))
                    _trackViewModels[track] = vm = new TimelineTrackViewModel(track);
                Tracks.Add(vm);
            }
            foreach (var stale in _trackViewModels.Keys.Except(desiredTracks).ToList())
                _trackViewModels.Remove(stale);
        }
        for (var i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].HasTrackAbove = i > 0 && Tracks[i - 1].Type == Tracks[i].Type;
            Tracks[i].HasTrackBelow = i < Tracks.Count - 1 && Tracks[i + 1].Type == Tracks[i].Type;
            Tracks[i].SyncState();
        }
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();

        var liveIds = new HashSet<Guid>();
        foreach (var trackVm in Tracks)
        {
            var desired = trackVm.Track.Clips.Select(GetClipViewModel).ToList();
            liveIds.UnionWith(desired.Select(c => c.Id));
            if (!desired.SequenceEqual(trackVm.Clips))
            {
                trackVm.Clips.Clear();
                foreach (var clipVm in desired) trackVm.Clips.Add(clipVm);
            }
        }

        foreach (var staleId in _clipViewModels.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _clipViewModels.Remove(staleId);

        var liveTransitions = new HashSet<Guid>();
        foreach (var trackVm in Tracks)
        {
            var desired = trackVm.Track.Transitions.Select(t => GetTransitionViewModel(t, trackVm.Track)).ToList();
            liveTransitions.UnionWith(desired.Select(t => t.Id));
            if (!desired.SequenceEqual(trackVm.Transitions))
            {
                trackVm.Transitions.Clear();
                foreach (var transitionVm in desired) trackVm.Transitions.Add(transitionVm);
            }
        }
        foreach (var staleId in _transitionViewModels.Keys.Where(id => !liveTransitions.Contains(id)).ToList())
            _transitionViewModels.Remove(staleId);
        if (_selectedTransitionId is { } selected && !liveTransitions.Contains(selected))
            _selectedTransitionId = null;
        IsEmpty = liveIds.Count == 0;
        RefreshClipNames(); // a text clip's label is its text, which any change (or undo) may alter
        _selection.RemoveAll(id => !liveIds.Contains(id));

        FrameRateDisplay = _projectService.Current.Settings.IsFrameRateLocked
            ? $"{FormatRate(FrameRate)} FPS"
            : $"{FormatRate(FrameRate)} FPS (provisional)";

        TracksHeight = Tracks.Count * TimelineTrackViewModel.Height;
        PixelsPerSecond = TimelineCoordinateMapper.ClampZoom(PixelsPerSecond, FrameRate);

        // The frame rate may have just changed (first video): keep the playhead on the grid.
        Sequence.PlayheadPosition = Playhead.SnapToFrame(FrameRate);

        Relayout();
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
        RaiseTransitionSelectionChanged();
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimelineTransitionViewModel GetTransitionViewModel(Transition transition, Track track)
    {
        if (!_transitionViewModels.TryGetValue(transition.Id, out var vm) || vm.Track != track)
            _transitionViewModels[transition.Id] = vm = new TimelineTransitionViewModel(transition, track);
        return vm;
    }

    private TimelineClipViewModel GetClipViewModel(Clip clip)
    {
        // Reused only for the same clip object: a view model shows and hands out the clip it wraps.
        if (!_clipViewModels.TryGetValue(clip.Id, out var vm) || vm.Clip != clip)
            _clipViewModels[clip.Id] = vm = new TimelineClipViewModel(clip, ClipName(clip));
        return vm;
    }

    private void RefreshClipNames()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.Name = ClipName(vm.Clip);
    }

    private string ClipName(Clip clip) => clip switch
    {
        MediaBackedClip m => FindAsset(m.MediaAssetId)?.FileName ?? "(missing media)",
        TextClip t => TextLabel(t.Text),
        _ => "Clip"
    };

    public const string EmptyTextLabel = "(empty text)";

    /// <summary>First line of a text clip's text (any line break), or <see cref="EmptyTextLabel"/>
    /// when the text is empty or only whitespace (such a clip draws nothing).</summary>
    public static string TextLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmptyTextLabel;
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    private MediaAsset? FindAsset(Guid id) => _projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == id);

    /// <summary>Whether a dissolve sits on the clip's start / end (D025 PO-8: the clip's fade there is not applied).</summary>
    private (bool AtStart, bool AtEnd) Dissolves(Clip clip)
    {
        var track = Sequence.VideoTracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null) return (false, false);
        return (track.Transitions.Any(t => t.RightClipId == clip.Id), track.Transitions.Any(t => t.LeftClipId == clip.Id));
    }

    /// <summary>Gives every clip the pixel width of its effective fade ramps (D025 §2, PO-8) at the current zoom.</summary>
    private void RefreshFades()
    {
        foreach (var vm in _clipViewModels.Values)
        {
            var clip = vm.Clip;
            if (clip.FadeIn == MediaTime.Zero && clip.FadeOut == MediaTime.Zero)
            {
                (vm.FadeInWidth, vm.FadeOutWidth) = (0, 0);
                continue;
            }
            var (atStart, atEnd) = Dissolves(clip);
            var (fadeIn, fadeOut) = FadeRule.EffectiveFrames(clip, FrameRate, atStart, atEnd);
            var startFrame = clip.TimelineStart.ToFrameFloor(FrameRate);
            var endFrame = clip.TimelineEnd.ToFrameFloor(FrameRate);
            vm.FadeInWidth = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(startFrame + fadeIn, FrameRate) - clip.TimelineStart, PixelsPerSecond);
            vm.FadeOutWidth = TimelineCoordinateMapper.TimeToX(clip.TimelineEnd - MediaTime.FromFrame(endFrame - fadeOut, FrameRate), PixelsPerSecond);
        }
    }

    /// <summary>Recomputes every pixel position from the model at the current zoom.</summary>
    private void Relayout()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.Layout(PixelsPerSecond);
        RefreshWaveforms();
        RefreshFades();
        foreach (var vm in _transitionViewModels.Values)
            vm.Layout(PixelsPerSecond, FrameRate);

        var contentEnd = TimelineCoordinateMapper.TimeToX(SequenceDuration + TrailingSpace, PixelsPerSecond);
        ContentWidth = Math.Max(contentEnd, _viewportWidth);
        PlayheadX = TimelineCoordinateMapper.TimeToX(Playhead, PixelsPerSecond);
        RebuildRuler();
        RebuildMarkers();
        RefreshRange();
    }

    // --- In / Out range (D030 §8): session state, never a project change ----------------------------------------------

    /// <summary>The session's In / Out range (shared with the Preview's loop and the export).</summary>
    public InOutRangeService InOut => _inOut;

    [ObservableProperty] private bool _hasIn;
    [ObservableProperty] private double _inX;
    [ObservableProperty] private bool _hasOut;
    [ObservableProperty] private double _outX;

    /// <summary>The range bar: <c>[In, Out)</c> within the sequence (a missing point: the sequence's start / end).</summary>
    [ObservableProperty] private bool _hasRange;
    [ObservableProperty] private double _rangeLeft;
    [ObservableProperty] private double _rangeWidth;

    /// <summary>The playhead's frame lies in the range (the bar is drawn brighter).</summary>
    [ObservableProperty] private bool _isPlayheadInRange;

    /// <summary>Where the ✕ that clears the range sits: right of the bar, or right of the only point when the range has
    /// nothing of the sequence in it.</summary>
    [ObservableProperty] private double _rangeRight;
    [ObservableProperty] private bool _canClearRange;

    private void RefreshRange()
    {
        var range = _inOut.Range;
        HasIn = range.In is not null;
        InX = range.In is { } i ? TimelineCoordinateMapper.TimeToX(i, PixelsPerSecond) : 0;
        HasOut = range.Out is not null;
        OutX = range.Out is { } o ? TimelineCoordinateMapper.TimeToX(o, PixelsPerSecond) : 0;

        if (_inOut.Frames is { } frames)
        {
            var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frames.First, FrameRate), PixelsPerSecond);
            HasRange = true;
            RangeLeft = left;
            RangeWidth = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frames.End, FrameRate), PixelsPerSecond) - left;
            var at = Playhead.ToFrameFloor(FrameRate);
            IsPlayheadInRange = at >= frames.First && at < frames.End;
        }
        else
        {
            HasRange = false;
            IsPlayheadInRange = false;
        }
        CanClearRange = range.IsSet;
        RangeRight = HasRange ? RangeLeft + RangeWidth : Math.Max(InX, OutX);
    }

    /// <summary>I: In at the playhead's frame. Session state: the project stays clean, no undo step (D030 §8).</summary>
    [RelayCommand]
    private void SetIn()
    {
        _inOut.SetIn(Playhead);
        _status.Report($"In set at {TimeFormat.ToTimecode(_inOut.Range.In!.Value, FrameRate)}.");
    }

    /// <summary>O: Out after the playhead's frame — the frame at the playhead is the range's last one (Q7).</summary>
    [RelayCommand]
    private void SetOut()
    {
        _inOut.SetOut(Playhead);
        _status.Report($"Out set after {TimeFormat.ToTimecode(Playhead.SnapToFrame(FrameRate), FrameRate)} (that frame is included).");
    }

    /// <summary>The ✕ on the range bar (Q11).</summary>
    [RelayCommand]
    private void ClearInOut()
    {
        _inOut.Clear();
        _status.Report("In / Out cleared.");
    }

    private void RebuildMarkers()
    {
        Markers.Clear();
        foreach (var marker in Sequence.Markers.OrderBy(m => m.Position.Ticks))
        {
            Markers.Add(new TimelineMarkerViewModel
            {
                Id = marker.Id,
                Left = TimelineCoordinateMapper.TimeToX(marker.Position, PixelsPerSecond),
                Color = string.IsNullOrWhiteSpace(marker.ColorHex) ? "#4FC3F7" : marker.ColorHex
            });
        }
    }

    /// <summary>Gives every clip what its waveform shows now (D024 Step 9.5, PO-W1 / PO-W2 / PO-W5): audio clips and
    /// video clips whose media has a waveform — made, or cached for offline media (<see cref="WaveformCoordinator"/>
    /// never makes one for silent video) —, with the clip's timing, speed and volume; dimmed when the clip or its track is
    /// muted. Unchanged values are not re-raised (the records compare equal), so the view only redraws what changed.</summary>
    private void RefreshWaveforms()
    {
        foreach (var track in Tracks)
        {
            foreach (var vm in track.Clips)
                vm.Waveform = WaveformOf(vm.Clip, track.Track);
        }
    }

    private ClipWaveform? WaveformOf(Clip clip, Track track)
    {
        if (_waveforms is null) return null;
        var (media, volume, muted, lowerHalf) = clip switch
        {
            AudioClip a => ((MediaBackedClip)a, a.Volume, a.IsMuted, false),
            VideoClip v => ((MediaBackedClip)v, v.Volume, v.IsMuted, true),
            _ => (null, 0.0, false, false)
        };
        if (media is null || _waveforms.Get(media.MediaAssetId) is not { } data) return null;
        return new ClipWaveform(data, media.TimelineStart, media.TimelineEnd, media.SourceIn, media.Speed, volume,
            muted || track.IsMuted, lowerHalf, PixelsPerSecond);
    }

    /// <summary>Called by the view whenever it scrolls or resizes.</summary>
    public void SetViewport(double scrollOffsetX, double viewportWidth)
    {
        var widthChanged = Math.Abs(viewportWidth - _viewportWidth) > 0.5;
        var scrolledFar = Math.Abs(scrollOffsetX - _scrollOffsetX) > viewportWidth / 2;
        _scrollOffsetX = scrollOffsetX;
        _viewportWidth = viewportWidth;

        if (widthChanged)
            ContentWidth = Math.Max(TimelineCoordinateMapper.TimeToX(SequenceDuration + TrailingSpace, PixelsPerSecond), _viewportWidth);
        if (widthChanged || scrolledFar)
            RebuildRuler();
    }

    /// <summary>Ticks only for the visible range (± one viewport), so very long or very
    /// zoomed-in timelines don't create thousands of ruler elements.</summary>
    private void RebuildRuler()
    {
        RulerTicks.Clear();
        var interval = TimelineCoordinateMapper.RulerIntervalTicks(PixelsPerSecond);
        var viewport = _viewportWidth > 0 ? _viewportWidth : 2000;
        var fromTime = TimelineCoordinateMapper.XToTime(Math.Max(0, _scrollOffsetX - viewport), PixelsPerSecond);
        var toTime = TimelineCoordinateMapper.XToTime(Math.Min(ContentWidth, _scrollOffsetX + 2 * viewport), PixelsPerSecond);

        for (var i = fromTime.Ticks / interval; i * interval <= toTime.Ticks; i++)
        {
            var time = new MediaTime(i * interval);
            RulerTicks.Add(new TimelineRulerTickViewModel
            {
                Label = RulerLabel(time, interval),
                Left = TimelineCoordinateMapper.TimeToX(time, PixelsPerSecond)
            });
        }
    }

    private static string RulerLabel(MediaTime time, long intervalTicks)
    {
        if (intervalTicks >= TimeSpan.TicksPerSecond)
            return TimeFormat.ToShortString(time);
        var span = time.ToTimeSpan();
        return $"{(int)span.TotalMinutes}:{span.Seconds:D2}.{span.Milliseconds / 10:D2}";
    }

    private static string FormatRate(FrameRate rate) => RateFormat.Number(rate);

    // --- Playhead -----------------------------------------------------------------

    /// <summary>User action: moves the playhead to the nearest frame boundary (never before
    /// zero) and requests a playback seek there. View state only: not undoable, doesn't mark
    /// the project dirty.</summary>
    public void SetPlayhead(MediaTime time)
    {
        var snapped = (time < MediaTime.Zero ? MediaTime.Zero : time).SnapToFrame(FrameRate);
        MovePlayhead(snapped);
        SeekRequested?.Invoke(this, snapped);
    }

    /// <summary>Playback moved: shows <paramref name="frameStart"/> (a frame boundary) without
    /// requesting a seek.</summary>
    public void ShowPlaybackPosition(MediaTime frameStart)
    {
        if (frameStart == Playhead) return;
        MovePlayhead(frameStart);
    }

    private void MovePlayhead(MediaTime position)
    {
        Sequence.PlayheadPosition = position;
        PlayheadX = TimelineCoordinateMapper.TimeToX(position, PixelsPerSecond);
        RefreshRange();
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlayheadFromX(double contentX) =>
        SetPlayhead(TimelineCoordinateMapper.XToFrameTime(contentX, PixelsPerSecond, FrameRate));

    public void StepFrames(long frames)
    {
        var frame = Math.Max(0, Playhead.ToNearestFrame(FrameRate) + frames);
        SetPlayhead(MediaTime.FromFrame(frame, FrameRate));
    }

    [RelayCommand] private void StepForward() => StepFrames(1);
    [RelayCommand] private void StepBackward() => StepFrames(-1);
    [RelayCommand] private void StepForwardSecond() => StepFrames(TimeFormat.NominalFps(FrameRate));
    [RelayCommand] private void StepBackwardSecond() => StepFrames(-TimeFormat.NominalFps(FrameRate));
    [RelayCommand] public void GoToStart() => SetPlayhead(MediaTime.Zero);
    [RelayCommand] private void GoToEnd() => SetPlayhead(SequenceDuration);

    // --- Zoom ---------------------------------------------------------------------

    [RelayCommand] private void ZoomIn() => ZoomAroundPlayhead(ZoomStep);
    [RelayCommand] private void ZoomOut() => ZoomAroundPlayhead(1 / ZoomStep);

    [RelayCommand]
    private void ZoomToFit()
    {
        var duration = Math.Max(SequenceDuration.TotalSeconds, 10);
        var width = _viewportWidth > 40 ? _viewportWidth - 20 : 1000;
        ApplyZoom(width / duration);
        ScrollRequested?.Invoke(this, 0);
    }

    /// <summary>Ctrl+wheel: keeps the time under the mouse pointer fixed on screen.</summary>
    public void ZoomAtPointer(double factor, double contentX)
    {
        var anchorTime = TimelineCoordinateMapper.XToTime(Math.Max(0, contentX), PixelsPerSecond);
        var anchorViewportX = contentX - _scrollOffsetX;
        ApplyZoom(PixelsPerSecond * factor);
        ScrollRequested?.Invoke(this, TimelineCoordinateMapper.ScrollOffsetForAnchor(anchorTime, anchorViewportX, PixelsPerSecond));
    }

    private void ZoomAroundPlayhead(double factor)
    {
        var playheadViewportX = PlayheadX - _scrollOffsetX;
        if (playheadViewportX < 0 || playheadViewportX > _viewportWidth)
            playheadViewportX = _viewportWidth / 2; // off-screen playhead: bring it to the centre
        ApplyZoom(PixelsPerSecond * factor);
        ScrollRequested?.Invoke(this, TimelineCoordinateMapper.ScrollOffsetForAnchor(Playhead, playheadViewportX, PixelsPerSecond));
    }

    private void ApplyZoom(double pixelsPerSecond)
    {
        PixelsPerSecond = TimelineCoordinateMapper.ClampZoom(pixelsPerSecond, FrameRate);
        Sequence.ZoomPixelsPerSecond = PixelsPerSecond;
        Relayout();
    }

    // --- Editing commands ---------------------------------------------------------
    // Disabled while an export runs (EditingLock): they would change the timeline. Viewing (playhead, zoom,
    // selection, snapping) stays available; pointer edits and drops are ignored.

    /// <summary>False while editing is locked (an export runs).</summary>
    public bool IsEditingAllowed => !_editingLock.IsLocked;

    private bool CanEdit() => !_editingLock.IsLocked;

    private bool CanDeleteSelected() => CanEdit() && (HasSelection || HasTransitionSelection);

    /// <summary>The Dissolve command needs two selected clips; whether they meet on a video track and how long a
    /// dissolve fits there is the edit service's call (D025 §3–§5).</summary>
    private bool CanAddDissolve() => CanEdit() && _selection.Count == 2;

    private void OnEditingLockChanged()
    {
        if (_editingLock.IsLocked) CancelGesture();
        OnPropertyChanged(nameof(IsEditingAllowed));
        SplitAtPlayheadCommand.NotifyCanExecuteChanged();
        TrimStartToPlayheadCommand.NotifyCanExecuteChanged();
        TrimEndToPlayheadCommand.NotifyCanExecuteChanged();
        RippleTrimStartToPlayheadCommand.NotifyCanExecuteChanged();
        RippleTrimEndToPlayheadCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RippleDeleteCommand.NotifyCanExecuteChanged();
        CloseGapCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        AddDissolveCommand.NotifyCanExecuteChanged();
        AddVideoTrackCommand.NotifyCanExecuteChanged();
        AddAudioTrackCommand.NotifyCanExecuteChanged();
        AddTextCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();
        ToggleTrackMuteCommand.NotifyCanExecuteChanged();
        ToggleTrackHiddenCommand.NotifyCanExecuteChanged();
        ToggleTrackLockCommand.NotifyCanExecuteChanged();
        AddMarkerCommand.NotifyCanExecuteChanged();
        RemoveMarkerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SplitAtPlayhead() =>
        Report(_edit.Split(Playhead, _selection.Count > 0 ? _selection.ToList() : null));

    // --- Trim to the playhead (D030 §5): Q / W plain, Shift+Q / Shift+W ripple ----------------------------
    // Only the selected clips with the playhead inside them (Q1); every rule is the edit service's. After a ripple trim
    // of the start the playhead goes to the clip's start (Q5) — session state, not part of the undo step.

    [RelayCommand(CanExecute = nameof(CanEdit))] private void TrimStartToPlayhead() => TrimToPlayhead(ClipEdge.Start, ripple: false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void TrimEndToPlayhead() => TrimToPlayhead(ClipEdge.End, ripple: false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void RippleTrimStartToPlayhead() => TrimToPlayhead(ClipEdge.Start, ripple: true);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void RippleTrimEndToPlayhead() => TrimToPlayhead(ClipEdge.End, ripple: true);

    private void TrimToPlayhead(ClipEdge edge, bool ripple)
    {
        var result = _edit.TrimToPlayhead(_selection.ToList(), edge, Playhead, ripple);
        if (result is { Success: true, Playhead: { } playhead }) SetPlayhead(playhead);
        var count = result.ClipIds.Count;
        Report(result, successMessage: $"{(ripple ? "Ripple trimmed" : "Trimmed")} the {(edge == ClipEdge.Start ? "start" : "end")} of " +
            $"{(count == 1 ? "the clip" : $"{count} clips")} to the playhead");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (_selectedTransitionId is { } transitionId && _selection.Count == 0)
        {
            Report(_edit.RemoveTransition(transitionId), successMessage: "Dissolve removed");
            return;
        }

        var result = _edit.DeleteClips(_selection.ToList());
        if (result.Success) ClearSelection();
        Report(result);
    }

    /// <summary>
    /// "Dissolve": a cross dissolve on the cut between the two selected clips (D025) — <see cref="DefaultDissolveDuration"/>,
    /// or the longest the edit service says fits there when that is shorter. Every check (the clips meet on one unlocked
    /// video track, the zone, the handles) is the service's; its message is shown when it refuses. The new dissolve is
    /// selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddDissolve))]
    private void AddDissolve()
    {
        var clips = _selection.Select(id => _clipViewModels.TryGetValue(id, out var vm) ? vm.Clip : null).OfType<Clip>()
            .OrderBy(c => c.TimelineStart).ToList();
        if (clips.Count != 2) return;
        var (left, right) = (clips[0], clips[1]);

        if (_edit.MaxTransitionFrames(left.Id, right.Id) is not { } max)
        {
            _status.Report("Select two clips that meet on a video track (the first ends where the second starts) to add a dissolve.");
            return;
        }

        var wanted = TransitionRules.Frames(DefaultDissolveDuration, FrameRate);
        var frames = max >= TransitionRules.MinFrames ? Math.Min(wanted, max) : wanted;
        var result = _edit.AddTransition(left.Id, right.Id, MediaTime.FromFrame(frames, FrameRate));
        if (result.Success && result.TransitionId is { } id)
            SelectTransition(id);
        Report(result, successMessage: frames < wanted
            ? $"Dissolve added: {frames} frames, the longest that fits here."
            : $"Dissolve added: {frames} frames.");
    }

    // --- Ripple delete and close gap (D027 §2) -------------------------------------
    // The rules (which clips move, dissolves, locks, validation) are the edit service's; its message is shown when it
    // refuses.

    private bool CanRippleDelete() => CanEdit() && HasSelection;

    /// <summary>Close Gap works on the gap right before the one selected clip, on its track.</summary>
    private bool CanCloseGap() => CanEdit() && _selection.Count == 1;

    /// <summary>"Ripple Delete": the selected clips go and the later clips of their tracks close up (D027 §2).</summary>
    [RelayCommand(CanExecute = nameof(CanRippleDelete))]
    private void RippleDelete()
    {
        var result = _edit.RippleDeleteClips(_selection.ToList());
        if (result.Success) ClearSelection();
        Report(result, successMessage: result.ClipIds.Count == 1 ? "Clip ripple deleted" : $"{result.ClipIds.Count} clips ripple deleted");
    }

    /// <summary>"Close Gap": the empty span before the selected clip goes; that clip and the later ones of its track move
    /// left (D027 §2). The selection stays.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseGap))]
    private void CloseGap()
    {
        if (_selection.Count != 1) return;
        Report(_edit.CloseGapBefore(_selection[0]), successMessage: "Gap closed");
    }

    // --- Copy / paste / duplicate (D027 §5) ------------------------------------------
    // The clipboard is this panel's session state for the current project (cleared on New / Open / Recover): what the
    // edit service copied. Placement, tracks, media and overlap rules are the service's; its message is shown when it
    // refuses. Copy changes nothing, so it stays available during an export.

    /// <summary>The clips copied last (null = nothing copied in this project).</summary>
    public TimelineClipboard? Clipboard
    {
        get => _clipboard;
        private set
        {
            if (SetProperty(ref _clipboard, value))
                PasteCommand.NotifyCanExecuteChanged();
        }
    }
    private TimelineClipboard? _clipboard;

    private bool CanCopy() => HasSelection;
    private bool CanPaste() => CanEdit() && Clipboard is not null;
    private bool CanDuplicate() => CanEdit() && HasSelection;

    /// <summary>"Copy" (Ctrl+C): the selected clips.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (_edit.CopyClips(_selection.ToList()) is not { } copied)
        {
            _status.Report("Nothing was copied: a selected clip no longer exists.");
            return;
        }
        Clipboard = copied;
        _status.Report(copied.Count == 1 ? "1 clip copied" : $"{copied.Count} clips copied");
    }

    /// <summary>"Paste" (Ctrl+V): the copied clips at the playhead, on their tracks; the pasted clips are selected.</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (Clipboard is not { } clipboard) return;
        var result = _edit.PasteClips(clipboard, Playhead);
        SelectAdded(result);
        Report(result, successMessage: result.ClipIds.Count == 1 ? "1 clip pasted" : $"{result.ClipIds.Count} clips pasted");
    }

    /// <summary>"Duplicate" (Ctrl+D): copies of the selected clips right after them; the copies are selected.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        var result = _edit.DuplicateClips(_selection.ToList());
        SelectAdded(result);
        Report(result, successMessage: result.ClipIds.Count == 1 ? "1 clip duplicated" : $"{result.ClipIds.Count} clips duplicated");
    }

    // --- Markers (D027 §6) --------------------------------------------------------------
    // The corner buttons above the track headers. Adding and removing change the project (undoable, not during an
    // export); going to a marker only moves the playhead. Which marker is where is the edit service's.

    /// <summary>"Add Marker": a marker at the playhead.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddMarker() => Report(_edit.AddMarker(Playhead), successMessage: $"Marker added at {TimeFormat.ToTimecode(Playhead, FrameRate)}");

    /// <summary>"Remove Marker": the marker at the playhead (go to it first).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveMarker() => Report(_edit.RemoveMarkerAt(Playhead), successMessage: "Marker removed");

    [RelayCommand]
    private void PreviousMarker()
    {
        if (_edit.PreviousMarker(Playhead) is { } at) SetPlayhead(at);
        else _status.Report("There is no marker before the playhead.");
    }

    [RelayCommand]
    private void NextMarker()
    {
        if (_edit.NextMarker(Playhead) is { } at) SetPlayhead(at);
        else _status.Report("There is no marker after the playhead.");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))] private void AddVideoTrack() => Report(_edit.AddTrack(TrackType.Video));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void AddAudioTrack() => Report(_edit.AddTrack(TrackType.Audio));

    // --- Tracks (D027 §3) -------------------------------------------------------------
    // The header's ▲ / ▼ / ✕. Every rule (locks, the last track, the neighbour) is the edit service's; its message is
    // shown when it refuses.

    private bool CanMoveTrackUp(TimelineTrackViewModel? track) => CanEdit() && track is { HasTrackAbove: true };
    private bool CanMoveTrackDown(TimelineTrackViewModel? track) => CanEdit() && track is { HasTrackBelow: true };
    private bool CanDeleteTrack(TimelineTrackViewModel? track) => CanEdit() && track is not null;

    /// <summary>One place up in the timeline: video tracks are listed top layer first (a higher order), audio tracks by
    /// ascending order.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveTrackUp))]
    private void MoveTrackUp(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        Report(_edit.MoveTrack(track.Track.Id, track.Type == TrackType.Video ? 1 : -1), successMessage: $"Track {track.Track.Name} moved up");
    }

    [RelayCommand(CanExecute = nameof(CanMoveTrackDown))]
    private void MoveTrackDown(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        Report(_edit.MoveTrack(track.Track.Id, track.Type == TrackType.Video ? -1 : 1), successMessage: $"Track {track.Track.Name} moved down");
    }

    // --- Track state (D030 §4) ---------------------------------------------------------
    // The header's mute / hide / lock toggles: each click one undoable command; mute and hide also on a locked track
    // (D030 Q13); all disabled while an export runs.

    private bool CanToggleTrack(TimelineTrackViewModel? track) => CanEdit() && track is not null;
    private bool CanToggleTrackHidden(TimelineTrackViewModel? track) => CanToggleTrack(track) && track!.CanHide;

    [RelayCommand(CanExecute = nameof(CanToggleTrack))]
    private void ToggleTrackMute(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var muted = !track.Track.IsMuted;
        Report(_edit.SetTrackMuted(track.Track.Id, muted), successMessage: $"Track {track.Track.Name} {(muted ? "muted" : "unmuted")}");
    }

    [RelayCommand(CanExecute = nameof(CanToggleTrackHidden))]
    private void ToggleTrackHidden(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var hidden = !track.Track.IsHidden;
        Report(_edit.SetTrackHidden(track.Track.Id, hidden), successMessage: $"Track {track.Track.Name} {(hidden ? "hidden" : "shown")}");
    }

    [RelayCommand(CanExecute = nameof(CanToggleTrack))]
    private void ToggleTrackLock(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var locked = !track.Track.IsLocked;
        Report(_edit.SetTrackLocked(track.Track.Id, locked), successMessage: $"Track {track.Track.Name} {(locked ? "locked" : "unlocked")}");
    }

    /// <summary>Deletes a track; one with clips only after the user confirms (D027 §3). Without a dialog service a track
    /// with clips is not deleted.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteTrack))]
    private async Task DeleteTrack(TimelineTrackViewModel? track)
    {
        if (track is null || !CanEdit()) return;
        var name = track.Track.Name;
        var clipCount = track.Track.Clips.Count;
        if (_edit.GetDeleteTrackBlockReason(track.Track.Id) is { } blocked)
        {
            _status.Report(blocked);
            return;
        }
        if (clipCount > 0)
        {
            if (_dialogs is null) return;
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = "Delete Track",
                Message = clipCount == 1
                    ? $"Track {name} has 1 clip. Deleting the track deletes the clip too.\n\nYou can undo this with Undo."
                    : $"Track {name} has {clipCount} clips. Deleting the track deletes them too, and any dissolves between them.\n\nYou can undo this with Undo.",
                Buttons = new[] { "Delete Track", "Cancel" }
            });
            // The answer may come after an export started or the project was replaced; the service re-checks the rest.
            if (choice != 0 || !CanEdit()) return;
        }

        Report(_edit.DeleteTrack(track.Track.Id), successMessage: $"Track {name} deleted");
    }

    [RelayCommand] private void ToggleSnapping() => SnappingEnabled = !SnappingEnabled;

    /// <summary>"+ Text": a text clip at the playhead on the topmost video track, selected.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddText()
    {
        var result = _edit.AddTextClip(Playhead);
        SelectAdded(result);
        Report(result, successMessage: "Text added");
    }

    /// <summary>"Add to Timeline" from the Media Browser: end of V1 / A1.</summary>
    public void AddMedia(MediaAsset asset) => AddMediaAt(asset.Id, null, null);

    /// <summary>Drag-and-drop from the Media Browser onto a track position.</summary>
    public void DropMedia(Guid assetId, TimelineTrackViewModel? track, double contentX)
    {
        ClearDropTargets();
        AddMediaAt(assetId, track?.Track.Id, TimelineCoordinateMapper.XToFrameTime(contentX, PixelsPerSecond, FrameRate));
    }

    private void AddMediaAt(Guid assetId, Guid? trackId, MediaTime? start)
    {
        if (!CanEdit()) return;
        var result = _edit.AddClip(assetId, trackId, start);
        SelectAdded(result);
        Report(result, successMessage: "Added to timeline");
    }

    private void SelectAdded(TimelineEditResult result)
    {
        if (!result.Success || result.ClipIds.Count == 0) return;
        DropTransitionSelection();
        _selection.Clear();
        _selection.AddRange(result.ClipIds);
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    public void ShowDropTarget(TimelineTrackViewModel? track)
    {
        foreach (var t in Tracks) t.IsDropTarget = t == track;
    }

    public void ClearDropTargets() => ShowDropTarget(null);

    private void Report(TimelineEditResult result, string? successMessage = null)
    {
        if (!result.Success)
            _status.Report(result.Message ?? "The timeline edit was not possible.");
        else if (result.Message is not null)
            _status.Report(result.Message);
        else if (successMessage is not null && !result.NoChange)
            _status.Report(successMessage);
    }

    // --- Selection ----------------------------------------------------------------

    /// <summary>Pointer pressed on a clip. Ctrl toggles it in the selection; a plain
    /// press on an unselected clip selects only that clip. Returns false when the press
    /// must not start a drag (Ctrl-toggle).</summary>
    public bool OnClipPressed(TimelineClipViewModel clip, bool toggle)
    {
        DropTransitionSelection();
        if (toggle)
        {
            if (!_selection.Remove(clip.Id)) _selection.Add(clip.Id);
            UpdateSelectionVisuals();
            RaiseSelectionChanged();
            return false;
        }

        if (!_selection.Contains(clip.Id))
            SelectOnly(clip.Id);
        return true;
    }

    /// <summary>Pointer released on a clip without dragging: collapse a multi-selection to it.</summary>
    public void OnClipClicked(TimelineClipViewModel clip, bool toggle)
    {
        if (!toggle && (_selection.Count != 1 || _selection[0] != clip.Id))
            SelectOnly(clip.Id);
    }

    /// <summary>Pointer pressed on a dissolve's zone: it becomes the selection (no clip stays selected).</summary>
    public void OnTransitionPressed(TimelineTransitionViewModel transition) => SelectTransition(transition.Id);

    /// <summary>The dissolve zone of <paramref name="track"/> at <paramref name="contentX"/>, if any. The view asks this
    /// only when no trim handle was hit: the clips' handles take precedence over a zone drawn over them.</summary>
    public TimelineTransitionViewModel? TransitionAt(TimelineTrackViewModel? track, double contentX) =>
        track?.Transitions.FirstOrDefault(t => t.Contains(contentX));

    private void SelectTransition(Guid id)
    {
        if (_selection.Count > 0)
        {
            _selection.Clear();
            UpdateSelectionVisuals();
            RaiseSelectionChanged();
        }
        _selectedTransitionId = id;
        UpdateSelectionVisuals();
        RaiseTransitionSelectionChanged();
    }

    /// <summary>Drops a dissolve selection (a clip or nothing is being selected instead).</summary>
    private void DropTransitionSelection()
    {
        if (_selectedTransitionId is null) return;
        _selectedTransitionId = null;
        UpdateSelectionVisuals();
        RaiseTransitionSelectionChanged();
    }

    private void RaiseTransitionSelectionChanged()
    {
        if (_selectedTransitionId is { } id && _transitionViewModels.TryGetValue(id, out var vm))
        {
            var t = vm.Transition;
            var left = _clipViewModels.TryGetValue(t.LeftClipId, out var a) ? a.Name : "";
            var right = _clipViewModels.TryGetValue(t.RightClipId, out var b) ? b.Name : "";
            TransitionSelectionChanged?.Invoke(this, new TimelineTransitionSelection(t, vm.Track, left, right, FrameRate,
                _edit.MaxTransitionFrames(t.LeftClipId, t.RightClipId)));
        }
        else
        {
            TransitionSelectionChanged?.Invoke(this, null);
        }
    }

    public void ClearSelection()
    {
        DropTransitionSelection();
        if (_selection.Count == 0) return;
        _selection.Clear();
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    /// <summary>Clears the selection without notifying (used when another panel's
    /// selection takes over the Inspector).</summary>
    public void ClearSelectionSilently()
    {
        _selection.Clear();
        _selectedTransitionId = null;
        UpdateSelectionVisuals();
    }

    private void SelectOnly(Guid id)
    {
        DropTransitionSelection();
        _selection.Clear();
        _selection.Add(id);
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.IsSelected = _selection.Contains(vm.Id);
        foreach (var vm in _transitionViewModels.Values)
            vm.IsSelected = vm.Id == _selectedTransitionId;
        HasSelection = _selection.Count > 0;
        HasTransitionSelection = _selectedTransitionId is not null;
        // Dissolve and Close Gap depend on how many clips are selected, not only on whether any is: 1 → 2 keeps
        // HasSelection true.
        AddDissolveCommand.NotifyCanExecuteChanged();
        CloseGapCommand.NotifyCanExecuteChanged();
    }

    private void RaiseSelectionChanged()
    {
        if (_selection.Count > 0 && _clipViewModels.TryGetValue(_selection[^1], out var primary))
        {
            var asset = primary.Clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;
            var (dissolveAtStart, dissolveAtEnd) = Dissolves(primary.Clip);
            SelectionChanged?.Invoke(this, new TimelineClipSelection(primary.Clip, primary.Name, asset, FrameRate, dissolveAtStart, dissolveAtEnd));
        }
        else
        {
            SelectionChanged?.Invoke(this, null);
        }
    }

    // --- Gestures (move / trim) ---------------------------------------------------

    private sealed class Gesture
    {
        public required ClipEdge? Edge { get; init; }           // null = move
        public required double StartX { get; init; }
        public required List<TimelineClipViewModel> Clips { get; init; }
        public required TimelineTrackViewModel? SourceTrack { get; init; } // single-track selection only
        public bool Active { get; set; }
        public long FrameDelta { get; set; }
        public TimelineTrackViewModel? TargetTrack { get; set; }
        public MediaTime TrimTime { get; set; }

        /// <summary>A ripple trim (D030 §6): Shift was held when the edge was pressed; fixed for the whole gesture.</summary>
        public bool Ripple { get; init; }

        /// <summary>A slip (D030 §7): Alt was held when the clip's body was pressed; fixed for the whole gesture.</summary>
        public bool Slip { get; init; }

        /// <summary>The slip's frames so far (positive: later source content).</summary>
        public long SlipFrames { get; set; }
    }

    public void BeginMove(double contentX)
    {
        if (!CanEdit()) return;
        var clips = _selection.Where(_clipViewModels.ContainsKey).Select(id => _clipViewModels[id]).ToList();
        if (clips.Count == 0) return;
        var tracks = clips.Select(TrackOf).Distinct().ToList();
        _gesture = new Gesture { Edge = null, StartX = contentX, Clips = clips, SourceTrack = tracks.Count == 1 ? tracks[0] : null };
    }

    /// <summary>Pointer pressed on a clip's edge: a trim — the ordinary one, or with <paramref name="ripple"/> (Shift held at
    /// the press, D030 §6) the ripple trim, for the whole gesture.</summary>
    public void BeginTrim(TimelineClipViewModel clip, ClipEdge edge, double contentX, bool ripple = false)
    {
        if (!CanEdit()) return;
        SelectOnly(clip.Id);
        _gesture = new Gesture
        {
            Edge = edge, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(clip),
            TrimTime = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd,
            Ripple = ripple
        };
    }

    /// <summary>Pointer pressed on a clip's body with Alt (D030 §7): a slip of that clip, which becomes the selection.
    /// False — no gesture, the status says why — for an image or a text, which have no source to slip.</summary>
    public bool BeginSlip(TimelineClipViewModel clip, double contentX)
    {
        if (!CanEdit()) return false;
        SelectOnly(clip.Id);
        if (clip.Clip is not (VideoClip or AudioClip))
        {
            _status.Report("Only video and audio clips can be slipped.");
            return false;
        }
        _gesture = new Gesture
        {
            Edge = null, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(clip), Slip = true
        };
        return true;
    }

    public void UpdateGesture(double contentX, TimelineTrackViewModel? trackUnderPointer)
    {
        if (_gesture is not { } g) return;
        if (!g.Active && Math.Abs(contentX - g.StartX) < DragThresholdPixels && trackUnderPointer == g.SourceTrack)
            return;
        g.Active = true;

        var rawDelta = TimelineCoordinateMapper.XToTime(contentX - g.StartX, PixelsPerSecond);
        if (g.Slip) UpdateSlip(g, rawDelta);
        else if (g.Edge is { } edge) UpdateTrim(g, edge, rawDelta);
        else UpdateMove(g, rawDelta, trackUnderPointer);
    }

    private void UpdateMove(Gesture g, MediaTime rawDelta, TimelineTrackViewModel? trackUnderPointer)
    {
        var groupStart = g.Clips.Min(c => c.Clip.TimelineStart);
        var groupEnd = g.Clips.Max(c => c.Clip.TimelineEnd);
        var delta = ApplySnap(new[] { groupStart + rawDelta, groupEnd + rawDelta }, g.Clips, rawDelta);

        var startFrame = groupStart.ToNearestFrame(FrameRate);
        g.FrameDelta = Math.Max(-startFrame, (groupStart + delta).ToNearestFrame(FrameRate) - startFrame);

        g.TargetTrack = g.SourceTrack is not null && trackUnderPointer is not null && trackUnderPointer != g.SourceTrack
            ? trackUnderPointer
            : null;
        ShowDropTarget(g.TargetTrack);

        var invalid = _edit.CanMoveClips(g.Clips.Select(c => c.Id).ToList(), g.FrameDelta, g.TargetTrack?.Track.Id) is not null;
        foreach (var clip in g.Clips)
        {
            var start = MediaTime.FromFrame(clip.Clip.TimelineStart.ToNearestFrame(FrameRate) + g.FrameDelta, FrameRate);
            var end = MediaTime.FromFrame(clip.Clip.TimelineEnd.ToNearestFrame(FrameRate) + g.FrameDelta, FrameRate);
            clip.Layout(PixelsPerSecond, start, end);
            clip.IsInvalid = invalid;
        }

        // Dissolves follow the preview (D025 §5): both clips dragged together carry their zone along; a zone whose cut
        // the drag would open is hidden (the release removes it).
        var moved = g.Clips.Select(c => c.Id).ToHashSet();
        foreach (var zone in _transitionViewModels.Values)
        {
            var (left, right) = (moved.Contains(zone.Transition.LeftClipId), moved.Contains(zone.Transition.RightClipId));
            if (!left && !right) continue;
            zone.IsVisible = left && right;
            if (zone.IsVisible) zone.Layout(PixelsPerSecond, FrameRate, g.FrameDelta);
        }
    }

    private void UpdateTrim(Gesture g, ClipEdge edge, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        var origin = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd;
        var proposed = origin + ApplySnap(new[] { origin + rawDelta }, g.Clips, rawDelta);
        g.TrimTime = proposed;

        if (g.Ripple)
        {
            PreviewRipple(g, clip, edge, proposed);
            return;
        }

        if (_edit.PreviewTrim(clip.Id, edge, proposed) is { } preview)
        {
            clip.Layout(PixelsPerSecond, preview.Start, preview.End);
            clip.IsInvalid = false;

            // Trimming the cut edge opens the cut: its dissolve is hidden in the preview (the release removes it). A
            // far-edge trim keeps the cut, its zone stays where it is.
            var cutEdge = edge == ClipEdge.End ? clip.Clip.TimelineEnd : clip.Clip.TimelineStart;
            var opened = (edge == ClipEdge.End ? preview.End : preview.Start) != cutEdge;
            foreach (var zone in _transitionViewModels.Values)
                if (edge == ClipEdge.End ? zone.Transition.LeftClipId == clip.Id : zone.Transition.RightClipId == clip.Id)
                    zone.IsVisible = !opened;
        }
        else
        {
            clip.IsInvalid = true;
        }
    }

    /// <summary>The slip's feedback (D030 §7, Q12): the content follows the pointer (the pointer to the right brings in
    /// earlier source: a negative slip), in whole timeline frames; the edit service plans it (nothing applied, so the
    /// timeline geometry and the Preview stay) and the clip shows the planned Source In / Out and a limit's note.</summary>
    private void UpdateSlip(Gesture g, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        g.SlipFrames = -rawDelta.ToNearestFrame(FrameRate);
        var preview = _edit.PreviewSlip(clip.Id, g.SlipFrames);
        clip.IsInvalid = preview is null;
        clip.SlipText = preview is null ? "Can't slip"
            : $"In {TimeFormat.ToTimecode(preview.SourceIn, FrameRate)}  Out {TimeFormat.ToTimecode(preview.SourceOut, FrameRate)}" +
              (preview.Note is null ? "" : "  (limit)");
    }

    /// <summary>The ripple drag's preview (D030 §6): the edit service plans the trim (nothing is applied) and the view
    /// shows the trimmed clip and every clip it would move at their planned places, the dissolve zones with their clips;
    /// the rest of the timeline as it is. A refused plan (a locked track) marks the clip invalid.</summary>
    private void PreviewRipple(Gesture g, TimelineClipViewModel clip, ClipEdge edge, MediaTime proposed)
    {
        var preview = _edit.PreviewRippleTrim(clip.Id, edge, proposed);
        var planned = preview?.Clips ?? new Dictionary<Guid, (MediaTime Start, MediaTime End)>();
        foreach (var vm in _clipViewModels.Values)
        {
            if (planned.TryGetValue(vm.Id, out var at)) vm.Layout(PixelsPerSecond, at.Start, at.End);
            else vm.Layout(PixelsPerSecond);
        }
        clip.IsInvalid = preview is null;

        // A zone sits on its cut: it moves with B (the clip starting at the cut) — a ripple never opens a cut.
        foreach (var zone in _transitionViewModels.Values)
        {
            var right = zone.Track.Clips.FirstOrDefault(c => c.Id == zone.Transition.RightClipId);
            var delta = right is not null && planned.TryGetValue(right.Id, out var at)
                ? at.Start.ToNearestFrame(FrameRate) - right.TimelineStart.ToNearestFrame(FrameRate)
                : 0;
            zone.IsVisible = true;
            zone.Layout(PixelsPerSecond, FrameRate, delta);
        }
    }

    /// <summary>Returns <paramref name="rawDelta"/> adjusted so the nearest candidate edge
    /// lands on a snap target (when snapping is on), and shows the snap indicator.</summary>
    private MediaTime ApplySnap(IReadOnlyList<MediaTime> candidates, IEnumerable<TimelineClipViewModel> dragged, MediaTime rawDelta)
    {
        IsSnapIndicatorVisible = false;
        if (!SnappingEnabled) return rawDelta;

        var tolerance = TimelineCoordinateMapper.XToTime(SnapTolerancePixels, PixelsPerSecond);
        var snap = _edit.Snap(candidates, tolerance, dragged.Select(c => c.Id).ToList());
        if (!snap.Snapped) return rawDelta;

        SnapIndicatorX = TimelineCoordinateMapper.TimeToX(snap.Target, PixelsPerSecond);
        IsSnapIndicatorVisible = true;
        return rawDelta + snap.Offset;
    }

    public void EndGesture()
    {
        if (_gesture is not { Active: true } g)
        {
            _gesture = null;
            return;
        }

        _gesture = null;
        ResetGestureVisuals(g);

        var result = g.Slip
            ? _edit.SlipClip(g.Clips[0].Id, g.SlipFrames)
            : g.Edge is { } edge
            ? g.Ripple
                ? _edit.RippleTrimClip(g.Clips[0].Id, edge, g.TrimTime)
                : _edit.TrimClip(g.Clips[0].Id, edge, g.TrimTime)
            : _edit.MoveClips(g.Clips.Select(c => c.Id).ToList(), g.FrameDelta, g.TargetTrack?.Track.Id);
        Report(result);
        if (!result.Success) Relayout(); // restore the pre-drag positions
    }

    public void CancelGesture()
    {
        if (_gesture is not { } g) return;
        _gesture = null;
        ResetGestureVisuals(g);
        Relayout();
    }

    public bool IsGestureActive => _gesture is { Active: true };

    private void ResetGestureVisuals(Gesture g)
    {
        IsSnapIndicatorVisible = false;
        ClearDropTargets();
        foreach (var clip in g.Clips)
        {
            clip.IsInvalid = false;
            clip.SlipText = null;
        }
        foreach (var zone in _transitionViewModels.Values) zone.IsVisible = true;
    }

    private TimelineTrackViewModel? TrackOf(TimelineClipViewModel clip) =>
        Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
}
