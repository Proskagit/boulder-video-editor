using System.Diagnostics;
using AiVideoEditor.Core.Common;
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
/// Phase 7 Step 7d: Inspector transform / opacity / crop through the real shell wiring —
/// SetClipProperties per field, merged undo steps, no echo edits, rejection — and the playback
/// regression: presentation edits while playing keep the video pipeline and seek generation and
/// never buffer, while the preview's layers follow.
/// </summary>
public sealed class InspectorVisualTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;
    private int _historyChanges;

    public InspectorVisualTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
        _undo.StateChanged += (_, _) => _historyChanges++;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private InspectorViewModel Inspector => _vm.Inspector;
    private PreviewViewModel Preview => _vm.Preview;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(string name, MediaKind kind)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-inspector-visual", Guid.NewGuid().ToString("N"), name),
            Kind = kind,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = F(250), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080,
                AudioCodec = kind == MediaKind.Audio ? "aac" : null
            }
        };
        if (kind != MediaKind.Audio) _decoder.Add(asset.FilePath, new FakeSource(Rate, kind == MediaKind.Image ? 1 : 250));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private Clip Add(MediaAsset asset, Track? track = null)
    {
        var result = _edit.AddClip(asset.Id, track?.Id, track is null ? null : MediaTime.Zero);
        Assert.True(result.Success, result.Message);
        return _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks)
            .SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
    }

    private void Select(Clip clip)
    {
        var vm = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
        _vm.Timeline.OnClipPressed(vm, toggle: false);
    }

    private Track V2()
    {
        if (_projects.Current.Timeline.VideoTracks.Count < 2) Assert.True(_edit.AddTrack(TrackType.Video).Success);
        return _projects.Current.Timeline.VideoTracks[1];
    }

    private VideoClip SelectedVideo()
    {
        var clip = (VideoClip)Add(Asset("v.mp4", MediaKind.Video));
        Select(clip);
        return clip;
    }

    // --- Showing ---------------------------------------------------------------------------------------

    [Fact]
    public void Sections_follow_the_clip_kind()
    {
        var video = SelectedVideo();
        Assert.True(Inspector.HasVisualProperties);
        Assert.True(Inspector.HasCrop);
        Assert.Equal((0m, 0m, 100m, 0m, 100m), (Inspector.PositionX, Inspector.PositionY, Inspector.ScalePercent, Inspector.Rotation, Inspector.OpacityPercent));

        var image = Add(Asset("i.png", MediaKind.Image));
        Select(image);
        Assert.True(Inspector.HasVisualProperties);
        Assert.True(Inspector.HasCrop);

        var audio = Add(Asset("a.wav", MediaKind.Audio));
        Select(audio);
        Assert.False(Inspector.HasVisualProperties);
        Assert.False(Inspector.HasCrop);

        var text = new TextClip { Text = "Title", TimelineStart = F(300), Duration = F(25) };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(text);
        _projects.NotifyTimelineChanged();
        Select(text);
        Assert.True(Inspector.HasVisualProperties);
        Assert.False(Inspector.HasCrop);                     // text has no crop

        Select(video);
        Assert.True(Inspector.HasCrop);
    }

    [Fact]
    public void Showing_values_the_fields_cant_hold_exactly_never_rewrites_the_model()
    {
        var clip = SelectedVideo();
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = new VisualProperties(1.0 / 3, -2.0 / 3, 1.0 / 3, 1.0 / 7, 1.0 / 3, new CropRect(0.1 / 3, 0, 0, 0))
        }).Success);
        var bits = BitConverter.DoubleToInt64Bits(clip.PositionX);
        var changes = _historyChanges;

        var other = Add(Asset("o.mp4", MediaKind.Video));
        Select(other);
        Select(clip);
        _undo.Undo(); _undo.Undo(); _undo.Redo(); _undo.Redo();

        Assert.Equal(changes + 4 + 1, _historyChanges); // the add, then only the undos/redos themselves
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(clip.PositionX));
        Assert.False(_undo.CanRedo);
    }

    // --- Editing ------------------------------------------------------------------------------------------

    [Fact]
    public void Each_field_sets_exactly_its_own_value()
    {
        var clip = SelectedVideo();

        Inspector.PositionX = 100;
        Inspector.PositionY = -50.5m;
        Inspector.ScalePercent = 50;
        Inspector.Rotation = 45;
        Inspector.OpacityPercent = 25;
        Inspector.CropLeftPercent = 10;
        Inspector.CropTopPercent = 20;
        Inspector.CropRightPercent = 30;
        Inspector.CropBottomPercent = 40;

        Assert.Equal(new VisualProperties(100, -50.5, 0.5, 45, 0.25, new CropRect(0.1, 0.2, 0.3, 0.4)), VisualProperties.Of(clip));
        Assert.True(_projects.Current.IsDirty);
    }

    [Fact]
    public void Spinning_one_field_is_one_undo_step_and_undo_redo_refresh_the_fields_without_edits()
    {
        var clip = SelectedVideo();

        for (var percent = 99; percent >= 80; percent--)
            Inspector.OpacityPercent = percent;
        Assert.Equal(0.8, clip.Opacity);
        Inspector.ScalePercent = 150;                          // another field: a new step

        _undo.Undo();                                          // the scale step
        Assert.Equal(100m, Inspector.ScalePercent);
        Assert.Equal(0.8, clip.Opacity);
        _undo.Undo();                                          // one step undoes all twenty opacity spins
        Assert.Equal(100m, Inspector.OpacityPercent);
        Assert.Equal(1.0, clip.Opacity);
        Assert.True(_undo.CanUndo);                            // only the Add is left
        Assert.True(_undo.CanRedo);                            // refreshing the fields produced no edit

        _undo.Redo();
        Assert.Equal(80m, Inspector.OpacityPercent);
        _undo.Redo();
        Assert.Equal(150m, Inspector.ScalePercent);
        Assert.False(_undo.CanRedo);
    }

    [Fact]
    public void Crop_that_leaves_nothing_is_rejected_and_the_field_shows_the_model_again()
    {
        var clip = SelectedVideo();
        Inspector.CropLeftPercent = 60;
        var changes = _historyChanges;

        Inspector.CropRightPercent = 50;                       // 60 % + 50 % ≥ 100 %

        Assert.Equal(0.0, clip.Crop.Right);
        Assert.Equal(0m, Inspector.CropRightPercent);
        Assert.Contains("Crop", _status.Message);
        Assert.Equal(changes, _historyChanges);
    }

    [Fact]
    public void Locked_track_rejects_and_restores()
    {
        var clip = SelectedVideo();
        _projects.Current.Timeline.VideoTracks[0].IsLocked = true;

        Inspector.Rotation = 90;

        Assert.Equal(0.0, clip.RotationDegrees);
        Assert.Equal(0m, Inspector.Rotation);
        Assert.Contains("locked", _status.Message);
    }

    // --- Playback: presentation-only edits ----------------------------------------------------------------

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}: {string.Join(", ", Preview.Layers.Select(l => l.State))} buffering={Preview.IsBuffering}");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task Inspector_edits_while_playing_keep_the_pipeline_and_seek_generation_and_never_buffer()
    {
        var bottomAsset = Asset("bottom.mp4", MediaKind.Video);
        var bottom = Add(bottomAsset);
        var top = Add(Asset("top.mp4", MediaKind.Video), V2());
        Select(top);
        Preview.PlayPauseCommand.Execute(null);
        await TickUntil(() => !Preview.IsBuffering && Preview.AreLayersCurrent && Preview.Layers.Length == 1, "playing");
        var pipeline = _playback.VideoPipelineInstance;
        var generation = _playback.SeekGeneration;
        var gate = _decoder.Gate(bottomAsset.FilePath);

        Inspector.OpacityPercent = 90;                         // uncovers the bottom layer
        Preview.Tick();

        Assert.False(Preview.IsBuffering);
        Assert.Equal(new[] { bottom.Id, top.Id }, Preview.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(LayerPictureState.Pending, Preview.Layers[0].State);
        Assert.Equal(0.9, Preview.Layers[1].Layer.Opacity);

        gate.SetResult();
        for (var i = 0; i < 10; i++)
        {
            Inspector.ScalePercent = 90 - i;                    // spin while playing
            Inspector.Rotation = i;
            _clock.Advance(0.04);
            Preview.Tick();
            Assert.False(Preview.IsBuffering);
        }
        await TickUntil(() => Preview.AreLayersCurrent && Preview.Layers.Length == 2, "both layers");

        Assert.Same(pipeline, _playback.VideoPipelineInstance);
        Assert.Equal(generation, _playback.SeekGeneration);
        Assert.Equal(0.81, ((VideoClip)top).Scale, 12);
        Assert.Equal(9.0, Preview.Layers[1].Layer is Core.Composition.PictureLayer { Geometry: { } g } ? g.RotationDegrees : double.NaN);
        Assert.True(Preview.IsPlaying);

        Inspector.ScalePercent = 100;                          // full canvas again …
        Inspector.Rotation = 0;
        Inspector.OpacityPercent = 100;                        // … and opaque: it covers the bottom, whose reader closes
        await TickUntil(() => Preview.Layers.Length == 1 && Preview.AreLayersCurrent, "top only");
        Assert.Same(pipeline, _playback.VideoPipelineInstance);
        Assert.Equal(generation, _playback.SeekGeneration);
        await Eventually(() => _decoder.LiveStreams == 1, "the bottom stream is still open");
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
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
