using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>The In / Out range (D030 §8), the playhead, the zoom, the ruler and the per-zoom pictures (waveforms, filmstrips, markers) (split out, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineViewModel
{
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
        _inOut.SetIn(EditPlayhead);
        _status.Report(string.Format(Strings.InOut_InSet, TimeFormat.ToTimecode(_inOut.Range.In!.Value, FrameRate)));
    }

    /// <summary>O: Out after the playhead's frame — the frame at the playhead is the range's last one (Q7).</summary>
    [RelayCommand]
    private void SetOut()
    {
        _inOut.SetOut(EditPlayhead);
        _status.Report(string.Format(Strings.InOut_OutSet, TimeFormat.ToTimecode(Playhead.SnapToFrame(FrameRate), FrameRate)));
    }

    /// <summary>The ✕ on the range bar (Q11).</summary>
    [RelayCommand]
    private void ClearInOut()
    {
        _inOut.Clear();
        _status.Report(Strings.InOut_Cleared);
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
                Color = string.IsNullOrWhiteSpace(marker.ColorHex) ? "#4FC3F7" : marker.ColorHex,
                Label = marker.Label
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

    /// <summary>D068 item 10: every video clip's strip of pictures, from its video's filmstrip, its timing and the zoom.</summary>
    private void RefreshFilmstrips()
    {
        foreach (var track in Tracks)
        {
            foreach (var vm in track.Clips)
                vm.Filmstrip = vm.Clip is VideoClip video && _filmstrips?.Get(video.MediaAssetId) is { } strip
                    ? new Rendering.ClipFilmstrip(strip, video.SourceIn, video.Speed, PixelsPerSecond)
                    : null;
        }
    }

    private ClipWaveform? WaveformOf(Clip clip, Track track)
    {
        if (_waveforms is null) return null;
        var (media, volume, muted, lowerHalf) = clip switch
        {
            AudioClip a => ((MediaBackedClip)a, a.Volume, a.IsMuted, false),
            VideoClip { IsAudioDetached: false } v => ((MediaBackedClip)v, v.Volume, v.IsMuted, true),
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
        ApplyInitialZoom();
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
        FollowPlayhead();   // D058 §1: a page turn while playing
    }

    private void MovePlayhead(MediaTime position)
    {
        Sequence.PlayheadPosition = position;
        PlayheadX = TimelineCoordinateMapper.TimeToX(position, PixelsPerSecond);
        DropKeyframeSelectionOffFrame();
        NotifyKeyframeCommands();
        OpenTransitionMenuCommand.NotifyCanExecuteChanged();   // D069 amendment 1: the cut under the playhead
        AddTransitionCommand.NotifyCanExecuteChanged();
        AddDissolveCommand.NotifyCanExecuteChanged();
        RefreshRange();
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlayheadFromX(double contentX) =>
        SetPlayhead(SnapToKeyframe(SnapToCut(TimelineCoordinateMapper.XToFrameTime(contentX, PixelsPerSecond, FrameRate))));

    /// <summary>D069 amendment 1: a click or a scrub on the ruler lands on a clip's start or end (any track) within the snap
    /// distance — so the playhead stands exactly between two clips, as in other editors.</summary>
    private MediaTime SnapToCut(MediaTime time)
    {
        if (!SnappingEnabled) return time;
        MediaTime? best = null;
        var bestPixels = SnapTolerancePixels;
        foreach (var clip in Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips))
            foreach (var edge in new[] { clip.TimelineStart, clip.TimelineEnd })
            {
                var pixels = Math.Abs(TimelineCoordinateMapper.TimeToX(edge - time, PixelsPerSecond));
                if (pixels <= bestPixels) (best, bestPixels) = (edge, pixels);
            }
        return best ?? time;
    }

    public void StepFrames(long frames)
    {
        var frame = Math.Max(0, Playhead.ToNearestFrame(FrameRate) + frames);
        SetPlayhead(MediaTime.FromFrame(frame, FrameRate));
    }

    /// <summary>D057 §3.14: Left / Right — one frame of the active video clip's source (never finer than the timeline's
    /// highest frame rate; one project frame where no video is). The playhead may then lie between the project's frames.</summary>
    [RelayCommand] private void StepForward() => StepSourceFrame(1);
    [RelayCommand] private void StepBackward() => StepSourceFrame(-1);

    private void StepSourceFrame(int direction)
    {
        var project = _projectService.Current;
        var target = FrameNavigation.Step(project.Timeline, id => project.MediaAssets.FirstOrDefault(a => a.Id == id), Playhead, direction, FrameRate);
        SetPlayheadExact(target);
    }

    /// <summary>Moves the playhead to exactly <paramref name="time"/> (a source frame between the project's frames, D057
    /// §3.14) and seeks there.</summary>
    public void SetPlayheadExact(MediaTime time)
    {
        var at = time < MediaTime.Zero ? MediaTime.Zero : time;
        MovePlayhead(at);
        SeekRequested?.Invoke(this, at);
    }
    [RelayCommand] private void StepForwardSecond() => StepFrames(TimeFormat.NominalFps(FrameRate));
    [RelayCommand] private void StepBackwardSecond() => StepFrames(-TimeFormat.NominalFps(FrameRate));
    [RelayCommand] public void GoToStart() => SetPlayhead(MediaTime.Zero);
    [RelayCommand] private void GoToEnd() => SetPlayhead(SequenceDuration);

    // --- Zoom ---------------------------------------------------------------------

    [RelayCommand] private void ZoomIn() => ZoomAroundPlayhead(ZoomStep);
    [RelayCommand] private void ZoomOut() => ZoomAroundPlayhead(1 / ZoomStep);

    private bool _hadClips;
    private bool _userZoomed;     // the user zoomed this project's timeline (wheel, − / +): the first clips keep that zoom

    private bool HasClips() =>
        Sequence.VideoTracks.Any(t => t.Clips.Count > 0) || Sequence.AudioTracks.Any(t => t.Clips.Count > 0);

    /// <summary>D068 item 6 (changes D058's «adding clips keeps the zoom» for the first clips only): the first clips put on an
    /// empty timeline are shown whole — they were a sliver at the start of the ten-minute ruler — unless the user already chose
    /// a zoom; every later edit keeps the zoom.</summary>
    private void FitWhenTheFirstClipsArrive()
    {
        var has = HasClips();
        if (has && !_hadClips && !_userZoomed) ZoomToFit();
        _hadClips = has;
    }

    [RelayCommand]
    private void ZoomToFit()
    {
        SuspendFollowing();
        var duration = Math.Max(SequenceDuration.TotalSeconds, 10);
        var width = _viewportWidth > 40 ? _viewportWidth - 20 : 1000;
        ApplyZoom(width / duration);
        ScrollRequested?.Invoke(this, 0);
    }

    /// <summary>Ctrl+wheel: keeps the time under the mouse pointer fixed on screen.</summary>
    public void ZoomAtPointer(double factor, double contentX)
    {
        SuspendFollowing();
        _userZoomed = true;
        var anchorTime = TimelineCoordinateMapper.XToTime(Math.Max(0, contentX), PixelsPerSecond);
        var anchorViewportX = contentX - _scrollOffsetX;
        ApplyZoom(PixelsPerSecond * factor);
        ScrollRequested?.Invoke(this, TimelineCoordinateMapper.ScrollOffsetForAnchor(anchorTime, anchorViewportX, PixelsPerSecond));
    }

    private void ZoomAroundPlayhead(double factor)
    {
        SuspendFollowing();
        _userZoomed = true;
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
}
