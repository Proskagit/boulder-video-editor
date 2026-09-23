using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
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

    private readonly Dictionary<Guid, TimelineClipViewModel> _clipViewModels = new();
    private readonly Dictionary<Track, TimelineTrackViewModel> _trackViewModels = new();
    private readonly List<Guid> _selection = new(); // last = primary

    private Gesture? _gesture;
    private double _viewportWidth;
    private double _scrollOffsetX;

    public TimelineViewModel(
        IProjectService projectService,
        ITimelineEditService edit,
        StatusService status,
        ILogger<TimelineViewModel> logger)
    {
        _projectService = projectService;
        _edit = edit;
        _status = status;
        _logger = logger;

        _projectService.TimelineChanged += (_, _) => Refresh();
        _projectService.ProjectChanged += (_, _) => { _selection.Clear(); Refresh(); };
        _projectService.MediaAssetsChanged += (_, _) => RefreshClipNames();

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

    [ObservableProperty] private double _pixelsPerSecond;
    [ObservableProperty] private double _contentWidth;
    [ObservableProperty] private double _tracksHeight;
    [ObservableProperty] private double _playheadX;
    [ObservableProperty] private string _frameRateDisplay = "";
    [ObservableProperty] private bool _snappingEnabled;
    [ObservableProperty] private bool _isSnapIndicatorVisible;
    [ObservableProperty] private double _snapIndicatorX;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _hasSelection;

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

    private void Refresh()
    {
        var desiredTracks = Sequence.VideoTracks.OrderByDescending(t => t.Order)
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
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimelineClipViewModel GetClipViewModel(Clip clip)
    {
        if (!_clipViewModels.TryGetValue(clip.Id, out var vm))
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
        TextClip t => t.Text,
        _ => "Clip"
    };

    private MediaAsset? FindAsset(Guid id) => _projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == id);

    /// <summary>Recomputes every pixel position from the model at the current zoom.</summary>
    private void Relayout()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.Layout(PixelsPerSecond);

        var contentEnd = TimelineCoordinateMapper.TimeToX(SequenceDuration + TrailingSpace, PixelsPerSecond);
        ContentWidth = Math.Max(contentEnd, _viewportWidth);
        PlayheadX = TimelineCoordinateMapper.TimeToX(Playhead, PixelsPerSecond);
        RebuildRuler();
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

    private static string FormatRate(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Math.Round(rate.ToDouble(), 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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

    [RelayCommand]
    private void SplitAtPlayhead() =>
        Report(_edit.Split(Playhead, _selection.Count > 0 ? _selection.ToList() : null));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        var result = _edit.DeleteClips(_selection.ToList());
        if (result.Success) ClearSelection();
        Report(result);
    }

    [RelayCommand] private void AddVideoTrack() => Report(_edit.AddTrack(TrackType.Video));
    [RelayCommand] private void AddAudioTrack() => Report(_edit.AddTrack(TrackType.Audio));

    [RelayCommand] private void ToggleSnapping() => SnappingEnabled = !SnappingEnabled;

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
        var result = _edit.AddClip(assetId, trackId, start);
        if (result.Success && result.ClipIds.Count > 0)
        {
            _selection.Clear();
            _selection.AddRange(result.ClipIds);
            UpdateSelectionVisuals();
            RaiseSelectionChanged();
        }
        Report(result, successMessage: "Added to timeline");
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

    public void ClearSelection()
    {
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
        UpdateSelectionVisuals();
    }

    private void SelectOnly(Guid id)
    {
        _selection.Clear();
        _selection.Add(id);
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.IsSelected = _selection.Contains(vm.Id);
        HasSelection = _selection.Count > 0;
    }

    private void RaiseSelectionChanged()
    {
        if (_selection.Count > 0 && _clipViewModels.TryGetValue(_selection[^1], out var primary))
        {
            var asset = primary.Clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;
            SelectionChanged?.Invoke(this, new TimelineClipSelection(primary.Clip, primary.Name, asset, FrameRate));
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
    }

    public void BeginMove(double contentX)
    {
        var clips = _selection.Where(_clipViewModels.ContainsKey).Select(id => _clipViewModels[id]).ToList();
        if (clips.Count == 0) return;
        var tracks = clips.Select(TrackOf).Distinct().ToList();
        _gesture = new Gesture { Edge = null, StartX = contentX, Clips = clips, SourceTrack = tracks.Count == 1 ? tracks[0] : null };
    }

    public void BeginTrim(TimelineClipViewModel clip, ClipEdge edge, double contentX)
    {
        SelectOnly(clip.Id);
        _gesture = new Gesture
        {
            Edge = edge, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(clip),
            TrimTime = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd
        };
    }

    public void UpdateGesture(double contentX, TimelineTrackViewModel? trackUnderPointer)
    {
        if (_gesture is not { } g) return;
        if (!g.Active && Math.Abs(contentX - g.StartX) < DragThresholdPixels && trackUnderPointer == g.SourceTrack)
            return;
        g.Active = true;

        var rawDelta = TimelineCoordinateMapper.XToTime(contentX - g.StartX, PixelsPerSecond);
        if (g.Edge is { } edge) UpdateTrim(g, edge, rawDelta);
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
    }

    private void UpdateTrim(Gesture g, ClipEdge edge, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        var origin = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd;
        var proposed = origin + ApplySnap(new[] { origin + rawDelta }, g.Clips, rawDelta);
        g.TrimTime = proposed;

        if (_edit.PreviewTrim(clip.Id, edge, proposed) is { } preview)
        {
            clip.Layout(PixelsPerSecond, preview.Start, preview.End);
            clip.IsInvalid = false;
        }
        else
        {
            clip.IsInvalid = true;
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

        var result = g.Edge is { } edge
            ? _edit.TrimClip(g.Clips[0].Id, edge, g.TrimTime)
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
        foreach (var clip in g.Clips) clip.IsInvalid = false;
    }

    private TimelineTrackViewModel? TrackOf(TimelineClipViewModel clip) =>
        Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
}
