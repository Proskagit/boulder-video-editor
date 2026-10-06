using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
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
/// Phase 13 Step 13.6 (D028): Project Settings… — the toolbar command, the dialog's draft (nothing changes until Apply),
/// Apply through <c>SetProjectSettings</c> / <c>SetCanvasSize</c> / <c>SetFrameRate</c> with a refusal shown in the dialog,
/// Cancel, the export lock, the frame-rate options (provisional, a rate no longer offered), and the whole workflow through
/// the real shell: one Undo step, then Undo / Redo followed by the Preview, the timeline's rate display and the Inspector.
/// </summary>
public sealed class ProjectSettingsUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly PlaybackService _service;
    private readonly ScriptedSettingsDialog _dialog = new();
    private readonly ProjectSettingsWorkflow _workflow;
    private readonly MainWindowViewModel _vm;
    private int _timelineChanges;

    public ProjectSettingsUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _service = new PlaybackService(_decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        _workflow = new ProjectSettingsWorkflow(_projects, _edit, _status, _lock, _dialog);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status, editingLock: _lock, projectSettings: _workflow),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _service, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
        _projects.TimelineChanged += (_, _) => _timelineChanges++;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private ProjectSettings Settings => _projects.Current.Settings;

    /// <summary>A locked 25 fps project with a video moved left and a text, the video selected in the timeline.</summary>
    private (VideoClip Video, TextClip Title) Scene()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-settings-tests", Guid.NewGuid().ToString("N"), "a.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromFrame(250, Rate), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, 250));
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id, null, MediaTime.Zero).Success);
        var video = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        Assert.True(_edit.SetClipProperties(video.Id, new ClipPropertyChange { Visual = VisualProperties.Of(video)!.Value with { PositionX = -400 } }).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);          // the title goes on top, on V2
        Assert.True(_edit.AddTextClip(MediaTime.FromFrame(5, Rate)).Success);
        var title = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        _undo.MarkSavePoint(_undo.CurrentPosition);
        var clipVm = _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == video.Id);
        _vm.Timeline.OnClipPressed(clipVm, toggle: false);
        return (video, title);
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            _vm.Preview.Tick();
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    private ProjectSettingsViewModel Draft() => new(Settings, _edit, _status, () => _lock.IsLocked);

    // --- the whole workflow -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_of_canvas_and_rate_is_one_undo_step_followed_by_the_preview_the_rate_display_and_the_inspector()
    {
        var (video, _) = Scene();
        Assert.Equal(-400m, _vm.Inspector.PositionX);
        Assert.Equal("Project Settings — 1920 × 1080 · 25 FPS", _vm.Toolbar.ProjectSettingsToolTip);
        var top = _undo.CurrentPosition;
        _dialog.Script = d =>
        {
            Assert.Equal("1920 × 1080 · 25 FPS", d.CurrentSummary);
            d.SelectedPreset = ProjectSettingsViewModel.AllPresets.Single(p => p.Width == 1080 && p.Height == 1920);
            d.SelectedRate = d.Rates.Single(r => r.Rate == FrameRate.Fps30);
            Assert.Equal("Positions and text sizes will be scaled by 56.25 %.", d.ScaleNotice);
            Assert.NotNull(d.RateNotice);
            d.ApplyCommand.Execute(null);
        };
        var changes = _timelineChanges;

        await _vm.Toolbar.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.True(_dialog.Closed);
        Assert.Equal(changes + 1, _timelineChanges);
        Assert.NotSame(top, _undo.CurrentPosition);
        Assert.Equal("Change Project Settings", ((IUndoableCommand)_undo.CurrentPosition).Description);
        _undo.Undo();
        Assert.Same(top, _undo.CurrentPosition);                         // exactly one step
        _undo.Redo();
        await AssertState(1080, 1920, FrameRate.Fps30, -225m, "30 FPS");
        Assert.True(_projects.Current.IsDirty);
        Assert.False(_undo.IsAtSavePoint);
        Assert.Contains("1080 × 1920", _status.Message);

        _undo.Undo();
        await AssertState(1920, 1080, Rate, -400m, "25 FPS");
        Assert.True(_undo.IsAtSavePoint);   // the dirty state's undo part (the scene's media add is not undoable)

        _undo.Redo();
        await AssertState(1080, 1920, FrameRate.Fps30, -225m, "30 FPS");
        Assert.Equal(-225.0, video.PositionX);
    }

    private async Task AssertState(int width, int height, FrameRate rate, decimal positionX, string rateDisplay)
    {
        Assert.Equal((width, height, rate), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate));
        await TickUntil(() => _vm.Preview.Canvas == new FrameSize(width, height), $"Preview canvas {width} × {height}");
        Assert.Equal(rateDisplay, _vm.Timeline.FrameRateDisplay);
        Assert.Equal(positionX, _vm.Inspector.PositionX);
        Assert.Equal($"Project Settings — {width} × {height} · {rateDisplay}", _vm.Toolbar.ProjectSettingsToolTip);
    }

    [Fact]
    public async Task Only_the_canvas_and_only_the_rate_each_make_their_own_step()
    {
        Scene();
        _dialog.Script = d => { (d.Width, d.Height) = (1080, 1080); d.ApplyCommand.Execute(null); };
        await _vm.Toolbar.ProjectSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Set Frame Size", ((IUndoableCommand)_undo.CurrentPosition).Description);
        Assert.Equal((1080, 1080, Rate), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate));

        _dialog.Script = d => { d.SelectedRate = d.Rates.Single(r => r.Rate == new FrameRate(50, 1)); d.ApplyCommand.Execute(null); };
        await _vm.Toolbar.ProjectSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Set Frame Rate", ((IUndoableCommand)_undo.CurrentPosition).Description);
        Assert.Equal((1080, 1080, new FrameRate(50, 1)), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate));
    }

    // --- draft, Cancel, no change ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_the_draft_and_cancelling_changes_nothing()
    {
        Scene();
        var (top, changes, before) = (_undo.CurrentPosition, _timelineChanges, Snapshot());
        _dialog.Script = d =>
        {
            d.SelectedPreset = ProjectSettingsViewModel.AllPresets.First(p => p.Group == "Portrait");
            d.SwapCommand.Execute(null);
            (d.Width, d.Height) = (1234, 4000);
            d.SelectedRate = d.Rates.Single(r => r.Rate == FrameRate.Fps60);
            Assert.Equal(before, Snapshot());                           // the draft never reaches the project
            d.CancelCommand.Execute(null);
        };

        await _vm.Toolbar.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.True(_dialog.Closed);
        Assert.Equal(before, Snapshot());
        Assert.Same(top, _undo.CurrentPosition);
        Assert.Equal(changes, _timelineChanges);
        Assert.True(_undo.IsAtSavePoint);   // the dirty state's undo part (the scene's media add is not undoable)
    }

    [Fact]
    public void Apply_without_a_change_closes_with_no_step()
    {
        Scene();
        var (top, changes) = (_undo.CurrentPosition, _timelineChanges);
        var d = Draft();
        var closed = (bool?)null;
        d.CloseRequested += (_, applied) => closed = applied;

        d.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.Same(top, _undo.CurrentPosition);
        Assert.Equal(changes, _timelineChanges);
        Assert.True(_undo.IsAtSavePoint);   // the dirty state's undo part (the scene's media add is not undoable)
    }

    // --- errors in the dialog -------------------------------------------------------------------------------------------

    [Fact]
    public void The_canvas_rules_are_shown_live_and_apply_is_off_while_they_fail()
    {
        Scene();
        var d = Draft();
        Assert.True(d.CanApply);
        Assert.Null(d.CanvasError);

        (d.Width, d.Height) = (1921, 1080);
        Assert.Contains("even", d.CanvasError);
        Assert.False(d.ApplyCommand.CanExecute(null));
        Assert.Equal(ProjectSettingsViewModel.Custom, d.SelectedPreset);

        d.Width = 62;
        Assert.Contains("between 64 and 4096", d.CanvasError);
        d.Height = null;
        Assert.Equal("Enter the frame width and height.", d.CanvasError);
        (d.Width, d.Height) = (4096, 4096);
        Assert.Contains("too large", d.CanvasError);

        (d.Width, d.Height) = (1280, 720);
        Assert.Null(d.CanvasError);
        Assert.True(d.ApplyCommand.CanExecute(null));
        Assert.Equal(1280, d.SelectedPreset.Width);                     // a typed preset size selects the preset
        Assert.Equal("Positions and text sizes will be scaled by 66.67 %.", d.ScaleNotice);
    }

    [Fact]
    public void A_refusal_by_the_service_is_shown_in_the_open_dialog_and_changes_nothing()
    {
        var (_, title) = Scene();
        Assert.True(_edit.SetClipProperties(title.Id, new ClipPropertyChange { Text = TextProperties.Of(title)!.Value with { FontSize = 600 } }).Success);
        _undo.MarkSavePoint(_undo.CurrentPosition);
        var (top, changes, before) = (_undo.CurrentPosition, _timelineChanges, Snapshot());
        var d = Draft();
        var closed = false;
        d.CloseRequested += (_, _) => closed = true;
        d.SelectedPreset = ProjectSettingsViewModel.AllPresets.Single(p => p.Width == 3840);
        d.SelectedRate = d.Rates.Single(r => r.Rate == FrameRate.Fps30);

        d.ApplyCommand.Execute(null);

        Assert.False(closed);                                            // the dialog stays open …
        Assert.Contains("Font size", d.Error);                           // … with the reason under Apply
        Assert.Equal(before, Snapshot());
        Assert.Same(top, _undo.CurrentPosition);
        Assert.Equal(changes, _timelineChanges);
        Assert.True(_undo.IsAtSavePoint);   // the dirty state's undo part (the scene's media add is not undoable)

        d.SelectedPreset = ProjectSettingsViewModel.AllPresets.Single(p => p.Width == 1280);   // corrected: works
        Assert.Null(d.Error);
        d.ApplyCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal((1280, 720, FrameRate.Fps30), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate));
    }

    // --- export lock ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_command_is_off_during_an_export_and_apply_checks_the_lock_again()
    {
        Scene();
        Assert.True(_vm.Toolbar.ProjectSettingsCommand.CanExecute(null));
        using (_lock.Acquire())
        {
            Assert.False(_vm.Toolbar.ProjectSettingsCommand.CanExecute(null));
            await _workflow.RunAsync();                                  // reached anyway: refused, no dialog
            Assert.Equal(ProjectSettingsViewModel.LockedMessage, _status.Message);
            Assert.Equal(0, _dialog.Shown);
        }
        Assert.True(_vm.Toolbar.ProjectSettingsCommand.CanExecute(null));

        // An export that started while the dialog was open: Apply changes nothing.
        var before = Snapshot();
        var top = _undo.CurrentPosition;
        _dialog.Script = d =>
        {
            (d.Width, d.Height) = (1080, 1080);
            using (_lock.Acquire())
                d.ApplyCommand.Execute(null);
            Assert.Equal(ProjectSettingsViewModel.LockedMessage, d.Error);
            d.CancelCommand.Execute(null);
        };
        await _vm.Toolbar.ProjectSettingsCommand.ExecuteAsync(null);
        Assert.Equal(before, Snapshot());
        Assert.Same(top, _undo.CurrentPosition);
    }

    // --- frame-rate options ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_provisional_rate_is_offered_as_kept_and_choosing_it_explicitly_locks_it()
    {
        Assert.False(Settings.IsFrameRateLocked);
        var d = Draft();

        Assert.Equal("30 FPS (provisional)", d.SelectedRate.Label);
        Assert.True(d.SelectedRate.IsKeep);
        Assert.Equal(9, d.Rates.Count);                                  // kept + the eight project rates
        Assert.Equal("1920 × 1080 · 30 FPS (provisional)", _workflow.Summary);

        (d.Width, d.Height) = (1080, 1080);                              // a new size, the rate kept: still provisional
        d.ApplyCommand.Execute(null);
        Assert.Equal("Set Frame Size", ((IUndoableCommand)_undo.CurrentPosition).Description);
        Assert.False(Settings.IsFrameRateLocked);

        var again = Draft();
        again.SelectedRate = again.Rates.Single(r => !r.IsKeep && r.Rate == FrameRate.Default);
        Assert.Contains("will be kept", again.RateNotice);
        again.ApplyCommand.Execute(null);
        Assert.True(Settings.IsFrameRateLocked);
        Assert.Equal("1080 × 1080 · 30 FPS", _workflow.Summary);
        Assert.Equal(8, Draft().Rates.Count);                            // locked and offered: no extra entry
    }

    [Fact]
    public void A_rate_no_longer_offered_stays_available_as_current_until_replaced()
    {
        Settings.FrameRate = new FrameRate(15, 1);                       // e.g. fixed by a 15 fps first video
        Settings.IsFrameRateLocked = true;
        var d = Draft();

        Assert.Equal("15 FPS (current)", d.SelectedRate.Label);
        Assert.True(d.SelectedRate.IsKeep);
        Assert.Null(d.RateNotice);
        (d.Width, d.Height) = (1280, 720);
        d.ApplyCommand.Execute(null);
        Assert.Equal((1280, 720, new FrameRate(15, 1)), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate));

        var next = Draft();
        Assert.Equal("15 FPS (current)", next.Rates[0].Label);
        next.SelectedRate = next.Rates.Single(r => r.Rate == FrameRate.Fps25);
        next.ApplyCommand.Execute(null);
        Assert.Equal(FrameRate.Fps25, Settings.FrameRate);
        Assert.DoesNotContain(Draft().Rates, r => r.IsKeep);             // replaced: no longer offered
    }

    [Fact]
    public void Presets_cover_the_groups_and_swap_turns_the_size()
    {
        var groups = ProjectSettingsViewModel.AllPresets.Select(p => p.Group).Distinct().ToList();
        Assert.Equal(new[] { "Landscape", "Portrait", "Square", "4:5", "" }, groups);
        Assert.All(ProjectSettingsViewModel.AllPresets.Where(p => !p.IsCustom),
            p => Assert.Null(ProjectSettingsRules.CanvasError(p.Width, p.Height)));

        var d = Draft();
        Assert.Equal("Landscape — 1920 × 1080 (Full HD)", d.SelectedPreset.Label);
        d.SwapCommand.Execute(null);
        Assert.Equal((1080m, 1920m), (d.Width, d.Height));
        Assert.Equal("Portrait — 1080 × 1920 (Full HD)", d.SelectedPreset.Label);
    }

    [Fact]
    public void New_keeps_the_default_settings_and_opens_no_dialog()
    {
        Scene();
        Assert.True(_edit.SetProjectSettings(1080, 1920, FrameRate.Fps60).Success);

        _projects.CreateNew("Untitled Project");

        Assert.Equal((1920, 1080, FrameRate.Default, false), (Settings.FrameWidth, Settings.FrameHeight, Settings.FrameRate, Settings.IsFrameRateLocked));
        Assert.Equal(0, _dialog.Shown);
        Assert.Equal("Project Settings — 1920 × 1080 · 30 FPS (provisional)", _vm.Toolbar.ProjectSettingsToolTip);
    }

    private string Snapshot()
    {
        var s = Settings;
        var clips = _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks).SelectMany(t => t.Clips)
            .Select(c => $"{c.Id}:{c.TimelineStart.Ticks}-{c.TimelineEnd.Ticks}:{VisualProperties.Of(c)}:{TextProperties.Of(c)}");
        return $"{s.FrameWidth}x{s.FrameHeight}@{s.FrameRate}/{s.IsFrameRateLocked}|{_projects.Current.Timeline.PlayheadPosition.Ticks}|{string.Join(";", clips)}";
    }

    private sealed class ScriptedSettingsDialog : IProjectSettingsDialog
    {
        public Action<ProjectSettingsViewModel>? Script { get; set; }
        public int Shown { get; private set; }
        public bool Closed { get; private set; }

        public Task ShowAsync(ProjectSettingsViewModel settings)
        {
            Shown++;
            Closed = false;
            settings.CloseRequested += (_, _) => Closed = true;
            Script?.Invoke(settings);
            return Task.CompletedTask;
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
