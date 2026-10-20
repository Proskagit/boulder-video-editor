using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 27 Step 27.5 (D043 §1–§4, §6) through the real shell wiring: "+ Text" placed at the bottom centre by the measured
/// box; the Inspector's «Анимация» block (kinds, directions, durations in seconds over the fades; a stored fade outside
/// 0.1–3 s shown as it is), bold, the outline and the background (switches, swatches, thickness / opacity), the text colour
/// swatches and the 3 × 3 grid — each discrete choice one undo step, a typed or dragged value merging; the fades section
/// only for other clips; nothing edited while an export runs.
/// </summary>
public sealed class TextStyleUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private sealed class FixedBox : ITextBoxMeasurer
    {
        public (double Width, double Height) Measure(TextProperties text) => (300, 60);
    }

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TextStyleUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;                    // the durations below are at 25 fps
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 });
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance);
        var boxes = new FixedBox();
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: _lock, textBoxes: boxes),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, editingLock: _lock, textBoxes: boxes),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private InspectorViewModel Inspector => _vm.Inspector;
    private Sequence Sequence => _projects.Current.Timeline;
    private FrameSize Canvas => new(_projects.Current.Settings.FrameWidth, _projects.Current.Settings.FrameHeight);
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private TextClip AddText()
    {
        Sequence.PlayheadPosition = F(0);
        var before = Sequence.VideoTracks.SelectMany(t => t.Clips).Select(c => c.Id).ToHashSet();
        _vm.Timeline.AddTextCommand.Execute(null);
        return Sequence.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single(c => !before.Contains(c.Id));
    }

    /// <summary>The undo steps (undone to the start and redone back); the selected clip is selected again.</summary>
    private int UndoDepth()
    {
        var selected = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Clip).ToList();
        var depth = 0;
        while (_undo.CanUndo) { _undo.Undo(); depth++; }
        for (var i = 0; i < depth; i++) _undo.Redo();
        if (selected.Count == 1)
            _vm.Timeline.OnClipPressed(_vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == selected[0].Id), toggle: false);
        return depth;
    }

    [Fact]
    public void A_new_text_is_placed_at_the_bottom_centre_and_the_inspector_shows_its_defaults()
    {
        var clip = AddText();
        var expected = TextPlacement.Position(Canvas, TextProperties.Of(clip)!.Value, VisualProperties.Default, 300, 60,
            TextGridRow.Bottom, TextGridColumn.Centre);
        Assert.Equal(expected, (clip.PositionX, clip.PositionY));
        Assert.Equal(1, UndoDepth());

        Assert.False(Inspector.ShowsFades);
        Assert.Equal((int)TextAppearanceKind.Slide, Inspector.AppearKindIndex);
        Assert.Equal((int)TextMotionSide.Bottom, Inspector.AppearSideIndex);
        Assert.Equal(0.4m, Inspector.AppearSeconds);
        Assert.Equal((int)TextAppearanceKind.Fade, Inspector.DisappearKindIndex);
        Assert.Equal(0.32m, Inspector.DisappearSeconds);                // 0.3 s → 8 frames at 25 fps
        Assert.True(Inspector.IsAppearSlide && Inspector.IsAppearTimed && !Inspector.IsDisappearSlide);
        Assert.True(Inspector.IsBold && Inspector.IsBackgroundOn && !Inspector.IsOutlineOn);
        Assert.Equal((8m, 60m), (Inspector.OutlineThicknessPercent, Inspector.BackgroundOpacityPercent));
    }

    [Fact]
    public void A_kind_is_one_step_with_its_fade_and_none_clears_the_fade()
    {
        var clip = AddText();
        Inspector.AppearKindIndex = (int)TextAppearanceKind.Zoom;
        Assert.Equal((new TextMotion(TextMotionKind.Zoom), F(10)), (clip.MotionIn, clip.FadeIn));
        Inspector.AppearKindIndex = (int)TextAppearanceKind.None;
        Assert.Equal((TextMotion.None, MediaTime.Zero), (clip.MotionIn, clip.FadeIn));
        Assert.False(Inspector.IsAppearTimed);
        Inspector.AppearKindIndex = (int)TextAppearanceKind.Fade;
        Assert.Equal((TextMotion.None, F(10)), (clip.MotionIn, clip.FadeIn));          // the default 0.4 s back
        Assert.Equal(4, UndoDepth());                                                 // add + three choices

        _undo.Undo();
        _undo.Undo();
        Assert.Equal((new TextMotion(TextMotionKind.Zoom), F(10)), (clip.MotionIn, clip.FadeIn));
        Assert.Equal((int)TextAppearanceKind.Zoom, Inspector.AppearKindIndex);        // the fields follow undo
    }

    [Fact]
    public void A_direction_is_one_step_each()
    {
        var clip = AddText();
        Inspector.AppearSideIndex = (int)TextMotionSide.Left;
        Inspector.AppearSideIndex = (int)TextMotionSide.Top;
        Assert.Equal(new TextMotion(TextMotionKind.Slide, TextMotionSide.Top), clip.MotionIn);
        Assert.Equal(3, UndoDepth());
    }

    [Fact]
    public void A_duration_in_seconds_sets_the_fade_in_whole_frames_and_merges()
    {
        var clip = AddText();
        Inspector.DisappearSeconds = 1.5m;
        Inspector.DisappearSeconds = 2.02m;                                           // 50.5 frames → 51
        Assert.Equal(F(51), clip.FadeOut);
        Assert.Equal(2, UndoDepth());                                                 // add + one merged duration
        Assert.Equal(2.04m, Inspector.DisappearSeconds);                              // shown as the stored frames

        Inspector.DisappearSeconds = 5m;                                              // outside 0.1–3 s
        Assert.Equal(F(51), clip.FadeOut);
        Assert.Equal(string.Format(Strings.Inspector_MotionDurationRange, 0.1, 3), _status.Message);
    }

    [Fact]
    public void A_stored_fade_outside_the_range_is_shown_as_it_is_and_kept()
    {
        var clip = AddText();
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(100), F(1)) }).Success);
        var depth = UndoDepth();

        Assert.Equal(4m, Inspector.AppearSeconds);
        Assert.Equal(4m, Inspector.MaxAppearSeconds);
        Assert.Equal(0.04m, Inspector.DisappearSeconds);
        Assert.Equal(0.04m, Inspector.MinDisappearSeconds);
        Assert.Equal((F(100), F(1)), (clip.FadeIn, clip.FadeOut));
        Assert.Equal(depth, UndoDepth());                                             // showing it made no edit
    }

    [Fact]
    public void Bold_the_switches_and_the_swatches_are_one_step_each_thickness_and_opacity_merge()
    {
        var clip = AddText();
        Inspector.IsBold = false;
        Inspector.IsOutlineOn = true;
        Inspector.IsBackgroundOn = false;
        Inspector.SetTextColorCommand.Execute("#FFD600");
        Inspector.SetTextColorCommand.Execute("#E53935");
        Inspector.SetOutlineColorCommand.Execute("#FFFFFF");
        Inspector.SetBackgroundColorCommand.Execute("#43A047");
        Assert.Equal(8, UndoDepth());                                                 // add + seven choices

        Inspector.OutlineThicknessPercent = 10m;
        Inspector.OutlineThicknessPercent = 12.4m;                                    // whole percent: 12
        Inspector.BackgroundOpacityPercent = 30m;
        Inspector.BackgroundOpacityPercent = 40m;
        Assert.Equal(10, UndoDepth());                                                // + two merged values

        var text = TextProperties.Of(clip)!.Value;
        Assert.False(text.Bold);
        Assert.Equal("#E53935", text.ColorHex);
        Assert.Equal(new TextOutline(true, "#FFFFFF", 0.12), text.Outline);
        Assert.Equal(new TextBackground(false, "#43A047", 0.4), text.Background);   // off, its values kept
        Assert.Equal("#E53935", Inspector.TextColorSwatch);
    }

    [Theory]
    [InlineData("Top,Left", TextGridRow.Top, TextGridColumn.Left)]
    [InlineData("Middle,Centre", TextGridRow.Middle, TextGridColumn.Centre)]
    [InlineData("Bottom,Right", TextGridRow.Bottom, TextGridColumn.Right)]
    public void A_grid_cell_places_the_box_in_one_step(string cell, TextGridRow row, TextGridColumn column)
    {
        var clip = AddText();
        Inspector.PlaceCommand.Execute(cell);
        var expected = TextPlacement.Position(Canvas, TextProperties.Of(clip)!.Value, VisualProperties.Of(clip)!.Value, 300, 60, row, column);
        Assert.Equal(expected, (clip.PositionX, clip.PositionY));
        Inspector.PlaceCommand.Execute("Top,Right");
        Assert.Equal(3, UndoDepth());                                                 // add + two cells, never merged
    }

    [Fact]
    public void Nothing_is_edited_while_an_export_runs()
    {
        var clip = AddText();
        var before = (TextProperties.Of(clip), clip.PositionX, clip.PositionY, clip.FadeIn);
        using (_lock.Acquire())
        {
            Inspector.PlaceCommand.Execute("Top,Left");
            Inspector.SetTextColorCommand.Execute("#000000");
            Inspector.IsOutlineOn = true;
            Inspector.AppearKindIndex = (int)TextAppearanceKind.None;
            Inspector.AppearSeconds = 2m;
        }
        Assert.Equal(before, (TextProperties.Of(clip), clip.PositionX, clip.PositionY, clip.FadeIn));
        Assert.Equal(1, UndoDepth());
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
