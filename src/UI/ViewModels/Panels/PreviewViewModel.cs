using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
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

    [ObservableProperty] private string _currentTimeDisplay = "00:00:00:00";
    [ObservableProperty] private string _durationDisplay = "00:00:00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty] private bool _isBuffering;

    /// <summary>False while the decoder is behind and the previous picture is still shown (D012).</summary>
    [ObservableProperty] private bool _isPictureCurrent = true;

    /// <summary>The decoded frame to show, or null (black / placeholder / nothing decoded yet).</summary>
    [ObservableProperty] private DecodedFrame? _currentFrame;

    /// <summary>Text shown instead of a frame for Offline / Unsupported / DecodeError; null otherwise.</summary>
    [ObservableProperty] private string? _placeholderText;

    /// <summary>Kind of the picture on screen (Black while nothing has been decoded yet).</summary>
    [ObservableProperty] private PictureKind _pictureKind = PictureKind.Black;

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
        if (!_needsTick && !IsPlaying && !IsBuffering && IsPictureCurrent)
            return;

        var frame = _playback.Update();
        IsPlaying = frame.State == PlaybackState.Playing;
        IsBuffering = frame.IsBuffering;
        IsPictureCurrent = frame.IsPictureCurrent;
        if (frame.Picture is { } picture)
            Show(picture);

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

        if (!IsPlaying && !IsBuffering && IsPictureCurrent)
            _needsTick = false;
    }

    private void Show(PreviewPicture picture)
    {
        PictureKind = picture.Kind;
        CurrentFrame = picture.Kind == PictureKind.Frame ? picture.Frame : null;
        PlaceholderText = picture.Kind switch
        {
            PictureKind.Offline => "Media offline",
            PictureKind.Unsupported => "Unsupported clip",
            PictureKind.DecodeError => "Cannot decode media",
            _ => null
        };
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
