using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 29 (D045 §5–§6) through the real shell wiring: «Шаблон ▾» adds each template at the playhead on the top video track,
/// selected, in one undo step with a status; the timeline names it by its kind (and what it shows); the Inspector shows only
/// the kind's own fields — no fades, no transform — a typed field merging with the next change of it, a status and a route
/// colour one step each, a refused value shown back from the model; nothing is edited while an export runs.
/// </summary>
public sealed class TemplateUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TemplateUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 });
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(edit, _status, editingLock: _lock),
            new TimelineViewModel(_projects, edit, _status, NullLogger<TimelineViewModel>.Instance, editingLock: _lock),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private InspectorViewModel Inspector => _vm.Inspector;
    private Sequence Sequence => _projects.Current.Timeline;

    private TemplateClip Add(TemplateKind kind, long atFrame = 0)
    {
        Sequence.PlayheadPosition = MediaTime.FromFrame(atFrame, Rate);
        _vm.Timeline.AddTemplateCommand.Execute(kind.ToString());
        return Sequence.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().OrderBy(c => c.TimelineStart).Last();
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

    [Theory]
    [InlineData(TemplateKind.Rec, true, false, false)]
    [InlineData(TemplateKind.Vhs, true, false, false)]
    [InlineData(TemplateKind.Boulder, false, true, false)]
    [InlineData(TemplateKind.Highlight, false, false, true)]
    public void The_menu_adds_a_selected_template_and_the_inspector_shows_only_its_fields(TemplateKind kind, bool date, bool boulder, bool highlight)
    {
        var clip = Add(kind, 10);

        Assert.Equal(MediaTime.FromFrame(10, Rate), clip.TimelineStart);
        Assert.Equal(Strings.Template_Added, _status.Message);
        Assert.Equal(1, UndoDepth());
        var vm = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
        Assert.True(vm.IsSelected);
        Assert.Equal("#783A5C", vm.ColorHex);

        Assert.True(Inspector.HasTemplate);
        Assert.Equal((date, boulder, highlight), (Inspector.ShowsTemplateDate, Inspector.ShowsTemplateBoulder, Inspector.ShowsTemplateHighlight));
        Assert.False(Inspector.ShowsFades);
        Assert.False(Inspector.HasVisualProperties);
        Assert.False(Inspector.HasTextProperties);
        Assert.Equal(string.Format(Strings.ClipType_Template, TimelineViewModel.TemplateTypeName(kind)), Inspector.ClipTypeLabel);
    }

    [Fact]
    public void An_unknown_kind_adds_nothing()
    {
        _vm.Timeline.AddTemplateCommand.Execute("Wasted");
        Assert.Empty(Sequence.VideoTracks.SelectMany(t => t.Clips));
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void The_timeline_names_a_template_by_its_kind_and_what_it_shows()
    {
        var boulder = Add(TemplateKind.Boulder);
        Assert.Equal(Strings.TemplateType_Boulder, _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == boulder.Id).Name);

        Inspector.TemplateRoute = "7";
        Inspector.TemplateGrade = "7A";
        Assert.Equal(string.Format(Strings.Clip_TemplateWith, Strings.TemplateType_Boulder, "7 · 7A"), TimelineViewModel.TemplateLabel(boulder));
        Assert.Equal(string.Format(Strings.Clip_TemplateWith, Strings.TemplateType_Highlight, "ПЕРВЫЙ ТОП"),
            TimelineViewModel.TemplateLabel(new TemplateClip { Kind = TemplateKind.Highlight, Title = "ПЕРВЫЙ ТОП" }));
        Assert.Equal(Strings.TemplateType_Rec, TimelineViewModel.TemplateLabel(new TemplateClip { Kind = TemplateKind.Rec }));
    }

    [Fact]
    public void Typing_merges_while_a_status_and_a_colour_are_one_step_each()
    {
        var clip = Add(TemplateKind.Boulder);

        Inspector.TemplateRoute = "1";
        Inspector.TemplateRoute = "12";
        Assert.Equal("12", clip.Route);
        Assert.Equal(2, UndoDepth());                                          // add + the typed route

        Inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Top);
        Inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Attempt);
        Inspector.SetTemplateAccentCommand.Execute("#2979FF");
        Assert.Equal((BoulderStatus.Attempt, "#2979FF"), (clip.Status, clip.AccentHex));
        Assert.Equal(5, UndoDepth());
        Assert.True(Inspector.TemplateStatuses.Single(o => o.Status == BoulderStatus.Attempt).IsSelected);
        Assert.False(Inspector.TemplateStatuses.Single(o => o.Status == BoulderStatus.Top).IsSelected);

        Inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Attempt);   // the status shown: no step
        Assert.Equal(5, UndoDepth());

        _undo.Undo();
        _undo.Undo();
        Assert.Equal((BoulderStatus.Top, TemplateRules.DefaultAccentHex), (clip.Status, clip.AccentHex));
        Assert.Equal(TemplateRules.DefaultAccentHex, Inspector.TemplateAccentHex);   // the fields follow undo
    }

    [Fact]
    public void A_refused_value_is_shown_back_from_the_model_and_nothing_changes_during_an_export()
    {
        var clip = Add(TemplateKind.Highlight);
        var title = clip.Title;

        Inspector.TemplateTitle = "A\nB";
        Assert.Equal(title, clip.Title);
        Assert.Equal(Core.Localization.Messages.Template_OneLine, _status.Message);

        using (_lock.Acquire())
        {
            Inspector.TemplateSubtitle = "Трасса 3";
            Assert.Equal("", clip.Subtitle);
        }
    }

    [Fact]
    public void At_playhead_sets_the_status_moment_on_the_template_s_clock_in_one_step_and_marks_it_on_the_clip()
    {
        // D045 §17: choose the status, put the playhead on the frame, «На указатель».
        var clip = Add(TemplateKind.Boulder, 10);
        Assert.False(Inspector.ShowsTemplateStatusAt);                  // no status: nothing to time
        Inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Attempt);
        Assert.Equal(string.Format(Strings.TemplateStatusAt_Value, 0.0), Inspector.TemplateStatusAtText);
        var mark = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
        Assert.Equal(0.0, mark.StatusMarkX);

        Sequence.PlayheadPosition = MediaTime.FromFrame(60, Rate);
        Inspector.SetTemplateStatusAtPlayheadCommand.Execute(null);

        Assert.Equal(MediaTime.FromFrame(50, Rate), clip.StatusAt);     // 2 s after the template's start
        Assert.Equal(BoulderStatus.Attempt, clip.Status);
        Assert.Equal(string.Format(Strings.TemplateStatusAt_Value, 2.0), Inspector.TemplateStatusAtText);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(MediaTime.FromSeconds(2), _vm.Timeline.PixelsPerSecond), mark.StatusMarkX!.Value, 6);
        Assert.Equal(3, UndoDepth());                                   // the add, the status, the moment

        // A second click elsewhere: its own step.
        Sequence.PlayheadPosition = MediaTime.FromFrame(110, Rate);
        Inspector.SetTemplateStatusAtPlayheadCommand.Execute(null);
        Assert.Equal(MediaTime.FromFrame(100, Rate), clip.StatusAt);
        Assert.Equal(4, UndoDepth());
        _undo.Undo();
        Assert.Equal(MediaTime.FromFrame(50, Rate), clip.StatusAt);

        // The playhead off the clip: refused, nothing changes.
        Sequence.PlayheadPosition = MediaTime.FromFrame(5, Rate);
        Inspector.SetTemplateStatusAtPlayheadCommand.Execute(null);
        Assert.Equal(MediaTime.FromFrame(50, Rate), clip.StatusAt);
        Assert.Equal(Strings.TemplateStatusAt_PlayheadOutside, _status.Message);
    }

    [Fact]
    public void After_a_split_both_parts_keep_the_moment_and_at_playhead_on_the_right_part_counts_from_the_original_start()
    {
        var clip = Add(TemplateKind.Boulder, 10);
        Inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Top);
        Sequence.PlayheadPosition = MediaTime.FromFrame(60, Rate);
        Inspector.SetTemplateStatusAtPlayheadCommand.Execute(null);     // moment: 50 frames

        Sequence.PlayheadPosition = MediaTime.FromFrame(40, Rate);     // the cut 30 frames in, before the moment
        _vm.Timeline.SplitAtPlayheadCommand.Execute(null);
        var parts = Sequence.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(MediaTime.FromFrame(50, Rate), p.StatusAt));
        var marks = parts.Select(p => _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == p.Id)).ToList();
        Assert.False(marks[0].HasStatusMark);                           // the moment is after the left part
        Assert.Equal(TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(20, Rate), _vm.Timeline.PixelsPerSecond), marks[1].StatusMarkX!.Value, 6);

        _vm.Timeline.OnClipPressed(marks[1], toggle: false);
        Sequence.PlayheadPosition = MediaTime.FromFrame(70, Rate);
        Inspector.SetTemplateStatusAtPlayheadCommand.Execute(null);
        Assert.Equal(MediaTime.FromFrame(60, Rate), parts[1].StatusAt);  // 30 (its counter) + 30 frames into it
        Assert.Equal(MediaTime.FromFrame(50, Rate), parts[0].StatusAt);
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
