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
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 16 Steps 16.4–16.6 (D031): the Source mode of the Preview on the real shell and the real playback service (fake
/// decoder, manual clock; ticks driven like the view's timer). Source plays one asset through the same playback service;
/// the timeline playhead never follows it, and Timeline mode shows the playhead's frame again on the way back (SQ2). The
/// keys act on the source by mode (SQ4); the source In / Out is session state (SQ3); Insert / Overwrite place the range
/// at the playhead on the target track (SQ5, SQ8, SQ10, SQ16).
/// </summary>
public sealed class SourceViewerTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly InOutRangeService _inOut;
    private readonly SourceViewerService _source;
    private readonly MainWindowViewModel _vm;

    public SourceViewerTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        _inOut = new InOutRangeService(_projects);
        _source = new SourceViewerService(_projects, _edit);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance, _inOut, _source),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock, inOut: _inOut, source: _source),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private TimelineViewModel Timeline => _vm.Timeline;
    private PreviewViewModel Preview => _vm.Preview;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Video(int frames, string name = "v.mp4", bool sound = false)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), name),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = F(frames), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36,
                AudioCodec = sound ? "aac" : null, AudioSampleRate = sound ? 48000 : null
            }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private MediaAsset Audio(double seconds)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), "a.wav"),
            Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), AudioCodec = "pcm_s16le", AudioSampleRate = 48000 }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}: playhead {Timeline.Playhead}, playback {_playback.Position}, source mode {Preview.IsSourceMode}, layers "
                    + $"[{string.Join(", ", Preview.Layers.Select(l => l.Layer.GetType().Name + " " + l.State))}], buffering {Preview.IsBuffering}");
            await Task.Delay(1);
        }
    }

    private bool Settled => !Preview.IsBuffering && Preview.AreLayersCurrent;
    private int? Shown => Preview.Layers.FirstOrDefault(l => l.Layer is PictureLayer && l.State == LayerPictureState.Frame) is { } l
        ? FakeVideoDecoder.Number(l.Frame!) : null;

    private void Key(Key key, KeyModifiers modifiers = KeyModifiers.None) => ShortcutRouter.Handle(_vm, key, modifiers, null, null);

    /// <summary>V1: a 250-frame video from 0; the playhead at frame 50; settled.</summary>
    private async Task<MediaAsset> TimelineWithPlayheadAt50()
    {
        var timelineVideo = Video(250, "timeline.mp4");
        Assert.True(_edit.AddClip(timelineVideo.Id).Success);
        await TickUntil(() => Settled, "start");
        Timeline.SetPlayhead(F(50));
        await TickUntil(() => Settled && Shown == 50, "timeline at 50");
        return timelineVideo;
    }

    // --- 16.4: the source playback (SQ2, SQ3, SQ11, SQ12, SQ14) ----------------------------------------------------------

    [Fact]
    public async Task Source_plays_the_asset_and_the_timeline_playhead_never_follows_it()
    {
        await TimelineWithPlayheadAt50();
        var source = Video(100, "source.mp4");
        var top = _undo.CurrentPosition;

        Assert.Null(Preview.OpenSource(source));

        Assert.True(Preview.IsSourceMode);
        await TickUntil(() => Settled && Shown == 0, "the source's first frame");
        Assert.Equal("00:00:00:00", Preview.CurrentTimeDisplay);
        Assert.Equal("00:00:04:00", Preview.DurationDisplay);       // 100 frames at 25 fps
        Assert.Equal(new FrameSize(64, 36), Preview.Canvas);

        Preview.PlayPauseCommand.Execute(null);
        _clock.Advance(F(30) - MediaTime.Zero);
        await TickUntil(() => Preview.SourceFrame >= 30, "the source did not play");
        Preview.PlayPauseCommand.Execute(null);
        Assert.Equal(F(50), Timeline.Playhead);                     // SQ2
        Key(Avalonia.Input.Key.I);
        Assert.Same(top, _undo.CurrentPosition);                     // SQ3: viewing and the source range are no edit
    }

    [Fact]
    public async Task Timeline_mode_shows_the_playhead_frame_again_with_the_edits_made_meanwhile()
    {
        await TimelineWithPlayheadAt50();
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        Preview.SeekSource(80);
        await TickUntil(() => Settled && Shown == 80, "source at 80");

        Timeline.SetPlayhead(F(60));                                // a timeline seek in Source: not to playback
        Timeline.AddTextCommand.Execute(null);                       // a timeline edit in Source: applied on the way back
        await TickUntil(() => Settled, "after the edits");
        Assert.Equal(80, Shown);
        Assert.Equal(F(80), _playback.Position);
        Assert.Equal(F(100), _playback.Duration);                   // still the source's snapshot, not the timeline's
        Assert.Equal(new FrameSize(64, 36), Preview.Canvas);

        Preview.ShowTimelineCommand.Execute(null);

        await TickUntil(() => Settled && Shown == 60 && Preview.Layers.Any(l => l.Layer is TextLayer), "the timeline frame at the playhead");
        Assert.False(Preview.IsSourceMode);
        Assert.Equal(F(60), Timeline.Playhead);
        Assert.Equal("00:00:02:10", Preview.CurrentTimeDisplay);

        Preview.ShowSourceCommand.Execute(null);                     // back to Source: where it was
        await TickUntil(() => Settled && Shown == 80, "the source again at 80");
    }

    [Fact]
    public async Task Loop_in_Source_loops_the_source_range()
    {
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        Preview.SeekSource(20);
        await TickUntil(() => Settled && Shown == 20, "at 20");
        Key(Avalonia.Input.Key.I);
        Preview.SeekSource(29);
        await TickUntil(() => Settled && Shown == 29, "at 29");
        Key(Avalonia.Input.Key.O);                                   // [20, 30)
        Assert.Equal((20L, 30L), _source.Frames);
        Assert.Equal(InOutRange.None, _inOut.Range);                 // not the timeline's range

        Preview.IsLooping = true;
        Preview.PlayPauseCommand.Execute(null);
        _clock.Advance(F(15) - MediaTime.Zero);
        await TickUntil(() => Preview.SourceFrame is >= 20 and < 30 && _playback.Position < F(29), "looped");
        Preview.PlayPauseCommand.Execute(null);
    }

    [Fact]
    public async Task Another_project_or_the_asset_leaving_the_project_closes_Source()
    {
        await TimelineWithPlayheadAt50();
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        Key(Avalonia.Input.Key.I);
        Assert.True(_source.Range.IsSet);

        Assert.True(_edit.RemoveMedia(source.Id).Success);
        Assert.False(Preview.IsSourceMode);
        Assert.Null(_source.Asset);
        await TickUntil(() => Settled && Shown == 50, "the timeline back at 50");

        var other = Video(100, "other.mp4");
        Assert.Null(Preview.OpenSource(other));
        _projects.CreateNew("Next");
        Assert.False(Preview.IsSourceMode);
        Assert.Null(_source.Asset);
        Assert.False(Preview.HasSource);
    }

    [Fact]
    public void Images_missing_and_unanalysed_assets_do_not_open()
    {
        var image = new MediaAsset { FilePath = @"C:\x\logo.png", Kind = MediaKind.Image, AnalysisStatus = MediaAnalysisStatus.Completed };
        var missing = Video(100, "gone.mp4");
        missing.IsMissing = true;
        var pending = new MediaAsset { FilePath = @"C:\x\pending.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Analyzing };
        _projects.AddMediaAssets(new[] { image, pending });

        Assert.Equal("Images have no source range: add them with Add to Timeline.", Preview.OpenSource(image));
        Assert.Equal("gone.mp4 is missing.", Preview.OpenSource(missing));
        Assert.Contains("still being analyzed", Preview.OpenSource(pending));
        Assert.False(Preview.IsSourceMode);
        Assert.False(Preview.HasSource);
    }

    [Fact]
    public async Task An_audio_asset_plays_without_a_picture()
    {
        var music = Audio(4);
        Assert.Null(Preview.OpenSource(music));
        await TickUntil(() => Settled, "settled");
        Assert.True(Preview.IsSourceMode);
        Assert.Empty(Preview.Layers.Where(l => l.Layer is PictureLayer));
        Assert.Equal("00:00:04:00", Preview.DurationDisplay);       // the provisional 30 fps grid: 120 frames
        Assert.Equal(120, Preview.SourceFrames);
    }

    [Fact]
    public void A_video_in_a_provisional_project_is_on_its_own_rate()
    {
        var source = Video(100, "source.mp4");
        Assert.False(_projects.Current.Settings.IsFrameRateLocked);   // 30 fps, provisional
        Assert.Null(Preview.OpenSource(source));
        Assert.Equal(Rate, _source.Grid!.Rate);                       // SQ12
        Assert.Equal(100, _source.Grid.Frames);
    }

    // --- 16.5: the keys by mode (SQ4) ------------------------------------------------------------------------------------

    [Fact]
    public async Task In_Source_the_transport_and_In_Out_keys_act_on_the_source_only()
    {
        await TimelineWithPlayheadAt50();
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        await TickUntil(() => Settled && Shown == 0, "source at 0");

        Key(Avalonia.Input.Key.Right);
        await TickUntil(() => Settled && Shown == 1, "→");
        Key(Avalonia.Input.Key.Right, KeyModifiers.Shift);
        await TickUntil(() => Settled && Shown == 26, "Shift+→");
        Key(Avalonia.Input.Key.I);
        Key(Avalonia.Input.Key.End);
        await TickUntil(() => Settled && Preview.SourceFrame == 100, "End");
        Key(Avalonia.Input.Key.O);
        Key(Avalonia.Input.Key.J);
        await TickUntil(() => Settled && Preview.SourceFrame == 75, "J");
        Key(Avalonia.Input.Key.Home);
        await TickUntil(() => Settled && Shown == 0, "Home");

        Assert.Equal((26L, 100L), _source.Frames);                    // Out after the last frame
        Assert.Equal(F(50), Timeline.Playhead);
        Assert.Equal(InOutRange.None, _inOut.Range);

        Preview.ShowTimelineCommand.Execute(null);
        Key(Avalonia.Input.Key.I);                                   // Timeline mode: the timeline's In again
        Assert.Equal(F(50), _inOut.Range.In);
        Key(Avalonia.Input.Key.Right);
        Assert.Equal(F(51), Timeline.Playhead);
    }

    [Fact]
    public void The_key_maps_by_mode()
    {
        Video(100, "source.mp4");
        var source = _projects.Current.MediaAssets[0];

        Assert.Same(Timeline.SetInCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.I, KeyModifiers.None));
        Assert.Same(Timeline.InsertFromSourceCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.OemComma, KeyModifiers.None));
        Assert.Same(Timeline.OverwriteFromSourceCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.OemPeriod, KeyModifiers.None));

        Assert.Null(Preview.OpenSource(source));
        Assert.Same(Preview.SetSourceInCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.I, KeyModifiers.None));
        Assert.Same(Preview.SetSourceOutCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.O, KeyModifiers.None));
        Assert.Same(Preview.SourceStepForwardCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.Right, KeyModifiers.None));
        Assert.Same(Preview.SourceStepBackwardSecondCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.J, KeyModifiers.None));
        Assert.Same(Preview.SourceGoToEndCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.End, KeyModifiers.None));
        Assert.Same(Preview.PlayPauseCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.Space, KeyModifiers.None));
        Assert.Same(Preview.PlayCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.L, KeyModifiers.None));
        Assert.Same(Preview.ToggleLoopCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.L, KeyModifiers.Control));
        Assert.Same(_vm.Toolbar.ImportMediaCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.I, KeyModifiers.Control));
        Assert.Same(Timeline.InsertFromSourceCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.OemComma, KeyModifiers.None));
        Assert.Same(Timeline.TrimEndToPlayheadCommand, ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.W, KeyModifiers.None));
        Assert.Null(ShortcutRouter.CommandFor(_vm, Avalonia.Input.Key.OemComma, KeyModifiers.Shift));
    }

    // --- 16.6: Insert / Overwrite from the UI (SQ5, SQ8, SQ10, SQ16) ----------------------------------------------------

    [Fact]
    public async Task Comma_inserts_the_source_range_at_the_playhead_and_moves_the_playhead_to_its_end()
    {
        await TimelineWithPlayheadAt50();
        var clip = _projects.Current.Timeline.VideoTracks[0].Clips[0];
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        Preview.SeekSource(10);
        await TickUntil(() => Settled && Shown == 10, "source at 10");
        Key(Avalonia.Input.Key.I);
        Preview.SeekSource(39);
        await TickUntil(() => Settled && Shown == 39, "source at 39");
        Key(Avalonia.Input.Key.O);                                   // [10, 40)

        Key(Avalonia.Input.Key.OemComma);

        var v1 = _projects.Current.Timeline.VideoTracks[0];
        var inserted = v1.Clips.Single(c => c is VideoClip v && v.MediaAssetId == source.Id);
        Assert.Equal((F(50), F(80)), (inserted.TimelineStart, inserted.TimelineEnd));
        Assert.Equal(F(10), ((VideoClip)inserted).SourceIn);
        Assert.Equal(F(80), Timeline.Playhead);                      // SQ10
        Assert.Equal(inserted.Id, Assert.Single(Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected)).Clip.Id);
        Assert.Equal(3, v1.Clips.Count);                             // split at 50, the right part moved
        Assert.Equal((MediaTime.Zero, F(50)), (clip.TimelineStart, clip.TimelineEnd));
        Assert.True(Preview.IsSourceMode);                           // Source stays shown
        Assert.Equal("Inserted source.mp4", _status.Message);

        _vm.Toolbar.UndoCommand.Execute(null);
        Assert.Single(v1.Clips);
        Assert.Equal(F(250), clip.TimelineEnd);
    }

    [Fact]
    public async Task The_target_is_the_track_of_the_latest_selected_clip_of_the_source_kind()
    {
        await TimelineWithPlayheadAt50();
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var v1 = _projects.Current.Timeline.VideoTracks[0];
        var v2 = _projects.Current.Timeline.VideoTracks[1];
        var music = Audio(8);
        Assert.True(_edit.AddClip(music.Id).Success);
        var onV2 = Video(100, "on-v2.mp4");
        Assert.True(_edit.AddClip(onV2.Id, v2.Id, F(0)).Success);
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));

        Timeline.OnClipPressed(Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip == v2.Clips[0]), toggle: false);
        Timeline.OnClipPressed(Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip is AudioClip), toggle: true);
        Timeline.SetPlayhead(F(200));
        Key(Avalonia.Input.Key.OemPeriod);                           // the primary is the audio clip: the latest video one is V2's
        Assert.Contains(v2.Clips, c => c is VideoClip v && v.MediaAssetId == source.Id);

        Timeline.ClearSelectionSilently();
        Timeline.SetPlayhead(F(300));
        Key(Avalonia.Input.Key.OemPeriod);                           // nothing selected: V1
        Assert.Contains(v1.Clips, c => c is VideoClip v && v.MediaAssetId == source.Id);

        Assert.Null(Preview.OpenSource(music));
        Timeline.SetPlayhead(F(400));
        Key(Avalonia.Input.Key.OemComma);                            // an audio source: A1
        Assert.Equal(2, _projects.Current.Timeline.AudioTracks[0].Clips.Count);
    }

    [Fact]
    public async Task Refused_on_a_locked_target_and_off_during_an_export()
    {
        await TimelineWithPlayheadAt50();
        var source = Video(100, "source.mp4");
        Assert.Null(Preview.OpenSource(source));
        Assert.True(Timeline.InsertFromSourceCommand.CanExecute(null));
        var before = _projects.Current.Timeline.VideoTracks[0].Clips.Count;

        using (_lock.Acquire())
        {
            Assert.False(Timeline.InsertFromSourceCommand.CanExecute(null));
            Assert.False(Timeline.OverwriteFromSourceCommand.CanExecute(null));
            Key(Avalonia.Input.Key.OemComma);
            Preview.SeekSource(5);                                   // viewing stays available (SQ16)
            await TickUntil(() => Settled && Shown == 5, "viewing during an export");
        }

        Assert.True(_edit.SetTrackLocked(_projects.Current.Timeline.VideoTracks[0].Id, true).Success);
        Key(Avalonia.Input.Key.OemComma);
        Assert.Equal("Track V1 is locked.", _status.Message);
        Assert.Equal(before, _projects.Current.Timeline.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public void Without_a_source_Insert_and_Overwrite_are_off()
    {
        Assert.False(Timeline.InsertFromSourceCommand.CanExecute(null));
        Assert.False(Timeline.OverwriteFromSourceCommand.CanExecute(null));
        Assert.Same(Timeline.InsertFromSourceCommand, Preview.InsertCommand);
        Assert.Same(Timeline.OverwriteFromSourceCommand, Preview.OverwriteCommand);
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
