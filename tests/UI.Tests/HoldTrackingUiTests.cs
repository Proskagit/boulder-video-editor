using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Hold tracking (R2 → E) in the Inspector and the Preview: the clip's state (analysing → fixed to the wall / failed),
/// the manual correction "Follow the camera" off — the Preview's snapshot and the export's tracks both drop the clip, so
/// its contours stay where they were marked in both, and nothing waits for it — on again, "Retry" after a failure; none
/// of it is a project edit; the Preview stops listening to the coordinator when it is released.
/// </summary>
public sealed class HoldTrackingUiTests : IDisposable
{
    private const int W = 480, H = 270;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly TestUiThread _ui = new();
    public void Dispose() => _ui.Dispose();

    public HoldTrackingUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private VideoClip AddClip()
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(2), Width = W, Height = H, DisplayWidth = W, DisplayHeight = H,
                FrameRate = FrameRate.Fps25, StartTime = MediaTime.Zero
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2),
            HoldMarkup = RouteColor.WithResolved(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(),
                    ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
                    RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero))
            })
        };
        clip.SourceOut = clip.Duration;
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        return clip;
    }

    [Fact]
    public Task The_inspector_shows_analysing_then_fixed_to_the_wall() => _ui.Run(async () =>
    {
        var clip = AddClip();
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.PanningDecoder());
        var vm = new HoldMarkupViewModel(_edit, _projects, _status, cameraMotion: coordinator);
        vm.SetClip(clip);
        Assert.True(vm.ShowsTracking);
        Assert.True(vm.FollowsCamera);
        Assert.Equal(Strings.Holds_TrackingAnalysing, vm.TrackingStatus);

        await coordinator.IdleAsync();
        Assert.Equal(Strings.Holds_TrackingTracked, vm.TrackingStatus);
        Assert.False(vm.IsTrackingFailed);
        Assert.False(vm.RetryTrackingCommand.CanExecute(null));
    });

    [Fact]
    public Task Switching_it_off_leaves_the_contours_as_marked_in_the_preview_and_the_export() => _ui.Run(async () =>
    {
        var clip = AddClip();
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.PanningDecoder());
        var playback = new RecordingPlayback();
        var preview = new PreviewViewModel(_status, playback, _projects, NullLogger<PreviewViewModel>.Instance, cameraMotion: coordinator);
        var vm = new HoldMarkupViewModel(_edit, _projects, _status, cameraMotion: coordinator);
        vm.SetClip(clip);
        await coordinator.IdleAsync();
        Assert.NotNull(OutlineOf(playback.Last!).Motion);

        vm.FollowsCamera = false;
        Assert.Equal(Strings.Holds_TrackingOff, vm.TrackingStatus);
        Assert.Null(OutlineOf(playback.Last!).Motion);                    // the Preview draws them as marked …
        Assert.False(coordinator.ForExport().Tracks.ContainsKey(clip.Id)); // … and so does the export,
        Assert.DoesNotContain(clip.Id, coordinator.ForExport().Pending);   // which waits for nothing
        Assert.False(_undo.CanUndo);                                       // not a project edit

        vm.FollowsCamera = true;
        Assert.True(vm.FollowsCamera, "vm");
        Assert.Equal(CameraMotionState.Tracked, coordinator.StatusOf(clip.Id).State);
        Assert.Equal(Strings.Holds_TrackingTracked, vm.TrackingStatus);
        Assert.NotNull(OutlineOf(playback.Last!).Motion);
        Assert.Same(coordinator.Tracks[clip.Id], OutlineOf(playback.Last!).Motion);
        GC.KeepAlive(preview);
    });

    [Fact]
    public Task A_failed_analysis_can_be_retried() => _ui.Run(async () =>
    {
        var clip = AddClip();
        var decoder = new FlakyDecoder();
        using var coordinator = new CameraMotionCoordinator(_projects, decoder);
        var vm = new HoldMarkupViewModel(_edit, _projects, _status, cameraMotion: coordinator);
        vm.SetClip(clip);
        await coordinator.IdleAsync();
        Assert.Equal(Strings.Holds_TrackingFailed, vm.TrackingStatus);
        Assert.True(vm.IsTrackingFailed);
        Assert.True(vm.RetryTrackingCommand.CanExecute(null));

        decoder.Fail = false;
        vm.RetryTrackingCommand.Execute(null);
        Assert.Equal(Strings.Holds_TrackingAnalysing, vm.TrackingStatus);
        await coordinator.IdleAsync();
        Assert.Equal(Strings.Holds_TrackingTracked, vm.TrackingStatus);
    });

    [Fact]
    public Task A_clip_without_holds_shows_no_tracking() => _ui.Run(() =>
    {
        var clip = AddClip();
        clip.HoldMarkup = null;
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.PanningDecoder());
        var vm = new HoldMarkupViewModel(_edit, _projects, _status, cameraMotion: coordinator);
        vm.SetClip(clip);
        Assert.False(vm.ShowsTracking);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_released_preview_no_longer_listens_to_the_coordinator() => _ui.Run(async () =>
    {
        var clip = AddClip();
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.PanningDecoder());
        var playback = new RecordingPlayback();
        var preview = new PreviewViewModel(_status, playback, _projects, NullLogger<PreviewViewModel>.Instance, cameraMotion: coordinator);
        await coordinator.IdleAsync();
        await preview.ReleasePlaybackAsync();
        var before = playback.Updates;
        coordinator.SetFollowsCamera(clip.Id, false);   // a track goes away: TracksChanged
        Assert.Equal(before, playback.Updates);
    });

    private static HoldOutline OutlineOf(PlaybackSnapshot snapshot) => snapshot.VideoLayers.Single().Spans.Single().Outline!;

    private sealed class FlakyDecoder : IVideoDecoder
    {
        private readonly CameraMotionCoordinatorTests.PanningDecoder _inner = new();
        public bool Fail { get; set; } = true;

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            Fail ? throw new VideoDecodeException(VideoDecodeError.DecoderFailed, "busy") : _inner.OpenAsync(request, ct);
    }

    private sealed class RecordingPlayback : IPlaybackService
    {
        public PlaybackSnapshot? Last { get; private set; }
        public int Updates { get; private set; }
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => true;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.FromSeconds(10);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) { Last = snapshot; Updates++; }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(Position, 0, State, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
