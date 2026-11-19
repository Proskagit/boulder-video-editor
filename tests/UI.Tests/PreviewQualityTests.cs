using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D071 §2 and amendment 3 through the view models: the Preview's quality (saved in the settings, «Среднее» by default; a compact
/// button with its menu) bounds every video's decoding on the fly — no proxies; «Оригинал» decodes the source at its own size.
/// </summary>
public sealed class PreviewQualityTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly RecordingPlayback _playback = new();
    private readonly InMemoryUserSettings _settings = new();
    private readonly MediaAsset _uhd;
    private readonly MediaAsset _portrait;
    private PreviewViewModel? _preview;

    public PreviewQualityTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _uhd = Video("climb-4k.mp4", 3840, 2160);
        _portrait = Video("phone.mp4", 2160, 3840);
        _projects.AddMediaAssets(new[] { _uhd, _portrait });
        Assert.True(_edit.AddClip(_uhd.Id).Success);
        Assert.True(_edit.AddClip(_portrait.Id).Success);
    }

    private static MediaAsset Video(string name, int w, int h) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-quality", name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = w, Height = h, DisplayWidth = w, DisplayHeight = h,
            VideoCodec = "hevc", PixelFormat = "yuv420p"
        }
    };

    private PreviewViewModel Preview() =>
        _preview = new PreviewViewModel(new StatusService(), _playback, _projects, NullLogger<PreviewViewModel>.Instance, settings: _settings);

    private PlaybackAsset Decoding(MediaAsset asset) => _playback.Last!.Assets[asset.Id];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_preview is not null) await _preview.ReleasePlaybackAsync();
    }

    [Fact]
    public void The_quality_bounds_every_video_on_the_fly()
    {
        var preview = Preview();
        Assert.Equal((int)PreviewQuality.Medium, preview.QualityIndex);
        Assert.Equal(new(1920, 1080), Decoding(_uhd).DecodeBound);
        Assert.Equal(new(1080, 1920), Decoding(_portrait).DecodeBound);    // the box upright for a portrait video

        Assert.Equal("1080p", preview.QualityShort);                        // the compact button
        preview.SetQualityCommand.Execute("3");                             // its menu: «Минимум (720p)»
        Assert.Equal("720p", preview.QualityShort);
        Assert.Equal(PreviewQuality.Low, _settings.Current.PreviewQuality);
        Assert.Equal(new(1280, 720), Decoding(_uhd).DecodeBound);
        Assert.Equal(new(720, 1280), Decoding(_portrait).DecodeBound);

        preview.QualityIndex = (int)PreviewQuality.High;
        Assert.Equal(new(2560, 1440), Decoding(_uhd).DecodeBound);

        preview.QualityIndex = (int)PreviewQuality.Original;
        Assert.Equal(new(3840, 2160), Decoding(_uhd).DecodeBound);           // the source's own size
        Assert.Equal(new(2160, 3840), Decoding(_portrait).DecodeBound);
    }

    [Fact]
    public void The_export_snapshot_never_takes_the_preview_bound() =>
        Assert.All(PlaybackSnapshotBuilder.Build(_projects.Current, 1).Assets.Values, a => Assert.Null(a.DecodeBound));

    private sealed class RecordingPlayback : IPlaybackService
    {
        public PlaybackSnapshot? Last { get; private set; }
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => true;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.FromSeconds(60);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) => Last = snapshot;
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(Position, 0, State, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
