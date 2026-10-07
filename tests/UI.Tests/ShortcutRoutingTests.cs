using System.Windows.Input;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Steps 9.6a / 9.6b: the main window's shortcut routing (<see cref="ShortcutRouter"/>) — every shortcut keeps its
/// key and command (the existing ones, J / K / L and Ctrl+I / Ctrl+E / \ from 9.6b), modifiers must match exactly, nothing fires while a text input has focus or sent the key, and a
/// known shortcut whose command is unavailable (editing during an export) is consumed without running. Real view
/// models, project and edit service.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class ShortcutRoutingTests
{
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public ShortcutRoutingTests()
    {
        var undo = new UndoRedoService();
        var edit = new TimelineEditService(_projects, undo, NullLogger<TimelineEditService>.Instance);
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var picker = new ScriptedPicker();
        var import = new MediaImportWorkflow(picker, new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(1), picker, status,
            NullLogger<ProjectFileWorkflow>.Instance);
        var playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(undo, files, import, status, editingLock: _lock),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance, _lock),
            new PreviewViewModel(status, playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(edit, status, editingLock: _lock),
            new TimelineViewModel(_projects, edit, status, NullLogger<TimelineViewModel>.Instance, _lock),
            status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    private ICommand? For(Key key, KeyModifiers modifiers = KeyModifiers.None) => ShortcutRouter.CommandFor(_vm, key, modifiers);

    private bool Press(Key key, KeyModifiers modifiers = KeyModifiers.None, object? source = null, object? focused = null) =>
        ShortcutRouter.Handle(_vm, key, modifiers, source ?? new Button(), focused);

    [Fact]
    public void Every_existing_shortcut_keeps_its_key_and_command() =>
        Assert.All(Table(), row => Assert.Same(row.Command, For(row.Key, row.Modifiers)));

    [Fact]
    public void No_shortcut_fires_while_a_text_input_has_focus_or_sent_the_key()
    {
        // D024 Step 9.6d: every row of the table — also J / K / L, Ctrl+L, Ctrl+I / Ctrl+E and \ — stays with the text box
        // (checked by hand in the running app for the Inspector's text and number fields, progress.md 9.6d).
        var textBox = new TextBox();

        Assert.All(Table(), row =>
        {
            Assert.False(Press(row.Key, row.Modifiers, focused: textBox), $"{row.Modifiers}+{row.Key} fired with a text box focused");
            Assert.False(Press(row.Key, row.Modifiers, source: textBox), $"{row.Modifiers}+{row.Key} fired from a text box");
        });
    }

    /// <summary>Every shortcut: key, exact modifiers, command.</summary>
    private (Key Key, KeyModifiers Modifiers, ICommand Command)[] Table()
    {
        var toolbar = _vm.Toolbar;
        var timeline = _vm.Timeline;
        const KeyModifiers ctrl = KeyModifiers.Control, shift = KeyModifiers.Shift, ctrlShift = ctrl | shift;
        return new (Key Key, KeyModifiers Modifiers, ICommand Command)[]
        {
            (Key.N, ctrl, toolbar.NewProjectCommand),
            (Key.O, ctrl, toolbar.OpenCommand),
            (Key.S, ctrl, toolbar.SaveCommand),
            (Key.S, ctrlShift, toolbar.SaveAsCommand),
            (Key.Z, ctrl, toolbar.UndoCommand),
            (Key.Y, ctrl, toolbar.RedoCommand),
            (Key.Z, ctrlShift, toolbar.RedoCommand),
            (Key.Delete, KeyModifiers.None, timeline.DeleteSelectedCommand),
            (Key.Back, KeyModifiers.None, timeline.DeleteSelectedCommand),
            (Key.S, KeyModifiers.None, timeline.SplitAtPlayheadCommand),
            (Key.N, KeyModifiers.None, timeline.ToggleSnappingCommand),
            (Key.Left, KeyModifiers.None, timeline.StepBackwardCommand),
            (Key.Right, KeyModifiers.None, timeline.StepForwardCommand),
            (Key.Left, shift, timeline.StepBackwardSecondCommand),
            (Key.Right, shift, timeline.StepForwardSecondCommand),
            (Key.Space, KeyModifiers.None, _vm.Preview.PlayPauseCommand),
            (Key.Home, KeyModifiers.None, timeline.GoToStartCommand),
            (Key.End, KeyModifiers.None, timeline.GoToEndCommand),
            (Key.OemPlus, ctrl, timeline.ZoomInCommand),
            (Key.Add, ctrl, timeline.ZoomInCommand),
            (Key.OemMinus, ctrl, timeline.ZoomOutCommand),
            (Key.Subtract, ctrl, timeline.ZoomOutCommand),
            // Step 9.6b (PO-H1 / PO-H2 / PO-H4)
            (Key.J, KeyModifiers.None, timeline.StepBackwardSecondCommand),
            (Key.K, KeyModifiers.None, _vm.Preview.PauseCommand),
            (Key.L, KeyModifiers.None, _vm.Preview.PlayCommand),
            (Key.L, ctrl, _vm.Preview.ToggleLoopCommand),                    // Step 9.6c (PO-H3)
            (Key.I, ctrl, toolbar.ImportMediaCommand),
            (Key.E, ctrl, toolbar.ExportCommand),
            (Key.OemPipe, KeyModifiers.None, timeline.ZoomToFitCommand),
            (Key.OemBackslash, KeyModifiers.None, timeline.ZoomToFitCommand),
            // Phase 12 Step 12.6 (D027 §5)
            (Key.C, ctrl, timeline.CopyCommand),
            (Key.V, ctrl, timeline.PasteCommand),
            (Key.D, ctrl, timeline.DuplicateCommand),
            // Phase 15 Step 15.4 (D030 §5, Q2)
            (Key.Q, KeyModifiers.None, timeline.TrimStartToPlayheadCommand),
            (Key.W, KeyModifiers.None, timeline.TrimEndToPlayheadCommand),
            (Key.Q, shift, timeline.RippleTrimStartToPlayheadCommand),
            (Key.W, shift, timeline.RippleTrimEndToPlayheadCommand),
        };
    }

    [Theory]
    [InlineData(Key.S, KeyModifiers.Shift)]                        // not S
    [InlineData(Key.S, KeyModifiers.Alt)]
    [InlineData(Key.S, KeyModifiers.Control | KeyModifiers.Alt)]   // not Ctrl+S
    [InlineData(Key.N, KeyModifiers.Control | KeyModifiers.Shift)] // not Ctrl+N
    [InlineData(Key.Space, KeyModifiers.Control)]
    [InlineData(Key.Left, KeyModifiers.Control)]
    [InlineData(Key.OemPlus, KeyModifiers.None)]                   // zoom needs Ctrl
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.Z, KeyModifiers.None)]
    [InlineData(Key.J, KeyModifiers.Shift)]                        // J / K / L only without modifiers
    [InlineData(Key.K, KeyModifiers.Control)]
    [InlineData(Key.I, KeyModifiers.None)]                         // Import and Export need Ctrl
    [InlineData(Key.E, KeyModifiers.None)]
    [InlineData(Key.OemPipe, KeyModifiers.Control)]
    [InlineData(Key.C, KeyModifiers.None)]                         // copy / paste / duplicate need Ctrl
    [InlineData(Key.V, KeyModifiers.None)]
    [InlineData(Key.D, KeyModifiers.None)]
    [InlineData(Key.Q, KeyModifiers.Control)]                      // trim to the playhead: Q / W, Shift only
    [InlineData(Key.W, KeyModifiers.Control)]
    [InlineData(Key.Q, KeyModifiers.Alt)]
    [InlineData(Key.W, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift)]
    public void Other_keys_and_modifiers_are_not_shortcuts_and_are_not_consumed(Key key, KeyModifiers modifiers)
    {
        Assert.Null(For(key, modifiers));
        Assert.False(Press(key, modifiers));
    }

    [Fact]
    public void A_shortcut_runs_its_command_and_is_consumed()
    {
        var snapping = _vm.Timeline.SnappingEnabled;

        Assert.True(Press(Key.N));

        Assert.NotEqual(snapping, _vm.Timeline.SnappingEnabled);
    }

    [Fact]
    public void Nothing_fires_while_a_text_input_has_focus_or_sent_the_key()
    {
        var snapping = _vm.Timeline.SnappingEnabled;
        var textBox = new TextBox();

        Assert.False(Press(Key.N, source: textBox));                      // typed into the text box
        Assert.False(Press(Key.N, focused: textBox));                     // the text box has focus
        Assert.False(Press(Key.S, KeyModifiers.Control, focused: textBox));
        Assert.Equal(snapping, _vm.Timeline.SnappingEnabled);

        Assert.True(Press(Key.N, focused: new Button()));                 // any other control: the shortcut works
        Assert.NotEqual(snapping, _vm.Timeline.SnappingEnabled);
    }

    [Fact]
    public void Only_text_inputs_block_shortcuts()
    {
        Assert.True(ShortcutRouter.IsTextInput(new TextBox()));
        Assert.False(ShortcutRouter.IsTextInput(new NumericUpDown()));   // its inner TextBox is what gets focus
        Assert.False(ShortcutRouter.IsTextInput(new Button()));
        Assert.False(ShortcutRouter.IsTextInput(new ComboBox()));
        Assert.False(ShortcutRouter.IsTextInput(null));
    }

    [Fact]
    public void During_an_export_editing_shortcuts_are_consumed_but_do_nothing_and_viewing_ones_still_work()
    {
        var clip = new TextClip { Text = "T", Duration = MediaTime.FromSeconds(4) };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        _projects.NotifyTimelineChanged();
        _vm.Timeline.StepForwardSecondCommand.Execute(null);                   // playhead inside the clip
        var playhead = _vm.Timeline.Playhead;

        using (_lock.Acquire())
        {
            Assert.True(Press(Key.S));                                          // split: consumed …
            Assert.Single(_projects.Current.Timeline.VideoTracks[0].Clips);     // … but nothing split
            Assert.True(Press(Key.N, KeyModifiers.Control));                    // New: consumed, not run
            Assert.True(Press(Key.Z, KeyModifiers.Control));
            Assert.True(Press(Key.I, KeyModifiers.Control));                    // Import: consumed, not run
            Assert.False(_vm.Toolbar.ImportMediaCommand.CanExecute(null));
            Assert.True(Press(Key.E, KeyModifiers.Control));                    // Export: consumed, not run
            Assert.False(_vm.Toolbar.ExportCommand.CanExecute(null));

            Assert.True(Press(Key.Right));                                      // viewing still works
            Assert.True(_vm.Timeline.Playhead > playhead);
        }

        Assert.True(Press(Key.S));
        Assert.Equal(2, _projects.Current.Timeline.VideoTracks[0].Clips.Count); // after the export: split
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
