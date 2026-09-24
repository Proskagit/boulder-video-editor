using System.Collections.Immutable;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 7 Step 7a: the Preview view model publishes the composition (<see cref="PreviewViewModel.Layers"/>,
/// <see cref="PreviewViewModel.Canvas"/>) through the real shell wiring and real playback service
/// (fake decoders, manual clock).
/// </summary>
public sealed class PreviewLayersTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private readonly CountingPlayback _playback;
    private readonly MainWindowViewModel _vm;

    public PreviewLayersTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        _playback = new CountingPlayback(_service);
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, status),
            new TimelineViewModel(_projects, _edit, status, NullLogger<TimelineViewModel>.Instance),
            status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private PreviewViewModel Preview => _vm.Preview;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>Forwards to the real service and counts <see cref="IPlaybackService.Update"/> calls.</summary>
    private sealed class CountingPlayback(IPlaybackService inner) : IPlaybackService
    {
        public int Updates;
        public PlaybackState State => inner.State;
        public bool IsBuffering => inner.IsBuffering;
        public bool IsAvailable => inner.IsAvailable;
        public bool IsAudioAvailable => inner.IsAudioAvailable;
        public MediaTime Position => inner.Position;
        public MediaTime Duration => inner.Duration;
        public event EventHandler? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) => inner.UpdateSnapshot(snapshot);
        public void Play() => inner.Play();
        public void Pause() => inner.Pause();
        public void Stop() => inner.Stop();
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => inner.SeekAsync(position, ct);
        public PlaybackFrame Update() { Updates++; return inner.Update(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (Clip Clip, MediaAsset Asset) Video(Track track, string name)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-preview-tests", Guid.NewGuid().ToString("N"), name),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = F(250), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, 250));
        _projects.AddMediaAssets(new[] { asset });
        var result = _edit.AddClip(asset.Id, track.Id, MediaTime.Zero);
        Assert.True(result.Success, result.Message);
        return (track.Clips.Single(c => c.Id == result.ClipIds[0]), asset);
    }

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];

    private Track V2()
    {
        if (_projects.Current.Timeline.VideoTracks.Count < 2) Assert.True(_edit.AddTrack(TrackType.Video).Success);
        return _projects.Current.Timeline.VideoTracks[1];
    }

    private void SetOpacity(Clip clip, double opacity) =>
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = VisualProperties.Of(clip)!.Value with { Opacity = opacity } }).Success);

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}: {string.Join(", ", Preview.Layers.Select(l => l.State))}");
            await Task.Delay(1);
        }
    }

    private bool Settled() => !Preview.IsBuffering && Preview.AreLayersCurrent && Preview.Layers.Length > 0;

    [Fact]
    public async Task Layers_and_canvas_reach_the_preview_bottom_to_top()
    {
        var (bottom, _) = Video(V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        SetOpacity(top, 0.5);

        await TickUntil(() => Settled() && Preview.Layers.Length == 2, "two layers");

        Assert.Equal(new[] { bottom.Id, top.Id }, Preview.Layers.Select(l => l.Layer.ClipId));
        Assert.All(Preview.Layers, l => Assert.Equal(LayerPictureState.Frame, l.State));
        Assert.Equal(new FrameSize(1920, 1080), Preview.Canvas);
    }

    [Fact]
    public async Task Canvas_follows_the_project_settings()
    {
        _projects.Current.Settings.FrameWidth = 1080;
        _projects.Current.Settings.FrameHeight = 1920;
        Video(V1, "a.mp4"); // a timeline change rebuilds the snapshot with the new canvas

        await TickUntil(Settled, "layer");

        Assert.Equal(new FrameSize(1080, 1920), Preview.Canvas);
        var geometry = ((PictureLayer)Preview.Layers[0].Layer).Geometry!;
        Assert.Equal(new FrameSize(1080, 1920), geometry.Canvas);
    }

    [Fact]
    public async Task Layer_uncovered_while_paused_keeps_the_preview_polling_until_it_has_its_frame()
    {
        var (bottom, bottomAsset) = Video(V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        await TickUntil(() => Settled() && Preview.Layers.Length == 1, "top only");
        var generation = _service.SeekGeneration;
        var pipeline = _service.VideoPipelineInstance;

        var gate = _decoder.Gate(bottomAsset.FilePath);
        SetOpacity(top, 0.9);
        Preview.Tick();

        Assert.Equal(new[] { bottom.Id, top.Id }, Preview.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(LayerPictureState.Pending, Preview.Layers[0].State);
        Assert.False(Preview.AreLayersCurrent);
        Assert.False(Preview.IsBuffering);

        var updates = _playback.Updates;
        for (var i = 0; i < 5; i++) Preview.Tick();
        Assert.Equal(updates + 5, _playback.Updates);         // still polling while a layer is pending

        gate.SetResult();
        await TickUntil(() => Preview.AreLayersCurrent, "lower layer ready");
        Assert.Equal(LayerPictureState.Frame, Preview.Layers[0].State);
        Assert.Same(pipeline, _service.VideoPipelineInstance);
        Assert.Equal(generation, _service.SeekGeneration);

        Preview.Tick();                                        // one more tick settles …
        updates = _playback.Updates;
        for (var i = 0; i < 5; i++) Preview.Tick();
        Assert.Equal(updates, _playback.Updates);              // … and then no work while paused
    }

    [Fact]
    public async Task Buffering_keeps_the_previous_layers()
    {
        var (_, asset) = Video(V1, "a.mp4");
        await TickUntil(Settled, "initial");
        var before = Preview.Layers;

        var gate = _decoder.Gate(asset.FilePath);
        _vm.Timeline.SetPlayhead(F(100));  // user seek
        Preview.Tick();

        Assert.True(Preview.IsBuffering);
        Assert.Equal(before, Preview.Layers);                 // nothing flashes while decoding the new position
        gate.SetResult();
        await TickUntil(Settled, "after seek");
        Assert.NotEqual(before, Preview.Layers);
    }

    [Fact]
    public async Task Project_change_clears_the_layers()
    {
        Video(V1, "a.mp4");
        await TickUntil(Settled, "initial");

        _projects.CreateNew("Other");

        Assert.Empty(Preview.Layers);
        Assert.Equal(new FrameSize(1920, 1080), Preview.Canvas);
        await TickUntil(() => !Preview.IsBuffering, "new project");
        Assert.Empty(Preview.Layers);
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
