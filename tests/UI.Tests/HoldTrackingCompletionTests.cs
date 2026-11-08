using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// R2 → E completion through the coordinator, the Preview and the Inspector:
/// <list type="bullet">
/// <item>D4 — a track analysed once is kept between sessions: a new session (a new coordinator, as after reopening the
/// project) takes it from the cache without decoding a frame; a changed video file, another range, a damaged cache entry
/// or a missing cache analyse again; a relinked copy of the same file is found; Undo / Redo of an edit never re-analyses.</item>
/// <item>K2 — the states are told apart (tracked, partly unreliable, unreliable, failed) and reach the export (failed and
/// unreliable clips) and the Preview, which names the problem of the outlines shown now.</item>
/// <item>K3 — "Follow the camera" off is saved with the project and restored on open; it is one Undo step.</item>
/// </list>
/// </summary>
public sealed class HoldTrackingCompletionTests : IDisposable
{
    private const int W = 480, H = 270;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly TestUiThread _ui = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-r2e-" + Guid.NewGuid().ToString("N"));
    private readonly CameraMotionCacheStore _cache;

    public HoldTrackingCompletionTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        Directory.CreateDirectory(_folder);
        _cache = new CameraMotionCacheStore(Path.Combine(_folder, "cache"), NullLogger<CameraMotionCacheStore>.Instance);
    }

    public void Dispose()
    {
        _ui.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A real file (the decoder is a fake; the file is what the cache key reads).</summary>
    private string VideoFile(string name = "clip.mp4", int seed = 1)
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[64 * 1024];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private VideoClip AddClip(string path)
    {
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
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

    /// <summary>Counts the decoder's opens (an analysis opens it; a cache hit does not).</summary>
    private sealed class CountingDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        public int Opens { get; private set; }

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Opens++;
            return inner.OpenAsync(request, ct);
        }
    }

    private async Task<CameraMotionTrack> Session(CountingDecoder decoder, Guid clipId)
    {
        using var coordinator = new CameraMotionCoordinator(_projects, decoder, cache: _cache);
        await coordinator.IdleAsync();
        return coordinator.Tracks[clipId];
    }

    // --- D4: the cache --------------------------------------------------------------------------------------------------

    [Fact]
    public Task An_unchanged_clip_is_taken_from_the_cache_in_the_next_session_without_decoding() => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        var first = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        var analysed = await Session(first, clip.Id);
        Assert.True(first.Opens > 0);

        var second = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        var cached = await Session(second, clip.Id);
        Assert.Equal(0, second.Opens);                                    // no re-analysis
        Assert.Equal(analysed.Samples.ToArray(), cached.Samples.ToArray());
        Assert.Equal(analysed.Model, cached.Model);
    });

    [Fact]
    public Task A_changed_file_another_range_or_a_damaged_entry_is_analysed_again() => _ui.Run(async () =>
    {
        var path = VideoFile();
        var clip = AddClip(path);
        await Session(new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder()), clip.Id);

        File.WriteAllBytes(path, File.ReadAllBytes(path).Reverse().ToArray());   // another video under the same name
        var changed = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(changed, clip.Id);
        Assert.True(changed.Opens > 0);

        clip.SourceOut = MediaTime.FromSeconds(1.5);                      // another range (a trim)
        clip.Duration = MediaTime.FromSeconds(1.5);
        var trimmed = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(trimmed, clip.Id);
        Assert.True(trimmed.Opens > 0);

        foreach (var file in Directory.GetFiles(Path.Combine(_folder, "cache")))   // a damaged cache
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        var damaged = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(damaged, clip.Id);
        Assert.True(damaged.Opens > 0);
        var again = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(again, clip.Id);
        Assert.Equal(0, again.Opens);                                     // written again, found again
    });

    [Fact]
    public Task A_relinked_copy_of_the_same_file_is_found_in_the_cache() => _ui.Run(async () =>
    {
        var path = VideoFile();
        var clip = AddClip(path);
        await Session(new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder()), clip.Id);

        var moved = Path.Combine(_folder, "moved.mp4");
        File.Copy(path, moved);
        File.SetLastWriteTimeUtc(moved, File.GetLastWriteTimeUtc(path));
        _projects.Current.MediaAssets.Single().FilePath = moved;
        var relinked = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(relinked, clip.Id);
        Assert.Equal(0, relinked.Opens);
    });

    [Fact]
    public Task Undo_and_Redo_never_analyse_again() => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        var decoder = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        using var coordinator = new CameraMotionCoordinator(_projects, decoder, cache: _cache);
        await coordinator.IdleAsync();
        var opens = decoder.Opens;
        Assert.True(_edit.SetHoldFollowsCamera(clip.Id, false).Success);
        _undo.Undo();
        _undo.Redo();
        _undo.Undo();
        await coordinator.IdleAsync();
        Assert.Equal(opens, decoder.Opens);
        Assert.Equal(CameraMotionState.Tracked, coordinator.StatusOf(clip.Id).State);
    });

    [Fact]
    public Task Without_a_cache_or_a_readable_file_the_clip_is_simply_analysed() => _ui.Run(async () =>
    {
        var clip = AddClip(Path.Combine(_folder, "not-there.mp4"));      // no fingerprint: no key
        var decoder = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        await Session(decoder, clip.Id);
        Assert.True(decoder.Opens > 0);
        Assert.False(Directory.Exists(Path.Combine(_folder, "cache")) && Directory.GetFiles(Path.Combine(_folder, "cache")).Length > 0);
    });

    // --- K2: the states -------------------------------------------------------------------------------------------------

    /// <summary>The panning wall until <paramref name="lostFrom"/> (tenths of a second), then a picture of another wall —
    /// frames whose position cannot be known.</summary>
    private sealed class LosingDecoder(int lostFrom) : IVideoDecoder
    {
        private readonly CameraMotionCoordinatorTests.PanningDecoder _wall = new();

        public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            new Stream(await _wall.OpenAsync(request, ct), lostFrom);

        private sealed class Stream(IVideoFrameStream inner, int lostFrom) : IVideoFrameStream
        {
            public async ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                if (await inner.ReadFrameAsync(ct) is not { } frame) return null;
                if (frame.Timestamp.Pts < lostFrom) return frame;
                var noise = new byte[W * H * 4];
                var random = new Random((int)frame.Timestamp.Pts);
                for (var i = 0; i < noise.Length; i++) noise[i] = (byte)random.Next(256);
                return new DecodedFrame(W, H, W * 4, noise, frame.Timestamp);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(20, CameraMotionState.Tracked)]
    [InlineData(15, CameraMotionState.PartlyUnreliable)]
    [InlineData(5, CameraMotionState.Unreliable)]
    public Task The_states_are_told_apart_and_reach_the_export(int lostFrom, CameraMotionState expected) => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        using var coordinator = new CameraMotionCoordinator(_projects, new LosingDecoder(lostFrom));
        await coordinator.IdleAsync();
        var status = coordinator.StatusOf(clip.Id);
        Assert.Equal(expected, status.State);
        var export = coordinator.ForExport();
        Assert.Equal(expected != CameraMotionState.Tracked, export.Unreliable.ContainsKey(clip.Id));
        if (expected != CameraMotionState.Tracked) Assert.Equal(status.HiddenFraction, export.Unreliable[clip.Id]);
        Assert.Empty(export.Failed);
    });

    [Fact]
    public Task A_failed_analysis_is_named_as_failed_for_the_export() => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.FailingDecoder());
        await coordinator.IdleAsync();
        Assert.Equal(CameraMotionState.Failed, coordinator.StatusOf(clip.Id).State);
        Assert.True(coordinator.StatusOf(clip.Id).IsProblem);
        Assert.Contains(clip.Id, coordinator.ForExport().Failed);
        Assert.False(coordinator.ForExport().Tracks.ContainsKey(clip.Id));   // the outlines as marked — said, not silent

        var vm = new HoldMarkupViewModel(_edit, _projects, _status, cameraMotion: coordinator);
        vm.SetClip(clip);
        Assert.True(vm.HasTrackingProblem);
        Assert.Equal(Strings.Holds_TrackingFailed, vm.TrackingStatus);
    });

    [Fact]
    public Task Switched_off_is_no_problem_and_nothing_is_analysed() => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        clip.HoldMarkup = clip.HoldMarkup! with { FollowsCamera = false };
        var decoder = new CountingDecoder(new CameraMotionCoordinatorTests.PanningDecoder());
        using var coordinator = new CameraMotionCoordinator(_projects, decoder);
        await coordinator.IdleAsync();
        Assert.Equal(0, decoder.Opens);
        Assert.Equal(CameraMotionState.Off, coordinator.StatusOf(clip.Id).State);
        Assert.False(coordinator.StatusOf(clip.Id).IsProblem);
        var export = coordinator.ForExport();
        Assert.Empty(export.Failed);
        Assert.Empty(export.Unreliable);
        Assert.Empty(export.Pending);
    });

    // --- K2: the Preview's notice ---------------------------------------------------------------------------------------

    /// <summary>Plays the snapshot it is given: the layers at <see cref="Position"/>, each with a frame of that time.</summary>
    private sealed class LayeredPlayback : IPlaybackService
    {
        private PlaybackSnapshot? _snapshot;
        public MediaTime Position { get; set; }
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => true;
        public MediaTime Duration => MediaTime.FromSeconds(2);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) => _snapshot = snapshot;
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) { Position = position; return Task.FromResult(true); }

        public PlaybackFrame Update()
        {
            var layers = _snapshot is null ? ImmutableArray<LayerPicture>.Empty : _snapshot.LayersAt(Position).Select(l =>
                new LayerPicture(l, LayerPictureState.Frame,
                    new DecodedFrame(W, H, W * 4, new byte[W * H * 4], new SourceTimestamp(Position.Ticks, new TimeBase(1, (int)TimeSpan.TicksPerSecond))))).ToImmutableArray();
            return new PlaybackFrame(Position, Position.Ticks * 25 / TimeSpan.TicksPerSecond, State, false) { Layers = layers };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public Task The_preview_names_a_failed_or_unreliable_tracking_and_a_frame_whose_outlines_are_hidden() => _ui.Run(async () =>
    {
        var clip = AddClip(VideoFile());
        var decoder = new LosingDecoder(15);                              // unreliable from 1.5 s
        using var coordinator = new CameraMotionCoordinator(_projects, decoder);
        var playback = new LayeredPlayback();
        var preview = new PreviewViewModel(_status, playback, _projects, NullLogger<PreviewViewModel>.Instance, cameraMotion: coordinator);
        preview.Tick();
        Assert.Equal(Strings.Preview_TrackingAnalysing, preview.TrackingNotice);

        await coordinator.IdleAsync();
        playback.Position = MediaTime.FromSeconds(0.5);
        preview.Seek(playback.Position);
        preview.Tick();
        Assert.Null(preview.TrackingNotice);                              // tracked here: nothing to say

        playback.Position = MediaTime.FromSeconds(1.8);
        preview.Seek(playback.Position);
        preview.Tick();
        Assert.Equal(Strings.Preview_TrackingHiddenHere, preview.TrackingNotice);
        GC.KeepAlive(clip);
    });

    [Fact]
    public Task The_preview_says_when_the_outlines_do_not_follow_the_camera() => _ui.Run(async () =>
    {
        AddClip(VideoFile());
        using var coordinator = new CameraMotionCoordinator(_projects, new CameraMotionCoordinatorTests.FailingDecoder());
        var playback = new LayeredPlayback { Position = MediaTime.FromSeconds(0.5) };
        var preview = new PreviewViewModel(_status, playback, _projects, NullLogger<PreviewViewModel>.Instance, cameraMotion: coordinator);
        await coordinator.IdleAsync();
        preview.Tick();
        Assert.Equal(Strings.Preview_TrackingFailed, preview.TrackingNotice);
    });

    // --- K3: saved with the project -------------------------------------------------------------------------------------

    [Fact]
    public void Follow_the_camera_off_is_saved_restored_and_one_undo_step()
    {
        var clip = AddClip(VideoFile());
        Assert.True(_edit.SetHoldFollowsCamera(clip.Id, false).Success);
        Assert.Equal(TimelineEditResult.Unchanged().Success, _edit.SetHoldFollowsCamera(clip.Id, false).Success);
        var json = ProjectSerializer.Serialize(_projects.Current, _folder);
        Assert.Contains("\"followsCamera\": false", json);
        var reopened = ProjectSerializer.Deserialize(json, _folder);
        Assert.False(reopened.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().HoldMarkup!.FollowsCamera);

        _undo.Undo();
        Assert.True(clip.HoldMarkup!.FollowsCamera);
        Assert.DoesNotContain("followsCamera", ProjectSerializer.Serialize(_projects.Current, _folder));   // on: not written
    }
}
