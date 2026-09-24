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
/// Inspector audio section (Phase 7 Step 4) through the real shell wiring: Inspector →
/// SetClipProperties → undo/dirty → TimelineChanged → Inspector refresh and playback snapshot. The
/// playback service is real, with fake video/audio decoders and a fake audio device.
/// </summary>
public sealed class InspectorAudioTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeAudioOutput _output = new();
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;
    private int _historyChanges;

    public InspectorAudioTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_video, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, _audio, _output);
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

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private InspectorViewModel Inspector => _vm.Inspector;

    private MediaAsset Asset(string name, MediaKind kind, bool withAudio = true)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", name),
            Kind = kind,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = kind == MediaKind.Image
                ? new MediaMetadata { Width = 64, Height = 36 }
                : new MediaMetadata
                {
                    Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36,
                    AudioCodec = withAudio ? "aac" : null
                }
        };
        if (kind == MediaKind.Video) _video.Add(asset.FilePath, new FakeSource(Rate, 250));
        if (kind == MediaKind.Image) _video.Add(asset.FilePath, new FakeSource(Rate, 1));
        if (withAudio && kind != MediaKind.Image) _audio.Add(asset.FilePath, new FakeAudioSource(480_000));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    /// <summary>Adds through the timeline panel, which selects the new clip (→ Inspector).</summary>
    private Clip AddAndSelect(MediaAsset asset)
    {
        _vm.Timeline.AddMedia(asset);
        var clip = _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks)
            .SelectMany(t => t.Clips).Single(c => c is MediaBackedClip m && m.MediaAssetId == asset.Id);
        Assert.True(Inspector.IsTimelineClipSelected);
        return clip;
    }

    private void Select(Clip clip)
    {
        var clipVm = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
        _vm.Timeline.OnClipPressed(clipVm, toggle: false);
    }

    private int UndoSteps()
    {
        var steps = 0;
        while (_undo.CanUndo) { _undo.Undo(); steps++; }
        for (var i = 0; i < steps; i++) _undo.Redo();
        return steps;
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _vm.Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    // --- Showing ------------------------------------------------------------------------------

    [Fact]
    public void Audio_section_is_shown_for_clips_that_carry_sound()
    {
        var video = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));
        Assert.True(Inspector.HasAudioProperties);
        Assert.Equal((100m, false), (Inspector.VolumePercent, Inspector.IsMuted));

        AddAndSelect(Asset("m.wav", MediaKind.Audio));
        Assert.True(Inspector.HasAudioProperties);

        AddAndSelect(Asset("still.png", MediaKind.Image));
        Assert.False(Inspector.HasAudioProperties);

        AddAndSelect(Asset("silent.mp4", MediaKind.Video, withAudio: false));
        Assert.False(Inspector.HasAudioProperties);

        Select(video);
        Assert.True(Inspector.HasAudioProperties);
        _vm.MediaBrowser.SelectedItem = _vm.MediaBrowser.Items[0];
        Assert.False(Inspector.HasAudioProperties);
    }

    [Fact]
    public void Showing_a_clip_reflects_the_model_without_editing_it()
    {
        var clip = (AudioClip)AddAndSelect(Asset("m.wav", MediaKind.Audio));
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(0.25, true) }).Success);
        var steps = UndoSteps();
        var changes = _historyChanges;

        Select(clip);

        Assert.Equal((25m, true), (Inspector.VolumePercent, Inspector.IsMuted));
        Assert.Equal(changes, _historyChanges); // showing produced no command
        Assert.Equal(steps, UndoSteps());
    }

    [Fact]
    public void Showing_a_value_the_field_cant_hold_exactly_never_rewrites_the_model()
    {
        // 1/3 → 33.3333333333333 % → 0.333333333333333: an echo of the displayed value would
        // silently change the clip (and add an undo step) just by selecting it.
        var a = (VideoClip)AddAndSelect(Asset("a.mp4", MediaKind.Video));
        var b = (AudioClip)AddAndSelect(Asset("b.wav", MediaKind.Audio));
        Assert.True(_edit.SetClipProperties(a.Id, new ClipPropertyChange { Audio = new AudioProperties(1.0 / 3.0, false) }).Success);
        var changes = _historyChanges;

        Select(a);
        Select(b);
        Select(a);
        _undo.Undo();
        _undo.Redo();

        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0 / 3.0), BitConverter.DoubleToInt64Bits(a.Volume));
        Assert.Equal(changes + 2, _historyChanges);   // only the undo and redo themselves
        Assert.True(_undo.CanUndo);
        Assert.False(_undo.CanRedo);
        Assert.NotEqual(100m, Inspector.VolumePercent);
    }

    // --- Editing ------------------------------------------------------------------------------

    [Fact]
    public void Volume_edit_goes_through_the_edit_service_and_is_one_undo_step()
    {
        var clip = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));
        var timelineChanges = 0;
        _projects.TimelineChanged += (_, _) => timelineChanges++;
        var changes = _historyChanges;

        Inspector.VolumePercent = 50;

        Assert.Equal(0.5, clip.Volume);
        Assert.False(clip.IsMuted);
        Assert.Equal(1, timelineChanges);                 // one edit, no echo from the Inspector refresh
        Assert.Equal(changes + 1, _historyChanges);
        Assert.True(_projects.Current.IsDirty);
        Assert.EndsWith("* — AI Video Editor", _vm.Title);

        _undo.Undo();                                     // the volume step…
        Assert.Equal(1.0, clip.Volume);
        _undo.Undo();                                     // …and the Add: nothing else was recorded
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void Spinning_the_volume_merges_into_one_step_and_undo_redo_update_the_field()
    {
        var clip = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));

        for (var percent = 99; percent >= 90; percent--)
            Inspector.VolumePercent = percent;

        Assert.Equal(0.9, clip.Volume);

        _undo.Undo();                                    // one step undoes all ten changes
        Assert.Equal(1.0, clip.Volume);
        Assert.Equal(100m, Inspector.VolumePercent);    // refreshed from the model…
        Assert.True(_undo.CanRedo);                      // …without producing an edit (that would clear redo)

        _undo.Redo();
        Assert.Equal(90m, Inspector.VolumePercent);
    }

    [Fact]
    public void Mute_is_separate_from_volume_and_unmute_restores_it()
    {
        var clip = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));
        Inspector.VolumePercent = 40;

        Inspector.IsMuted = true;
        Assert.Equal((0.4, true), (clip.Volume, clip.IsMuted));
        Assert.Equal(40m, Inspector.VolumePercent);

        Inspector.IsMuted = false;
        Assert.Equal((0.4, false), (clip.Volume, clip.IsMuted));

        Inspector.VolumePercent = 0;                     // silent, but not muted
        Assert.Equal((0.0, false), (clip.Volume, clip.IsMuted));
        Assert.False(Inspector.IsMuted);
    }

    [Fact]
    public void Mute_applies_to_audio_clips_too()
    {
        var clip = (AudioClip)AddAndSelect(Asset("m.wav", MediaKind.Audio));

        Inspector.IsMuted = true;
        Assert.True(clip.IsMuted);
        _undo.Undo();
        Assert.False(clip.IsMuted);
        Assert.False(Inspector.IsMuted);
    }

    [Fact]
    public void Rejected_edit_reports_and_shows_the_model_again()
    {
        var clip = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));
        _projects.Current.Timeline.VideoTracks[0].IsLocked = true;
        var changes = _historyChanges;

        Inspector.VolumePercent = 30;

        Assert.Equal(1.0, clip.Volume);
        Assert.Equal(100m, Inspector.VolumePercent);
        Assert.Contains("locked", _status.Message);
        Assert.Equal(changes, _historyChanges);

        Inspector.IsMuted = true;
        Assert.False(clip.IsMuted);
        Assert.False(Inspector.IsMuted);
        Assert.Equal(changes, _historyChanges);
    }

    [Fact]
    public void Edits_follow_the_selected_clip()
    {
        var a = (VideoClip)AddAndSelect(Asset("a.mp4", MediaKind.Video));
        var b = (AudioClip)AddAndSelect(Asset("b.wav", MediaKind.Audio));

        Inspector.VolumePercent = 70;
        Select(a);
        Assert.Equal(100m, Inspector.VolumePercent);
        Inspector.IsMuted = true;

        Assert.Equal((1.0, true), (a.Volume, a.IsMuted));
        Assert.Equal((0.7, false), (b.Volume, b.IsMuted));
    }

    [Fact]
    public async Task Saved_volume_and_mute_come_back_after_reopening()
    {
        var clip = (VideoClip)AddAndSelect(Asset("v.mp4", MediaKind.Video));
        Inspector.VolumePercent = 150;
        Inspector.IsMuted = true;
        var folder = Path.Combine(_root, "Saved");
        await _projects.SaveAsAsync(folder);
        Assert.False(_projects.Current.IsDirty);

        await _projects.OpenAsync(folder);
        var reopened = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips.Single(c => c.Id == clip.Id);
        Select(reopened);

        Assert.Equal((150m, true), (Inspector.VolumePercent, Inspector.IsMuted));
        Assert.False(_projects.Current.IsDirty);
    }

    // --- Playback --------------------------------------------------------------------------------

    [Fact]
    public async Task Volume_and_mute_while_playing_reopen_no_decoder_and_never_buffer()
    {
        var music = AddAndSelect(Asset("m.wav", MediaKind.Audio));
        var video = AddAndSelect(Asset("v.mp4", MediaKind.Video));
        _vm.Preview.PlayPauseCommand.Execute(null);
        await TickUntil(() => _vm.Preview.Layers.Any(l => l.State == LayerPictureState.Frame) && !_vm.Preview.IsBuffering && _audio.Requests.Count == 2,
            "playback did not start");
        await _playback.RetiringSettledAsync(); // pipelines replaced while adding clips can't open late any more
        var videoOpens = _video.Requests.Count;
        var audioOpens = _audio.Requests.Count;

        Inspector.VolumePercent = 80;
        Inspector.IsMuted = true;
        Select(music);
        Inspector.VolumePercent = 20;
        Inspector.IsMuted = true;
        Inspector.IsMuted = false;
        Select(video);
        Inspector.IsMuted = false;

        for (var i = 0; i < 20; i++)
        {
            _vm.Preview.Tick();
            Assert.False(_vm.Preview.IsBuffering);
            _output.Play(480);
        }
        Assert.True(_vm.Preview.IsPlaying);
        Assert.Equal(videoOpens, _video.Requests.Count);
        Assert.Equal(audioOpens, _audio.Requests.Count);
        Assert.Equal((0, 1), (_output.StopCount, _output.StartCount));
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
