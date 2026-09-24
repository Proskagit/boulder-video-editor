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
/// Phase 7 Step 8 through the real shell wiring: "+ Text" (playhead, topmost video track, selection,
/// preview layer, rejection), the Inspector TEXT section and the timeline label of text clips.
/// </summary>
public sealed class TextClipUiTests : IAsyncLifetime
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

    public TextClipUiTests()
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
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private TimelineViewModel Timeline => _vm.Timeline;
    private InspectorViewModel Inspector => _vm.Inspector;
    private PreviewViewModel Preview => _vm.Preview;
    private Sequence Sequence => _projects.Current.Timeline;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Video(string name)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-text-ui", Guid.NewGuid().ToString("N"), name),
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
        return asset;
    }

    private TimelineClipViewModel ViewOf(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    /// <summary>"+ Text" at <paramref name="frame"/>; returns the new clip.</summary>
    private TextClip AddText(long frame = 0)
    {
        Sequence.PlayheadPosition = F(frame);
        var before = Sequence.VideoTracks.SelectMany(t => t.Clips).Select(c => c.Id).ToHashSet();
        Timeline.AddTextCommand.Execute(null);
        return Sequence.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single(c => !before.Contains(c.Id));
    }

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

    // --- "+ Text" --------------------------------------------------------------------------------------

    [Fact]
    public void Add_text_puts_a_selected_text_clip_at_the_playhead_on_the_topmost_video_track()
    {
        Assert.True(_edit.AddClip(Video("v.mp4").Id).Success);   // V1: frames 0–250
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var top = Sequence.VideoTracks.OrderByDescending(t => t.Order).First();

        var clip = AddText(40);

        Assert.Contains(clip, top.Clips);
        Assert.Equal(F(40), clip.TimelineStart);
        Assert.Equal(F(125), clip.Duration);                    // 5 s at 25 fps
        Assert.True(ViewOf(clip).IsSelected);
        Assert.Single(Timeline.Tracks.SelectMany(t => t.Clips), c => c.IsSelected);
        Assert.Equal(InspectorSelectionKind.TimelineClip, Inspector.SelectionKind);
        Assert.Equal("Text clip", Inspector.ClipTypeLabel);
        Assert.True(Inspector.HasVisualProperties);
        Assert.False(Inspector.HasCrop);
        Assert.False(Inspector.HasAudioProperties);
        Assert.Equal("Text added", _status.Message);
    }

    [Fact]
    public void A_rejected_add_reports_why_and_keeps_the_selection()
    {
        var video = _edit.AddClip(Video("v.mp4").Id).ClipIds[0];  // V1 is also the topmost track
        var clip = Sequence.VideoTracks[0].Clips.Single(c => c.Id == video);
        Timeline.OnClipPressed(ViewOf(clip), toggle: false);
        var clips = Sequence.VideoTracks[0].Clips.Count;

        Sequence.PlayheadPosition = F(100);
        Timeline.AddTextCommand.Execute(null);

        Assert.Equal(clips, Sequence.VideoTracks[0].Clips.Count);
        Assert.Contains("overlap", _status.Message);
        Assert.True(ViewOf(clip).IsSelected);
        Assert.Single(Sequence.VideoTracks);                    // no track was created
    }

    [Fact]
    public void Undo_removes_the_added_text_and_redo_brings_the_same_clip_back()
    {
        var clip = AddText(10);

        _undo.Undo();
        Assert.DoesNotContain(clip, Sequence.VideoTracks[0].Clips);
        Assert.Empty(Timeline.Tracks.SelectMany(t => t.Clips));
        Assert.Equal(InspectorSelectionKind.None, Inspector.SelectionKind);

        _undo.Redo();
        Assert.Contains(clip, Sequence.VideoTracks[0].Clips);
        Assert.Equal("Text", ViewOf(clip).Name);
    }

    [Fact]
    public async Task The_added_text_is_a_text_layer_in_the_preview()
    {
        Assert.True(_edit.AddClip(Video("v.mp4").Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);

        var clip = AddText(0);

        await TickUntil(() => Preview.AreLayersCurrent && Preview.Layers.Length == 2, "video + text");
        var layer = Preview.Layers[1];
        Assert.Equal(clip.Id, layer.Layer.ClipId);
        Assert.Equal(LayerPictureState.Text, layer.State);
    }

    // --- Inspector TEXT section -------------------------------------------------------------------------

    /// <summary>Number of undo steps; the history and the selection are left as they were (undoing
    /// the add deselects the clip, so it is selected again afterwards).</summary>
    private int UndoDepth()
    {
        var selected = Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Clip).ToList();
        var depth = 0;
        while (_undo.CanUndo) { _undo.Undo(); depth++; }
        for (var i = 0; i < depth; i++) _undo.Redo();
        if (selected.Count == 1) Timeline.OnClipPressed(ViewOf(selected[0]), toggle: false);
        return depth;
    }

    [Fact]
    public void The_text_section_shows_the_model_and_only_for_text_clips()
    {
        var video = _edit.AddClip(Video("v.mp4").Id).ClipIds[0];
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var text = AddText(0);
        Assert.True(_edit.SetClipProperties(text.Id, new ClipPropertyChange
        {
            Text = new TextProperties("One\nTwo", "Segoe UI", 64.5, "#ff8800", TextAlignment.Right)
        }).Success);

        Assert.True(Inspector.HasTextProperties);
        Assert.Equal("One\nTwo", Inspector.TextContent);
        Assert.Equal("Segoe UI", Inspector.FontFamilyName);
        Assert.Contains("Segoe UI", Inspector.FontFamilies);
        Assert.Equal(64.5m, Inspector.FontSize);
        Assert.Equal("#ff8800", Inspector.TextColorHex);
        Assert.Equal("#ff8800", Inspector.TextColorSwatch);
        Assert.Equal(TextAlignment.Right, Inspector.Alignment);

        Timeline.OnClipPressed(ViewOf(Sequence.VideoTracks[0].Clips.Single(c => c.Id == video)), toggle: false);
        Assert.False(Inspector.HasTextProperties);
        Assert.True(Inspector.HasCrop);
    }

    [Fact]
    public void Every_field_edits_only_its_own_property()
    {
        var clip = AddText(0);
        var visual = VisualProperties.Of(clip);

        Inspector.TextContent = "Line 1\r\nLine 2";
        Inspector.FontFamilyName = "Arial";
        Inspector.FontSize = 72;
        Inspector.TextColorHex = "#00Ff7a";
        Inspector.Alignment = TextAlignment.Left;

        Assert.Equal(new TextProperties("Line 1\r\nLine 2", "Arial", 72, "#00Ff7a", TextAlignment.Left), TextProperties.Of(clip));
        Assert.Equal(visual, VisualProperties.Of(clip));
        Assert.Equal(F(0), clip.TimelineStart);
        Assert.Equal(F(125), clip.Duration);
        Assert.Equal("#00Ff7a", Inspector.TextColorSwatch);
        Assert.Equal(6, UndoDepth());                           // add + one step per field
    }

    [Fact]
    public void Typing_merges_into_one_undo_step_and_undo_redo_refresh_the_fields_without_edits()
    {
        var clip = AddText(0);
        foreach (var typed in new[] { "H", "He", "Hel", "Hell", "Hello" })
            Inspector.TextContent = typed;
        Inspector.FontSize = 49;
        Inspector.FontSize = 50;                                // same field again: merged
        Assert.Equal(3, UndoDepth());                           // add, text, size

        _undo.Undo();                                           // size
        Assert.Equal(48m, Inspector.FontSize);
        Assert.Equal("Hello", Inspector.TextContent);
        _undo.Undo();                                           // text
        Assert.Equal("Text", Inspector.TextContent);
        Assert.Equal("Text", clip.Text);
        Assert.True(_undo.CanRedo);                             // showing the model made no edit

        _undo.Redo();
        _undo.Redo();
        Assert.Equal("Hello", Inspector.TextContent);
        Assert.Equal(50m, Inspector.FontSize);
        Assert.Equal(50, clip.FontSize);
        Assert.False(_undo.CanRedo);
    }

    [Fact]
    public void Other_timeline_changes_refresh_the_fields_without_an_edit()
    {
        var clip = AddText(0);
        Inspector.TextContent = "Title";
        var depth = UndoDepth();

        Assert.True(_edit.MoveClips(new[] { clip.Id }, 10).Success);     // refresh → SyncFromModel
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Text = TextProperties.Of(clip)!.Value with { FontSize = 100.0 / 3.0 } // not exact as decimal
        }).Success);

        Assert.Equal(depth + 2, UndoDepth());
        Assert.Equal(100.0 / 3.0, clip.FontSize);           // the field showing it made no edit
    }

    [Fact]
    public void An_incomplete_or_invalid_color_is_not_applied_and_the_field_shows_the_model_on_blur()
    {
        var clip = AddText(0);
        var depth = UndoDepth();

        foreach (var typed in new[] { "#", "#F", "#FF8", "#FF880" })
            Inspector.TextColorHex = typed;
        Assert.Equal("#FFFFFF", clip.ColorHex);                 // nothing applied while typing
        Assert.Equal("#FF880", Inspector.TextColorHex);         // and nothing reverted either
        Assert.Equal("#FFFFFF", Inspector.TextColorSwatch);
        Assert.Equal(depth, UndoDepth());

        Inspector.TextColorHex = "#GG0000";                     // 7 characters, not hex
        Inspector.TextColorHex = "FF8800 ";
        Assert.Equal("#FFFFFF", clip.ColorHex);

        Inspector.ShowModelValues();                            // the field lost focus
        Assert.Equal("#FFFFFF", Inspector.TextColorHex);

        Inspector.TextColorHex = "#FF8800";
        Assert.Equal("#FF8800", clip.ColorHex);
        Assert.Equal("#FF8800", Inspector.TextColorSwatch);
        Assert.Equal(depth + 1, UndoDepth());
    }

    [Fact]
    public void Rejected_or_empty_values_leave_the_model_and_show_it_again()
    {
        var clip = AddText(0);

        Inspector.FontSize = 1001;                              // above ClipPropertyLimits.MaxFontSize
        Assert.Equal(48, clip.FontSize);
        Assert.Equal(48m, Inspector.FontSize);
        Assert.Contains("Font size", _status.Message);

        Inspector.FontSize = null;                              // emptied field: no edit …
        Assert.Equal(48, clip.FontSize);
        Inspector.ShowModelValues();                            // … and the model on blur
        Assert.Equal(48m, Inspector.FontSize);

        Inspector.TextContent = new string('x', ClipPropertyLimits.MaxTextLength + 1);
        Assert.Equal("Text", clip.Text);
        Assert.Equal("Text", Inspector.TextContent);

        Inspector.FontFamilyName = null;                        // the font list lost its selection
        Assert.Equal("Segoe UI", clip.FontFamily);

        Sequence.VideoTracks[0].IsLocked = true;
        Inspector.TextContent = "locked";
        Assert.Equal("Text", clip.Text);
        Assert.Equal("Text", Inspector.TextContent);
        Assert.Contains("locked", _status.Message);
    }

    [Fact]
    public async Task Empty_or_whitespace_text_is_a_valid_value_that_draws_no_layer()
    {
        var clip = AddText(0);
        await TickUntil(() => Preview.AreLayersCurrent && Preview.Layers.Length == 1, "text layer");

        Inspector.TextContent = "  \n ";
        Assert.Equal("  \n ", clip.Text);
        await TickUntil(() => Preview.AreLayersCurrent && Preview.Layers.Length == 0, "no layer");

        Inspector.TextContent = "";
        Assert.Equal("", clip.Text);
        Assert.Equal("", Inspector.TextContent);
    }

    [Fact]
    public void The_font_list_is_the_installed_fonts_plus_the_clip_font_when_it_is_missing()
    {
        var inspector = new InspectorViewModel(_edit, _status, new Fonts("Arial", "Segoe UI", "Tahoma"));
        var clip = new TextClip { Text = "x", TimelineStart = F(0), Duration = F(25), FontFamily = "segoe ui" };
        Sequence.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "x", null, Rate));

        Assert.Equal(new[] { "Arial", "Segoe UI", "Tahoma" }, inspector.FontFamilies);
        Assert.Equal("Segoe UI", inspector.FontFamilyName);     // the listed spelling, no edit
        Assert.Equal("segoe ui", clip.FontFamily);

        clip.FontFamily = "Font From Elsewhere";                 // e.g. a project from another machine
        inspector.ShowClip(new TimelineClipSelection(clip, "x", null, Rate));
        Assert.Equal(new[] { "Font From Elsewhere", "Arial", "Segoe UI", "Tahoma" }, inspector.FontFamilies);
        Assert.Equal("Font From Elsewhere", inspector.FontFamilyName);
        Assert.False(_undo.CanUndo);

        inspector.FontFamilyName = "Tahoma";
        Assert.Equal("Tahoma", clip.FontFamily);
    }

    [Fact]
    public async Task Text_edits_while_playing_keep_the_pipeline_and_seek_generation()
    {
        Assert.True(_edit.AddClip(Video("v.mp4").Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var clip = AddText(0);
        Preview.PlayPauseCommand.Execute(null);
        await TickUntil(() => !Preview.IsBuffering && Preview.AreLayersCurrent && Preview.Layers.Length == 2, "playing");
        var pipeline = _playback.VideoPipelineInstance;
        var generation = _playback.SeekGeneration;

        for (var i = 0; i < 8; i++)
        {
            Inspector.TextContent = "Frame " + i;
            Inspector.FontSize = 40 + i;
            Inspector.Alignment = (TextAlignment)(i % 3);
            _clock.Advance(0.04);
            Preview.Tick();
            Assert.False(Preview.IsBuffering);
        }
        Inspector.TextColorHex = "#123456";
        await TickUntil(() => Preview.Layers.Length == 2 && Preview.Layers[1].Layer is Core.Composition.TextLayer { Text.ColorHex: "#123456" }, "new text");

        Assert.Same(pipeline, _playback.VideoPipelineInstance);
        Assert.Equal(generation, _playback.SeekGeneration);
        var layer = Assert.IsType<Core.Composition.TextLayer>(Preview.Layers[1].Layer);
        Assert.Equal("Frame 7", layer.Text.Text);
        Assert.Equal(47, layer.Text.FontSize);
        Assert.Equal(clip.Id, layer.ClipId);
        Assert.True(Preview.IsPlaying);
    }

    // --- Timeline label ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Title", "Title")]
    [InlineData("First\nSecond", "First")]
    [InlineData("First\r\nSecond", "First")]
    [InlineData("First\rSecond", "First")]
    [InlineData("  padded  \nx", "  padded  ")]
    [InlineData("", TimelineViewModel.EmptyTextLabel)]
    [InlineData("   ", TimelineViewModel.EmptyTextLabel)]
    [InlineData(" \r\n\t\n ", TimelineViewModel.EmptyTextLabel)]
    public void The_label_is_the_first_line_or_empty_text(string text, string label) =>
        Assert.Equal(label, TimelineViewModel.TextLabel(text));

    [Fact]
    public void The_label_follows_edits_undo_and_redo()
    {
        var clip = AddText(0);
        var view = ViewOf(clip);
        var raised = 0;
        view.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimelineClipViewModel.Name)) raised++; };
        Assert.Equal("Text", view.Name);

        Inspector.TextContent = "Hello";                       // merged with the next edit …
        Inspector.TextContent = "Hello\nWorld";
        Assert.Equal("Hello", view.Name);
        Assert.True(raised > 0);                                // the view is told

        Inspector.FontSize = 60;                                // a separate step
        Inspector.TextContent = "   ";
        Assert.Equal(TimelineViewModel.EmptyTextLabel, view.Name);

        _undo.Undo();                                           // whitespace text
        Assert.Equal("Hello", view.Name);
        _undo.Undo();                                           // font size: label unchanged
        Assert.Equal("Hello", view.Name);
        _undo.Undo();                                           // "Hello\nWorld"
        Assert.Equal("Text", view.Name);

        _undo.Redo();
        Assert.Equal("Hello", view.Name);
        _undo.Redo();
        _undo.Redo();
        Assert.Equal(TimelineViewModel.EmptyTextLabel, view.Name);
        Assert.Equal("   ", clip.Text);
    }

    [Fact]
    public void Split_halves_and_other_edits_keep_the_label_of_the_current_text()
    {
        var clip = AddText(0);
        Inspector.TextContent = "Top line\nsecond";

        Assert.True(_edit.Split(F(50), new[] { clip.Id }).Success);
        var halves = Sequence.VideoTracks[0].Clips.OfType<TextClip>().ToList();

        Assert.Equal(2, halves.Count);
        Assert.All(halves, h => Assert.Equal("Top line", ViewOf(h).Name));

        Assert.True(_edit.SetClipProperties(halves[1].Id, new ClipPropertyChange
        {
            Text = TextProperties.Of(halves[1])!.Value with { Text = "" }
        }).Success);
        Assert.Equal("Top line", ViewOf(halves[0]).Name);
        Assert.Equal(TimelineViewModel.EmptyTextLabel, ViewOf(halves[1]).Name);

        _undo.Undo();
        Assert.Equal("Top line", ViewOf(halves[1]).Name);
    }

    private sealed class Fonts(params string[] names) : IFontCatalog
    {
        public IReadOnlyList<string> FamilyNames { get; } = names;
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
