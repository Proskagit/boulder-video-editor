using System.Collections.Immutable;
using System.Windows.Input;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// The Source mode of the Preview (Phase 16, D031 SQ1 / SQ2 / SQ4 / SQ14): the same playback service and audio output
/// play one asset of <see cref="SourceViewerService"/> instead of the timeline. While Source is shown, playback positions
/// go to the source state, never to the timeline playhead; timeline seeks and timeline changes do not reach playback —
/// Timeline mode rebuilds the timeline snapshot and seeks to the playhead on the way back. One snapshot version counter
/// serves both modes, so a newer snapshot always wins (<see cref="IPlaybackService.UpdateSnapshot"/>).
/// </summary>
public sealed partial class PreviewViewModel
{
    private readonly SourceViewerService? _source;
    private (MediaTime Playhead, MediaTime Duration, FrameRate Rate) _timelineDisplay = (MediaTime.Zero, MediaTime.Zero, FrameRate.Default);
    private long _timelineReportedFrame = -1;
    private long _lastSourceFrame = -1;
    private (string FilePath, bool IsMissing, MediaMetadata? Metadata)? _sourceAssetState;

    /// <summary>Source is shown (the Timeline / Source switch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineMode))]
    private bool _isSourceMode;

    public bool IsTimelineMode => !IsSourceMode;

    /// <summary>An asset is open in Source (the switch's Source side is enabled).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowSourceCommand))]
    private bool _hasSource;

    [ObservableProperty] private string _sourceName = "";

    /// <summary>The source bar: the asset's whole frames, the frame shown, In / Out frames (−1 = not set).</summary>
    [ObservableProperty] private long _sourceFrames;
    [ObservableProperty] private long _sourceFrame;
    [ObservableProperty] private long _sourceInFrame = -1;
    [ObservableProperty] private long _sourceOutFrame = -1;
    [ObservableProperty] private bool _hasSourceRange;
    [ObservableProperty] private string _sourceRangeDisplay = "";

    /// <summary>Insert (<c>,</c>) and Overwrite (<c>.</c>) for the source bar's buttons — the timeline's commands (SQ5),
    /// given by the shell.</summary>
    public ICommand? InsertCommand { get; set; }
    public ICommand? OverwriteCommand { get; set; }

    public SourceViewerService? Source => _source;

    /// <summary>Opens <paramref name="asset"/> in Source and shows it (a double click in the Media Browser). Null when
    /// shown, otherwise why not (SQ11).</summary>
    public string? OpenSource(MediaAsset asset)
    {
        if (_source is null) return "The Source viewer is not available.";
        if (_source.Open(asset) is { } reason) return reason;
        ShowSource();
        return null;
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private void ShowSource()
    {
        if (IsSourceMode || _source?.Asset is null || _released) return;
        _timelineReportedFrame = _lastReportedFrame;
        IsSourceMode = true;
        _playback.Pause();
        LoadSourceSnapshot(_source.Position);
    }

    [RelayCommand]
    private void ShowTimeline()
    {
        if (!IsSourceMode || _released) return;
        IsSourceMode = false;
        _playback.Pause();
        Layers = ImmutableArray<LayerPicture>.Empty;     // the source's picture must not stand for the timeline's
        Canvas = ProjectCanvas();
        RebuildSnapshot();
        ApplyPlaybackRange();
        _lastReportedFrame = _timelineReportedFrame;
        _ = _playback.SeekAsync(_projectService.Current.Timeline.PlayheadPosition);
        _needsTick = true;
        SetPosition(_timelineDisplay.Playhead, _timelineDisplay.Duration, _timelineDisplay.Rate);
    }

    /// <summary>Another project: Timeline mode without rebuilding here (OnProjectChanged rebuilds and seeks).</summary>
    private void LeaveSourceModeForProject()
    {
        if (!IsSourceMode) return;
        IsSourceMode = false;
        _lastReportedFrame = _timelineReportedFrame;
    }

    private void LoadSourceSnapshot(MediaTime position)
    {
        if (_released) return;   // the window is closing: playback is being released
        var asset = _source!.Asset!;
        var grid = _source.Grid!;
        Layers = ImmutableArray<LayerPicture>.Empty;
        var snapshot = PlaybackSnapshotBuilder.BuildSource(asset, grid.Rate, grid.Frames, ProjectCanvas(), ++_snapshotVersion);
        Canvas = snapshot.Canvas;
        _sourceAssetState = (asset.FilePath, asset.IsMissing, asset.Metadata);
        _playback.UpdateSnapshot(snapshot);
        ApplyPlaybackRange();
        _lastSourceFrame = -1;
        _ = _playback.SeekAsync(position);
        _needsTick = true;
        ShowSourcePosition(position.ToFrameFloor(grid.Rate));
    }

    private void OnSourceChanged()
    {
        var asset = _source!.Asset;
        HasSource = asset is not null;
        SourceName = asset?.FileName ?? "";
        SourceFrames = _source.Grid?.Frames ?? 0;
        UpdateSourceRange();
        if (!IsSourceMode) return;
        if (asset is null) ShowTimeline();                    // closed: the asset left the project, another project
        else LoadSourceSnapshot(_source.Position);           // another asset, or its grid changed
    }

    /// <summary>A media change while Source is shown: the source asset's file or metadata changed (relink, analysis) —
    /// reload it; the timeline is rebuilt on the way back anyway.</summary>
    private void OnSourceMediaChanged()
    {
        if (_source?.Asset is not { } asset) return;
        if (_sourceAssetState != (asset.FilePath, asset.IsMissing, asset.Metadata))
            LoadSourceSnapshot(_source.Position);
    }

    private void ShowSourcePosition(long frame)
    {
        if (_source?.Grid is not { } grid || frame == _lastSourceFrame) return;
        _lastSourceFrame = frame;
        var time = MediaTime.FromFrame(frame, grid.Rate);
        _source.SetPosition(time);
        SourceFrame = Math.Clamp(frame, 0, grid.Frames);
        CurrentTimeDisplay = TimeFormat.ToTimecode(time, grid.Rate);
        DurationDisplay = TimeFormat.ToTimecode(grid.Duration, grid.Rate);
    }

    /// <summary>Loop in Source loops the source range (SQ14); without one, or without Loop, the whole asset.</summary>
    private void ApplySourcePlaybackRange()
    {
        var rate = _source?.Grid?.Rate ?? FrameRate.Default;
        _playback.SetPlaybackRange(IsLooping && _source?.Frames is { } f
            ? new PlaybackRange(MediaTime.FromFrame(f.First, rate), MediaTime.FromFrame(f.End, rate))
            : null);
        _needsTick = true;
        UpdateSourceRange();
    }

    private void UpdateSourceRange()
    {
        var grid = _source?.Grid;
        var range = _source?.Range ?? InOutRange.None;
        SourceInFrame = grid is not null && range.In is { } i ? i.ToNearestFrame(grid.Rate) : -1;
        SourceOutFrame = grid is not null && range.Out is { } o ? o.ToNearestFrame(grid.Rate) : -1;
        HasSourceRange = grid is not null && range.IsSet;
        SourceRangeDisplay = grid is null || !range.IsSet ? ""
            : $"In {(range.In is { } a ? TimeFormat.ToTimecode(a, grid.Rate) : "start")} · Out {(range.Out is { } b ? TimeFormat.ToTimecode(b, grid.Rate) : "end")}";
    }

    // --- Source transport (SQ4: the keys act here while Source is shown) -------------------------------------------------

    private long CurrentSourceFrame => _source?.Grid is { } g ? Math.Clamp(_playback.Position.ToFrameFloor(g.Rate), 0, g.Frames) : 0;

    /// <summary>The source bar was clicked or dragged at <paramref name="frame"/>.</summary>
    public void SeekSource(long frame)
    {
        if (!IsSourceMode || _source?.Grid is not { } grid) return;
        _ = _playback.SeekAsync(MediaTime.FromFrame(Math.Clamp(frame, 0, grid.Frames), grid.Rate));
        _needsTick = true;
    }

    private void StepSource(long frames) => SeekSource(CurrentSourceFrame + frames);

    private long SourceSecond => _source?.Grid is { } g ? TimeFormat.NominalFps(g.Rate) : 1;

    [RelayCommand] private void SourceStepBackward() => StepSource(-1);
    [RelayCommand] private void SourceStepForward() => StepSource(1);
    [RelayCommand] private void SourceStepBackwardSecond() => StepSource(-SourceSecond);
    [RelayCommand] private void SourceStepForwardSecond() => StepSource(SourceSecond);
    [RelayCommand] private void SourceGoToStart() => SeekSource(0);
    [RelayCommand] private void SourceGoToEnd() => SeekSource(_source?.Grid?.Frames ?? 0);

    [RelayCommand]
    private void SetSourceIn()
    {
        if (!IsSourceMode || _source?.Grid is not { } grid) return;
        _source.SetIn(MediaTime.FromFrame(CurrentSourceFrame, grid.Rate));
        _status.Report($"Source In set at {TimeFormat.ToTimecode(_source.Range.In!.Value, grid.Rate)}.");
    }

    [RelayCommand]
    private void SetSourceOut()
    {
        if (!IsSourceMode || _source?.Grid is not { } grid) return;
        _source.SetOut(MediaTime.FromFrame(CurrentSourceFrame, grid.Rate));
        _status.Report($"Source Out set at {TimeFormat.ToTimecode(_source.Range.Out!.Value, grid.Rate)}.");
    }

    [RelayCommand]
    private void ClearSourceRange()
    {
        if (_source is null || !_source.Range.IsSet) return;
        _source.ClearRange();
        _status.Report("Source In / Out cleared.");
    }
}
