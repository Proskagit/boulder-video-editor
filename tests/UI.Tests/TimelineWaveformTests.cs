using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.5d: the timeline's clips get what their waveform shows (PO-W1–W5) — audio clips over the whole clip, video
/// clips with sound in the lower half, none for silent video, images or text; the clip's timing, speed and volume;
/// dimmed when the clip or its track is muted; a waveform made later or cached for offline media appears, offline media
/// without one shows none; edits and zoom are followed. Real project, edit service and coordinator; a fake waveform
/// service.
/// </summary>
public sealed class TimelineWaveformTests
{
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly FakeWaveforms _service = new();
    private readonly WaveformCoordinator _coordinator;
    private readonly TimelineViewModel _timeline;

    public TimelineWaveformTests()
    {
        _coordinator = new WaveformCoordinator(_service, _projects, new Location(), NullLogger<WaveformCoordinator>.Instance);
        var edit = new TimelineEditService(_projects, new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance,
            waveforms: _coordinator);
    }

    private sealed class FakeWaveforms : IWaveformService
    {
        public ConcurrentDictionary<Guid, Waveform> Cached { get; } = new();
        public TaskCompletionSource Gate { get; set; } = Opened();
        public int Makes;

        private static TaskCompletionSource Opened()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public Waveform? TryGetCached(MediaAsset asset, string cacheFolder) => Cached.TryGetValue(asset.Id, out var w) ? w : null;

        public async Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Makes);
            await Gate.Task.WaitAsync(ct);
            return Data;
        }
    }

    private sealed class Location : IWaveformCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private static readonly Waveform Data = new(256, 48_000, new byte[188]);

    private static MediaAsset Media(MediaKind kind, string? audioCodec = "aac") => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-timeline-waveforms", Guid.NewGuid().ToString("N"), kind == MediaKind.Audio ? "a.wav" : "v.mp4"),
        Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = audioCodec, Width = 64, Height = 36 }
    };

    private T Place<T>(T clip, MediaAsset asset, Track? track = null) where T : Clip
    {
        if (!_projects.Current.MediaAssets.Contains(asset)) _projects.AddMediaAssets(new[] { asset });
        var timeline = _projects.Current.Timeline;
        (track ?? (clip is AudioClip ? timeline.AudioTracks[0] : timeline.VideoTracks[0])).Clips.Add(clip);
        _projects.NotifyTimelineChanged();
        return clip;
    }

    private static AudioClip Audio(MediaAsset asset, double start = 0) => new()
    {
        MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(start), Duration = MediaTime.FromSeconds(2),
        SourceOut = MediaTime.FromSeconds(2)
    };

    private static VideoClip Video(MediaAsset asset) => new()
    {
        MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2)
    };

    private TimelineClipViewModel Row(Clip clip) => _timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    private async Task Settle() => await _coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task An_audio_clip_shows_its_waveform_over_the_whole_clip_with_its_timing_and_volume()
    {
        var asset = Media(MediaKind.Audio);
        var clip = Place(Audio(asset, start: 3), asset);
        clip.Volume = 1.5;
        _projects.NotifyTimelineChanged();
        await Settle();

        var waveform = Row(clip).Waveform!;
        Assert.Same(Data, waveform.Data);
        Assert.Equal((MediaTime.FromSeconds(3), MediaTime.FromSeconds(5), MediaTime.Zero, ClipSpeed.Normal),
            (waveform.TimelineStart, waveform.TimelineEnd, waveform.SourceIn, waveform.Speed));
        Assert.Equal((1.5, false, false, _timeline.PixelsPerSecond),
            (waveform.Volume, waveform.IsMuted, waveform.LowerHalf, waveform.PixelsPerSecond));
        Assert.True(Row(clip).HasWaveform);
    }

    [Fact]
    public async Task Video_with_sound_uses_the_lower_half_silent_video_images_and_text_show_none()
    {
        var sound = Media(MediaKind.Video);
        var silent = Media(MediaKind.Video, audioCodec: null);
        var image = Media(MediaKind.Image);
        var withSound = Place(Video(sound), sound);
        var withoutSound = Place(new VideoClip { MediaAssetId = silent.Id, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(1), SourceOut = MediaTime.FromSeconds(1) }, silent);
        var picture = Place(new ImageClip { MediaAssetId = image.Id, TimelineStart = MediaTime.FromSeconds(4), Duration = MediaTime.FromSeconds(1) }, image);
        var text = Place(new TextClip { Text = "Hi", TimelineStart = MediaTime.FromSeconds(6), Duration = MediaTime.FromSeconds(1) }, sound);
        await Settle();

        Assert.True(Row(withSound).Waveform!.LowerHalf);
        Assert.Null(Row(withoutSound).Waveform);
        Assert.Null(Row(picture).Waveform);
        Assert.Null(Row(text).Waveform);
        Assert.Equal(1, _service.Makes);                                           // only the video with sound was made
    }

    [Fact]
    public async Task A_waveform_made_later_appears_on_its_clips()
    {
        _service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asset = Media(MediaKind.Audio);
        var first = Place(Audio(asset), asset);
        var second = Place(Audio(asset, start: 5), asset);
        Assert.Null(Row(first).Waveform);
        var raised = new List<string?>();
        Row(first).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _service.Gate.SetResult();
        await Settle();

        Assert.NotNull(Row(first).Waveform);
        Assert.NotNull(Row(second).Waveform);
        Assert.Contains(nameof(TimelineClipViewModel.HasWaveform), raised);
    }

    [Fact]
    public async Task A_muted_clip_or_a_clip_on_a_muted_track_is_dimmed_with_the_same_shape()
    {
        var asset = Media(MediaKind.Audio);
        var clip = Place(Audio(asset), asset);
        await Settle();
        Assert.False(Row(clip).Waveform!.IsMuted);

        clip.IsMuted = true;
        _projects.NotifyTimelineChanged();
        Assert.True(Row(clip).Waveform!.IsMuted);
        Assert.Same(Data, Row(clip).Waveform!.Data);

        clip.IsMuted = false;
        _projects.Current.Timeline.AudioTracks[0].IsMuted = true;
        _projects.NotifyTimelineChanged();
        Assert.True(Row(clip).Waveform!.IsMuted);
    }

    [Fact]
    public async Task Trim_move_speed_and_zoom_are_followed()
    {
        var asset = Media(MediaKind.Audio);
        var clip = Place(Audio(asset), asset);
        await Settle();

        clip.TimelineStart = MediaTime.FromSeconds(4);
        clip.SourceIn = MediaTime.FromSeconds(1);
        clip.Speed = ClipSpeed.FromSteps(40);
        clip.Duration = MediaTime.FromSeconds(0.5);
        _projects.NotifyTimelineChanged();
        var waveform = Row(clip).Waveform!;
        Assert.Equal((MediaTime.FromSeconds(4), MediaTime.FromSeconds(4.5), MediaTime.FromSeconds(1), ClipSpeed.FromSteps(40)),
            (waveform.TimelineStart, waveform.TimelineEnd, waveform.SourceIn, waveform.Speed));

        _timeline.ZoomInCommand.Execute(null);
        Assert.Equal(_timeline.PixelsPerSecond, Row(clip).Waveform!.PixelsPerSecond);
        Assert.NotEqual(waveform.PixelsPerSecond, Row(clip).Waveform!.PixelsPerSecond);
    }

    [Fact]
    public async Task Unchanged_waveforms_are_not_raised_again()
    {
        var asset = Media(MediaKind.Audio);
        var clip = Place(Audio(asset), asset);
        await Settle();
        var raised = 0;
        Row(clip).PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimelineClipViewModel.Waveform)) raised++; };

        _projects.NotifyTimelineChanged();                                         // nothing about this clip changed
        _projects.NotifyTimelineChanged();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Offline_media_shows_its_cached_waveform_and_none_without_one()
    {
        var cached = Media(MediaKind.Audio);
        cached.IsMissing = true;
        _service.Cached[cached.Id] = Data;
        var bare = Media(MediaKind.Audio);
        bare.IsMissing = true;

        var withCache = Place(Audio(cached), cached);
        var withoutCache = Place(Audio(bare, start: 3), bare);
        await Settle();

        Assert.Same(Data, Row(withCache).Waveform!.Data);
        Assert.Null(Row(withoutCache).Waveform);
        Assert.Equal(0, _service.Makes);                                           // nothing decoded
    }

    [Fact]
    public async Task Without_a_coordinator_no_clip_has_a_waveform()
    {
        var edit = new TimelineEditService(_projects, new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        var plain = new TimelineViewModel(_projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        var asset = Media(MediaKind.Audio);
        var clip = Place(Audio(asset), asset);
        await Settle();

        Assert.Null(plain.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id).Waveform);
    }
}
