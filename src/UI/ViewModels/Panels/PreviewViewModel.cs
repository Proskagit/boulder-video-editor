using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Central preview transport over <see cref="IPlaybackService"/> (D011). The view calls
/// <see cref="Tick"/> from its UI timer; each tick polls <see cref="IPlaybackService.Update"/>
/// and publishes the picture, state and position — nothing is pushed from background threads.
/// The timeline stays the owner of the playhead: user playhead moves arrive via
/// <see cref="Seek"/>, and playback reports its position through
/// <see cref="PlaybackPositionChanged"/> (which must not be turned back into a seek).
/// A new immutable <see cref="PlaybackSnapshot"/> is built here, on the UI thread, whenever the
/// project, its timeline or its media change.
/// </summary>
public sealed partial class PreviewViewModel : ViewModelBase
{
    private readonly StatusService _status;
    private readonly IPlaybackService _playback;
    private readonly IProjectService _projectService;
    private readonly ILogger<PreviewViewModel> _logger;

    private long _snapshotVersion;
    private ImmutableDictionary<Guid, PlaybackSnapshotBuilder.AssetState> _assetStates =
        ImmutableDictionary<Guid, PlaybackSnapshotBuilder.AssetState>.Empty;
    private bool _needsTick = true;
    private long _lastReportedFrame = -1;
    private bool _reportedUnavailable;
    private bool _reportedNoAudio;
    private bool _released;

    [ObservableProperty] private string _currentTimeDisplay = "00:00:00:00";
    [ObservableProperty] private string _durationDisplay = "00:00:00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty] private bool _isBuffering;

    /// <summary>Loop (D024 Step 9.6, PO-H3): while on, playback that reaches the end of the sequence continues from the
    /// start (the whole sequence — there is no in / out range); off, the D011 end rule is unchanged (paused at the end).
    /// Session state only: not saved in the project, not an edit, not undoable, kept across New / Open.</summary>
    [ObservableProperty] private bool _isLooping;

    /// <summary>
    /// The composition to draw (D018/D019): visible layers bottom to top, each with its state.
    /// While a seek or a timeline change is buffering, the previous layers stay, so seeking never
    /// flashes an empty canvas.
    /// </summary>
    [ObservableProperty] private ImmutableArray<LayerPicture> _layers = ImmutableArray<LayerPicture>.Empty;

    /// <summary>The project canvas the layers are laid out on (composition coordinates).</summary>
    [ObservableProperty] private FrameSize _canvas;

    /// <summary>False while any layer is still pending or late (D012); the preview keeps polling
    /// until every layer has its picture (e.g. a layer uncovered by an opacity change while paused).</summary>
    [ObservableProperty] private bool _areLayersCurrent = true;

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>Requests a playhead step by this many frames (negative = back).</summary>
    public event EventHandler<long>? FrameStepRequested;

    /// <summary>Playback moved to the frame starting at this time (UI thread, from <see cref="Tick"/>).</summary>
    public event EventHandler<MediaTime>? PlaybackPositionChanged;

    public PreviewViewModel(StatusService status, IPlaybackService playback, IProjectService projectService,
        ILogger<PreviewViewModel> logger)
    {
        _status = status;
        _playback = playback;
        _projectService = projectService;
        _logger = logger;

        _playback.StateChanged += (_, _) => { IsPlaying = _playback.State == PlaybackState.Playing; _needsTick = true; };
        _projectService.TimelineChanged += (_, _) => RebuildSnapshot();
        _projectService.MediaAssetsChanged += (_, _) => OnMediaAssetsChanged();
        _projectService.ProjectChanged += (_, _) => OnProjectChanged();
        Canvas = ProjectCanvas();
        RebuildSnapshot();
    }

    public void SetPosition(MediaTime playhead, MediaTime duration, FrameRate rate)
    {
        CurrentTimeDisplay = TimeFormat.ToTimecode(playhead, rate);
        DurationDisplay = TimeFormat.ToTimecode(duration, rate);
    }

    /// <summary>User moved the playhead: seek there without changing Playing/Paused.</summary>
    public void Seek(MediaTime position)
    {
        _ = _playback.SeekAsync(position);
        _needsTick = true;
    }

    /// <summary>
    /// One UI tick. Polls playback while playing, buffering, catching up after a late frame
    /// or right after a transport/seek/snapshot change; otherwise returns without touching
    /// the playback service (no work while paused and settled).
    /// </summary>
    public void Tick()
    {
        if (_released) return; // the window is closing: keep the last picture
        if (!_needsTick && !IsPlaying && !IsBuffering && AreLayersCurrent)
            return;

        var wasPlaying = _playback.State == PlaybackState.Playing;
        var frame = _playback.Update();
        if (IsLooping && wasPlaying && frame.State == PlaybackState.Paused && frame.Position >= _playback.Duration)
        {
            // This update reached the end and paused there (D011); Play at the end starts again from 0.
            _playback.Play();
            frame = _playback.Update();
        }
        IsPlaying = frame.State == PlaybackState.Playing;
        IsBuffering = frame.IsBuffering;
        ShowLayers(frame);

        if (frame.TimelineFrame != _lastReportedFrame)
        {
            _lastReportedFrame = frame.TimelineFrame;
            PlaybackPositionChanged?.Invoke(this, MediaTime.FromFrame(frame.TimelineFrame, _projectService.Current.Settings.FrameRate));
        }

        if (!_playback.IsAvailable && !_reportedUnavailable)
        {
            _reportedUnavailable = true;
            _status.Report("Playback is unavailable: FFmpeg could not be found. Install FFmpeg or configure its path.");
        }

        if (IsPlaying && !_playback.IsAudioAvailable && !_reportedNoAudio)
        {
            _reportedNoAudio = true;
            _status.Report("Playing without sound: no audio output is available.");
        }

        if (!IsPlaying && !IsBuffering && AreLayersCurrent)
            _needsTick = false;
    }

    /// <summary>
    /// The window is closing: stops polling and releases playback — decoders, their ffmpeg processes and the
    /// audio device — while the UI thread still runs its dispatcher. Its asynchronous disposal must not be left
    /// to the host's synchronous disposal after the dispatcher has stopped: continuations posted to the stopped
    /// dispatcher never run, and closing hung (D024 Step 9.3).
    /// </summary>
    public async Task ReleasePlaybackAsync()
    {
        _released = true;
        await _playback.DisposeAsync();
    }

    private void ShowLayers(PlaybackFrame frame)
    {
        if (frame.Canvas.IsValid && frame.Canvas != Canvas)
            Canvas = frame.Canvas;
        if (frame.IsBuffering)
        {
            AreLayersCurrent = false;
            return; // keep the previous composition until the new position is decoded
        }
        Layers = frame.Layers;
        AreLayersCurrent = frame.Layers.All(l => l.IsCurrent && l.State != LayerPictureState.Pending);
    }

    private FrameSize ProjectCanvas()
    {
        var settings = _projectService.Current.Settings;
        return new FrameSize(settings.FrameWidth, settings.FrameHeight);
    }

    private void RebuildSnapshot()
    {
        _assetStates = PlaybackSnapshotBuilder.CaptureAssetStates(_projectService.Current);
        _playback.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_projectService.Current, ++_snapshotVersion));
        _needsTick = true;
    }

    /// <summary>Media changes (import, analysis, missing files) rebuild only if an asset the
    /// timeline uses changed in a way that affects playback — otherwise playback, and its
    /// decoders, are left alone.</summary>
    private void OnMediaAssetsChanged()
    {
        var states = PlaybackSnapshotBuilder.CaptureAssetStates(_projectService.Current);
        if (PlaybackSnapshotBuilder.AssetStatesDiffer(states, _assetStates))
            RebuildSnapshot();
    }

    private void OnProjectChanged()
    {
        Layers = ImmutableArray<LayerPicture>.Empty; // the old project's layers must not survive
        Canvas = ProjectCanvas();
        _playback.Pause();
        RebuildSnapshot();
        Seek(_projectService.Current.Timeline.PlayheadPosition);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_playback.State == PlaybackState.Playing)
            _playback.Pause();
        else
            _playback.Play();
        _needsTick = true;
        _logger.LogDebug("Playback {State} at {Position}.", _playback.State, _playback.Position);
    }

    /// <summary>L (D024 Step 9.6, PO-H2): plays; nothing when already playing (no faster speeds). At the end of the
    /// sequence it starts again from 0, like Play (D011).</summary>
    [RelayCommand]
    private void Play()
    {
        if (_playback.State == PlaybackState.Playing) return;
        _playback.Play();
        _needsTick = true;
    }

    /// <summary>K (D024 Step 9.6, PO-H2): pauses; nothing when already paused.</summary>
    [RelayCommand]
    private void Pause()
    {
        if (_playback.State != PlaybackState.Playing) return;
        _playback.Pause();
        _needsTick = true;
    }

    /// <summary>Ctrl+L and the Loop button (PO-H3).</summary>
    [RelayCommand]
    private void ToggleLoop() => IsLooping = !IsLooping;

    [RelayCommand]
    private void Stop()
    {
        _playback.Stop();
        _needsTick = true;
    }

    [RelayCommand]
    private void PreviousFrame() => FrameStepRequested?.Invoke(this, -1);

    [RelayCommand]
    private void NextFrame() => FrameStepRequested?.Invoke(this, 1);
}
