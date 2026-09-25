using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 8 Step 7: the Export command through <see cref="ExportWorkflow"/> — preflight (errors stop, warnings may be
/// accepted), the output file picker, the replace question, the progress window fed by the service's
/// <see cref="ExportProgress"/>, cancellation by the token only, the editing lock, the result messages per failure
/// category and <c>LastExportSettings</c>. Real project and timeline services; the export service, the dialogs, the
/// picker and the progress window are scripted.
/// </summary>
public sealed class ExportWorkflowTests : IDisposable
{
    private const int Ok = 0, Continue = 0, Replace = 0, Cancel = 1;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly FakeExportService _export = new();
    private readonly FakeProgressDialog _progressDialog = new();
    private readonly ScriptedPicker _picker = new();
    private readonly RecordingLogger _log = new();
    private ScriptedDialogs _dialogs = new();

    public ExportWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ExportWorkflow Workflow(params int?[] answers)
    {
        _dialogs = new ScriptedDialogs(answers);
        return new ExportWorkflow(_projects, _export, new Fonts("Segoe UI"), _picker, _dialogs, _progressDialog, _lock, _status, _log);
    }

    private string Output(string name = "out.mp4") => Path.Combine(_root, name);

    /// <summary>A project that can be exported: one text clip (no media).</summary>
    private void ExportableProject()
    {
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        Assert.True(_edit.AddTextClip(MediaTime.Zero).Success);
    }

    private TextClip TextClip => _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().First();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    // --- Command -------------------------------------------------------------------------------

    [Fact]
    public void The_export_command_is_available_while_nothing_is_exported()
    {
        var toolbar = new ToolbarViewModel(_undo, null!, null!, _status, Workflow(), _lock);

        Assert.True(toolbar.ExportCommand.CanExecute(null));
        using (_lock.Acquire())
            Assert.False(toolbar.ExportCommand.CanExecute(null));
        Assert.True(toolbar.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_new_empty_project_cannot_be_exported()
    {
        // The editor always has a current project; with nothing on the timeline the preflight blocks.
        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.PreflightFailed, outcome.Kind);
        Assert.Contains("The timeline is empty.", Assert.Single(_dialogs.Asked).Message);
        Assert.Empty(_picker.SaveRequests);
        Assert.Empty(_export.Jobs);
    }

    // --- Preflight ------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_errors_are_listed_apart_from_warnings_and_nothing_is_encoded()
    {
        ExportableProject();
        TextClip.FontFamily = "No Such Font";                                     // warning
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "gone.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(5), FrameRate = FrameRate.Fps30, Width = 64, Height = 36 }
        };
        _projects.Current.MediaAssets.Add(asset);
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(10), Duration = MediaTime.FromSeconds(1),
            SourceOut = MediaTime.FromSeconds(1)
        });                                                                       // offline media: error

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.PreflightFailed, outcome.Kind);
        var message = Assert.Single(_dialogs.Asked).Message;
        var errors = message.IndexOf("Errors:", StringComparison.Ordinal);
        var warnings = message.IndexOf("Warnings:", StringComparison.Ordinal);
        Assert.True(errors >= 0 && warnings > errors, message);
        Assert.Contains("'gone.mp4' is offline", message[errors..warnings]);
        Assert.Contains("No Such Font", message[warnings..]);
        Assert.Empty(_picker.SaveRequests);
        Assert.Empty(_export.Jobs);
        Assert.Contains("Export not possible", _status.Message);
    }

    [Fact]
    public async Task Warnings_only_can_be_accepted()
    {
        ExportableProject();
        TextClip.FontFamily = "No Such Font";
        _picker.SaveFiles.Enqueue(Output());

        var outcome = await Workflow(Continue, Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal("Export warnings", _dialogs.Asked[0].Title);
        Assert.Contains("No Such Font", _dialogs.Asked[0].Message);
        Assert.Single(_export.Jobs);
    }

    [Fact]
    public async Task Warnings_can_stop_the_export()
    {
        ExportableProject();
        TextClip.FontFamily = "No Such Font";

        var outcome = await Workflow(Cancel).RunAsync();

        Assert.Equal(ExportOutcomeKind.NotStarted, outcome.Kind);
        Assert.Empty(_picker.SaveRequests);
        Assert.Empty(_export.Jobs);
    }

    [Fact]
    public async Task Missing_ffmpeg_is_a_preflight_error()
    {
        ExportableProject();
        _export.Available = false;

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.PreflightFailed, outcome.Kind);
        Assert.Contains("ffmpeg could not be found", Assert.Single(_dialogs.Asked).Message);
        Assert.Empty(_export.Jobs);
    }

    // --- Output file ------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_the_file_picker_ends_the_flow_silently()
    {
        ExportableProject();
        _status.Report("before");

        var outcome = await Workflow().RunAsync();

        Assert.Equal(ExportOutcomeKind.NotStarted, outcome.Kind);
        Assert.Single(_picker.SaveRequests);
        Assert.Empty(_dialogs.Asked);
        Assert.Empty(_export.Jobs);
        Assert.Equal("before", _status.Message);
    }

    [Fact]
    public async Task The_picker_asks_for_an_mp4_named_after_the_project_or_the_last_export()
    {
        ExportableProject();
        await Workflow().RunAsync();
        var first = _picker.SaveRequests[^1];
        Assert.Equal(_projects.Current.Name + ".mp4", first.SuggestedFileName);
        Assert.Equal("mp4", first.DefaultExtension);
        Assert.Equal(new[] { "*.mp4" }, Assert.Single(first.FileTypeFilters).Patterns);

        _projects.Current.LastExportSettings.OutputPath = Output("last.mp4");
        await Workflow().RunAsync();
        var second = _picker.SaveRequests[^1];
        Assert.Equal(("last.mp4", _root), (second.SuggestedFileName, second.StartFolder));
    }

    [Fact]
    public async Task Another_extension_is_not_replaced_but_reported()
    {
        ExportableProject();
        _picker.SaveFiles.Enqueue(Output("clip.mov"));

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.PreflightFailed, outcome.Kind);
        Assert.Contains(".mp4", Assert.Single(_dialogs.Asked).Message);
        Assert.Empty(_export.Jobs);
    }

    [Fact]
    public async Task An_existing_file_is_replaced_only_after_confirmation()
    {
        ExportableProject();
        var existing = Output();
        File.WriteAllText(existing, "old");

        _picker.SaveFiles.Enqueue(existing);
        var declined = await Workflow(Cancel).RunAsync();
        Assert.Equal(ExportOutcomeKind.NotStarted, declined.Kind);
        Assert.Equal("Replace file?", Assert.Single(_dialogs.Asked).Title);
        Assert.Empty(_export.Jobs);

        _picker.SaveFiles.Enqueue(existing);
        var accepted = await Workflow(Replace, Ok).RunAsync();
        Assert.Equal(ExportOutcomeKind.Succeeded, accepted.Kind);
        Assert.Equal(existing, Assert.Single(_export.Jobs).OutputPath);
        Assert.Equal("old", File.ReadAllText(existing));                         // the workflow itself never writes it
    }

    // --- Success, progress, session state ------------------------------------------------------------

    [Fact]
    public async Task Success_reports_the_path_and_remembers_it_without_making_the_project_dirty()
    {
        ExportableProject();
        var dirty = _projects.Current.IsDirty;
        var canUndo = _undo.CanUndo;
        var saveStateChanges = 0;
        _projects.SaveStateChanged += (_, _) => saveStateChanges++;
        _picker.SaveFiles.Enqueue(Output());

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal(ExportOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal(Output(), outcome.OutputPath);
        var finished = Assert.Single(_dialogs.Asked);
        Assert.Equal("Export finished", finished.Title);
        Assert.Contains(Output(), finished.Message);
        Assert.Contains(Output(), _status.Message);
        Assert.Equal(Output(), _projects.Current.LastExportSettings.OutputPath);
        Assert.Equal((dirty, canUndo, 0), (_projects.Current.IsDirty, _undo.CanUndo, saveStateChanges));
        Assert.Equal((1, 1, false), (_progressDialog.Shown, _progressDialog.Closed, _progressDialog.IsOpen));
        Assert.False(_lock.IsLocked);
        Assert.Single(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips));        // nothing imported
    }

    [Fact]
    public async Task Progress_shows_the_service_values_stage_by_stage_and_success_only_after_the_task()
    {
        ExportableProject();
        _picker.SaveFiles.Enqueue(Output());
        var steps = _export.Stepwise();
        var workflow = Workflow(Ok);
        var run = workflow.RunAsync();
        await steps.Started;
        var progress = _progressDialog.Current!;
        Assert.Same(progress, workflow.Progress);

        var seen = new List<(ExportStage?, long, long, double, string)>();
        foreach (var report in FakeExportService.Reports(steps.Job))
        {
            await steps.Next(report);
            progress.Refresh();
            seen.Add((progress.Stage, progress.Done, progress.Total, progress.Percent, progress.DetailText));
            Assert.False(run.IsCompleted);
            Assert.True(_progressDialog.IsOpen);
        }
        Assert.Contains(seen, s => s is (ExportStage.Audio, 48_000, 96_000, 50.0, _));
        Assert.Contains((ExportStage.Video, 25L, 100L, 25.0, "25 / 100 frames (25 %)"), seen);
        Assert.Equal(new ExportStage?[] { ExportStage.Preparing, ExportStage.Audio, ExportStage.Video, ExportStage.Finalizing },
            seen.Select(s => s.Item1).Distinct());
        Assert.Equal((ExportStage.Finalizing, 1L, 1L), (seen[^1].Item1, seen[^1].Item2, seen[^1].Item3));
        Assert.Empty(_dialogs.Asked);                                              // Finalizing 1/1 is not success yet

        steps.Complete();
        var outcome = await run;
        Assert.Equal(ExportOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal("Export finished", Assert.Single(_dialogs.Asked).Title);
        Assert.False(_progressDialog.IsOpen);
        Assert.Null(workflow.Progress);
    }

    [Fact]
    public async Task The_export_uses_the_snapshot_taken_when_it_started()
    {
        ExportableProject();
        var duration = _projects.Current.Timeline.Duration();
        _picker.SaveFiles.Enqueue(Output());
        var steps = _export.Stepwise();
        var run = Workflow(Ok).RunAsync();
        await steps.Started;

        Assert.True(_edit.AddTextClip(duration).Success);                         // a model change (not through the locked UI)

        Assert.Equal(duration, steps.Job.Snapshot.Duration);
        steps.Complete();
        await run;
    }

    // --- Cancellation -------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_only_cancels_the_token_and_the_window_closes_after_the_export_unwound()
    {
        ExportableProject();
        File.WriteAllText(Output(), "old");
        _picker.SaveFiles.Enqueue(Output());
        var unwind = _export.WaitForCancellation();
        var workflow = Workflow(Replace);
        var toolbar = new ToolbarViewModel(_undo, null!, null!, _status, workflow, _lock);
        var run = workflow.RunAsync();
        await _export.Started;

        _progressDialog.Current!.CancelCommand.Execute(null);
        await WaitUntil(() => _export.Token.IsCancellationRequested);

        // Still unwinding: the window stays, editing stays locked, no second export.
        Assert.True(_progressDialog.IsOpen);
        Assert.Equal("Cancelling…", _progressDialog.Current!.StageText);
        Assert.False(_progressDialog.Current.CancelCommand.CanExecute(null));
        Assert.True(_lock.IsLocked);
        Assert.False(toolbar.ExportCommand.CanExecute(null));
        Assert.Equal(ExportOutcomeKind.NotStarted, (await workflow.RunAsync()).Kind);
        Assert.False(run.IsCompleted);

        unwind.SetResult();
        var outcome = await run;

        Assert.Equal(ExportOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(_progressDialog.IsOpen);
        Assert.False(_lock.IsLocked);
        Assert.DoesNotContain(_dialogs.Asked, d => d.Title == "Export failed");
        Assert.StartsWith("Export cancelled.", _status.Message);
        Assert.Contains("left unchanged", _status.Message);
        Assert.Equal("old", File.ReadAllText(Output()));
        Assert.Equal("", _projects.Current.LastExportSettings.OutputPath);
        Assert.True(toolbar.ExportCommand.CanExecute(null));
        Assert.Single(_export.Jobs);
    }

    [Fact]
    public async Task Closing_the_progress_window_requests_cancellation()
    {
        ExportableProject();
        _picker.SaveFiles.Enqueue(Output());
        var unwind = _export.WaitForCancellation();
        var run = Workflow().RunAsync();
        await _export.Started;

        _progressDialog.Current!.Cancel();                                         // what the window's Closing does
        unwind.SetResult();

        Assert.Equal(ExportOutcomeKind.Cancelled, (await run).Kind);
    }

    // --- Editing lock -------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_is_locked_while_exporting_and_restored_afterwards()
    {
        ExportableProject();
        var toolbar = new ToolbarViewModel(_undo, null!, null!, _status, Workflow(Ok), _lock);
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);
        var inspector = new InspectorViewModel(_edit, _status, null, _lock);
        var media = new MediaBrowserViewModel(_projects, null!, NullLogger<MediaBrowserViewModel>.Instance, _lock);
        inspector.ShowClip(new TimelineClipSelection(TextClip, "Text", null, _projects.Current.Settings.FrameRate));
        _picker.SaveFiles.Enqueue(Output());
        var steps = _export.Stepwise();
        var workflow = Workflow(Ok);
        var run = workflow.RunAsync();
        await steps.Started;

        var mutating = new System.Windows.Input.ICommand[]
        {
            toolbar.NewProjectCommand, toolbar.OpenCommand, toolbar.SaveCommand, toolbar.SaveAsCommand, toolbar.UndoCommand,
            toolbar.ImportMediaCommand, toolbar.ExportCommand, timeline.SplitAtPlayheadCommand, timeline.AddVideoTrackCommand,
            timeline.AddAudioTrackCommand, timeline.AddTextCommand, media.ImportCommand
        };
        Assert.All(mutating, c => Assert.False(c.CanExecute(null)));
        Assert.False(timeline.IsEditingAllowed);
        Assert.False(inspector.IsEditingAllowed);
        // Viewing stays available.
        Assert.True(timeline.ZoomInCommand.CanExecute(null));
        Assert.True(timeline.StepForwardCommand.CanExecute(null));

        // A field changed anyway (e.g. focused before) is not applied.
        var opacity = TextClip.Opacity;
        var clips = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).Count();
        inspector.OpacityPercent = 10;
        Assert.Equal(opacity, TextClip.Opacity);
        Assert.Equal(100m, inspector.OpacityPercent);
        timeline.AddMedia(new MediaAsset { FilePath = "x.mp4", Kind = MediaKind.Video });
        Assert.Equal(clips, _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).Count());

        steps.Complete();
        await run;

        Assert.All(mutating, c => Assert.True(c.CanExecute(null), c.GetType().Name));
        Assert.True(timeline.IsEditingAllowed);
        Assert.True(inspector.IsEditingAllowed);
    }

    // --- Failures -----------------------------------------------------------------------------------

    public static TheoryData<ExportFailure, string> Failures => new()
    {
        { ExportFailure.EncoderUnavailable, "ffmpeg could not be found" },
        { ExportFailure.DecodeFailed, "A source file could not be read" },
        { ExportFailure.EncodeFailed, "The video could not be encoded" },
        { ExportFailure.OutputFailed, "The output file could not be written" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Each_failure_category_has_its_own_message(ExportFailure failure, string expected)
    {
        ExportableProject();
        _picker.SaveFiles.Enqueue(Output());
        _export.Throw = new ExportException(failure, "detail");

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal((ExportOutcomeKind.Failed, (ExportFailure?)failure), (outcome.Kind, outcome.Failure));
        var shown = Assert.Single(_dialogs.Asked);
        Assert.Equal("Export failed", shown.Title);
        Assert.StartsWith(expected, shown.Message);
        Assert.Contains("detail", shown.Message);
        Assert.Equal(outcome.Message, _status.Message);
        Assert.False(_progressDialog.IsOpen);
        Assert.False(_lock.IsLocked);
        Assert.Equal("", _projects.Current.LastExportSettings.OutputPath);
        Assert.Same(_export.Throw, _log.Exceptions.Single());
    }

    [Fact]
    public async Task An_unexpected_error_is_a_generic_failure_and_is_logged_with_the_exception()
    {
        ExportableProject();
        _picker.SaveFiles.Enqueue(Output());
        var thrown = new InvalidOperationException("The render target could not be created.");
        _export.Throw = thrown;

        var outcome = await Workflow(Ok).RunAsync();

        Assert.Equal((ExportOutcomeKind.Failed, (ExportFailure?)null), (outcome.Kind, outcome.Failure));
        var shown = Assert.Single(_dialogs.Asked);
        Assert.Equal("Export failed", shown.Title);
        Assert.Contains("unexpected error", shown.Message);
        Assert.Contains("render target", shown.Message);
        var (level, exception) = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Same(thrown, exception);
        Assert.False(_progressDialog.IsOpen);
    }

    [Fact]
    public async Task Distinct_messages_per_outcome()
    {
        var messages = new[]
        {
            new ExportException(ExportFailure.EncoderUnavailable, "x"), new ExportException(ExportFailure.DecodeFailed, "x"),
            new ExportException(ExportFailure.EncodeFailed, "x"), new ExportException(ExportFailure.OutputFailed, "x")
        }.Select(ExportWorkflow.FailureMessage).ToList();
        Assert.Equal(messages.Count, messages.Distinct().Count());

        ExportableProject();
        _picker.SaveFiles.Enqueue(Output());
        var unwind = _export.WaitForCancellation();
        var run = Workflow().RunAsync();
        await _export.Started;
        _progressDialog.Current!.Cancel();
        unwind.SetResult();
        var cancelled = await run;
        Assert.DoesNotContain(cancelled.Message, messages);
        Assert.Empty(_log.Exceptions);                                              // cancellation is not logged as a failure
    }

    // --- Fakes --------------------------------------------------------------------------------------

    private sealed class Fonts(params string[] names) : IFontCatalog
    {
        public IReadOnlyList<string> FamilyNames { get; } = names;
    }

    private sealed class FakeProgressDialog : IExportProgressDialog
    {
        public ExportProgressViewModel? Current { get; private set; }
        public int Shown { get; private set; }
        public int Closed { get; private set; }
        public bool IsOpen => Current is not null;

        public void Show(ExportProgressViewModel progress)
        {
            Assert.Null(Current);
            Current = progress;
            Shown++;
        }

        public void Close()
        {
            Current = null;
            Closed++;
        }
    }

    private sealed class RecordingLogger : ILogger<ExportWorkflow>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IEnumerable<Exception> Exceptions => Entries.Where(e => e.Exception is not null).Select(e => e.Exception!);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Entries.Add((logLevel, exception));
        }
    }

    /// <summary>The export service: succeeds at once, throws <see cref="Throw"/>, waits for cancellation, or is
    /// stepped by the test.</summary>
    private sealed class FakeExportService : IExportService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<ExportJob, IProgress<ExportProgress>?, CancellationToken, Task>? _behaviour;

        public bool Available { get; set; } = true;
        public Exception? Throw { get; set; }
        public List<ExportJob> Jobs { get; } = new();
        public CancellationToken Token { get; private set; }
        public Task Started => _started.Task;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public async Task ExportAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct = default)
        {
            Jobs.Add(job);
            Token = ct;
            _started.TrySetResult();
            await Task.Yield();                                                      // like the real service: off the caller
            if (Throw is not null) throw Throw;
            if (_behaviour is not null) await _behaviour(job, progress, ct);
            else foreach (var report in Reports(job)) progress?.Report(report);
        }

        /// <summary>The service's report sequence for <paramref name="job"/>'s kind of output (fixed totals here).</summary>
        public static IEnumerable<ExportProgress> Reports(ExportJob? job)
        {
            yield return new ExportProgress(ExportStage.Preparing, 0, 1);
            yield return new ExportProgress(ExportStage.Audio, 0, 96_000);
            yield return new ExportProgress(ExportStage.Audio, 48_000, 96_000);
            yield return new ExportProgress(ExportStage.Audio, 96_000, 96_000);
            yield return new ExportProgress(ExportStage.Video, 0, 100);
            yield return new ExportProgress(ExportStage.Video, 25, 100);
            yield return new ExportProgress(ExportStage.Video, 100, 100);
            yield return new ExportProgress(ExportStage.Finalizing, 0, 1);
            yield return new ExportProgress(ExportStage.Finalizing, 1, 1);
        }

        /// <summary>Waits for cancellation, then keeps "unwinding" until the returned source is completed.</summary>
        public TaskCompletionSource WaitForCancellation()
        {
            var unwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _behaviour = async (_, progress, ct) =>
            {
                progress?.Report(new ExportProgress(ExportStage.Video, 3, 100));
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { await unwind.Task; }
            };
            return unwind;
        }

        public Steps Stepwise()
        {
            var steps = new Steps(this);
            _behaviour = steps.RunAsync;
            return steps;
        }

        public sealed class Steps(FakeExportService owner)
        {
            private readonly SemaphoreSlim _next = new(0);
            private readonly SemaphoreSlim _done = new(0);
            private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private ExportProgress _report;
            private IProgress<ExportProgress>? _progress;

            public Task Started => owner.Started;
            public ExportJob Job => owner.Jobs[^1];

            public async Task RunAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct)
            {
                _progress = progress;
                while (!_finish.Task.IsCompleted)
                {
                    var next = _next.WaitAsync(ct);
                    if (await Task.WhenAny(next, _finish.Task) != next) break;
                    _progress?.Report(_report);
                    _done.Release();
                }
            }

            /// <summary>Lets the service report <paramref name="report"/> and waits until it did.</summary>
            public async Task Next(ExportProgress report)
            {
                await Started;
                _report = report;
                _next.Release();
                await _done.WaitAsync(TimeSpan.FromSeconds(10));
            }

            public void Complete() => _finish.TrySetResult();
        }
    }
}
