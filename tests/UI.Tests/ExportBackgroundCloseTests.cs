using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
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
/// D060 §5: the export in the background as the main window sees it — the compact indicator (shown while it runs, its
/// window, its ✕ that asks first) and closing the application while it runs: never silently — «Продолжить экспорт» keeps
/// the window open, «Отменить экспорт и закрыть» asks about unsaved changes, then cancels the export and waits for its end
/// before the window may close; Cancel at the unsaved question leaves the export running.
/// </summary>
public sealed class ExportBackgroundCloseTests : IAsyncLifetime
{
    private const int KeepExporting = 0, CancelAndClose = 1;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly BlockingExport _export = new();
    private readonly ProgressWindow _window = new();
    private readonly ScriptedPicker _picker = new();
    private ScriptedDialogs _dialogs = new();

    public ExportBackgroundCloseTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        Assert.True(_edit.AddTextClip(MediaTime.Zero).Success);
        _projects.Current.IsDirty = false;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _export.Release();
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (MainWindowViewModel Vm, ExportWorkflow Export) Shell(params int?[] answers) => Shell(new ScriptedDialogs(answers));

    private (MainWindowViewModel Vm, ExportWorkflow Export) Shell(ScriptedDialogs dialogs)
    {
        _dialogs = dialogs;
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(_picker, new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), _dialogs, _picker, _status, NullLogger<ProjectFileWorkflow>.Instance);
        var export = new ExportWorkflow(_projects, _export, new Fonts(), _picker, _dialogs, _window, new EditingLock(), _status,
            NullLogger<ExportWorkflow>.Instance, projectFiles: files);
        var vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status, export),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance,
            export: export, dialogs: _dialogs);
        return (vm, export);
    }

    private async Task<Task<ExportOutcome>> StartExport(ExportWorkflow export)
    {
        _picker.SaveFiles.Enqueue(Path.Combine(_root, "out.mp4"));
        var run = export.RunAsync();
        await _export.Started;
        return run;
    }

    [Fact]
    public async Task The_indicator_shows_the_running_export_and_opens_its_window()
    {
        var (vm, export) = Shell();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.IsExportActive);

        var run = await StartExport(export);
        Assert.True(vm.IsExportActive);
        Assert.Same(export.Progress, vm.ExportProgress);
        Assert.Contains(nameof(MainWindowViewModel.IsExportActive), changed);
        vm.ShowExportCommand.Execute(null);
        Assert.Equal(1, _window.Activated);

        _export.Release();
        Assert.Equal(ExportOutcomeKind.Succeeded, (await run).Kind);
        Assert.False(vm.IsExportActive);
        Assert.Null(vm.ExportProgress);
    }

    [Fact]
    public async Task The_indicator_s_cancel_asks_first()
    {
        var (vm, export) = Shell(1 /* Continue export */, 0 /* Cancel export */);
        var run = await StartExport(export);

        await vm.CancelExportCommand.ExecuteAsync(null);
        Assert.Equal("Cancel the export?", _dialogs.Asked[^1].Title);
        Assert.False(_export.Token.IsCancellationRequested);

        await vm.CancelExportCommand.ExecuteAsync(null);
        Assert.True(_export.Token.IsCancellationRequested);
        Assert.Equal(ExportOutcomeKind.Cancelled, (await run).Kind);
    }

    [Fact]
    public async Task An_export_that_ends_while_the_indicator_s_question_is_open_is_not_cancelled_afterwards()
    {
        Task<ExportOutcome>? run = null;
        var ended = false;
        // The export finishes while «Отменить экспорт?» is open; the user then answers «Отменить экспорт».
        var (vm, export) = Shell(new ScriptedDialogs(0, 0)
        {
            OnAsk = () =>
            {
                if (ended || run is null) return;
                ended = true;
                _export.Release();
                run.Wait(TimeSpan.FromSeconds(10));
            }
        });
        run = await StartExport(export);

        await vm.CancelExportCommand.ExecuteAsync(null);                         // no ObjectDisposedException

        Assert.True(ended);
        Assert.Equal(ExportOutcomeKind.Succeeded, (await run).Kind);
        Assert.False(vm.IsExportActive);
    }

    [Fact]
    public async Task A_cancel_after_the_export_ended_does_nothing()
    {
        var (_, export) = Shell();
        var run = await StartExport(export);
        var progress = export.Progress!;
        _export.Release();
        await run;
        progress.Cancel();                                                       // the token source is gone: harmless
        Assert.True(progress.IsCancelling);
    }

    [Fact]
    public async Task Closing_while_exporting_asks_and_Continue_export_keeps_the_window_open()
    {
        var (vm, export) = Shell(KeepExporting);
        var run = await StartExport(export);

        Assert.False(await vm.PrepareToCloseAsync());

        var question = Assert.Single(_dialogs.Asked);
        Assert.Equal("Export in progress", question.Title);
        Assert.Equal(new[] { "Continue export", "Cancel export and close" }, question.Buttons);
        Assert.False(_export.Token.IsCancellationRequested);
        Assert.True(export.IsExporting);
        _export.Release();
        Assert.Equal(ExportOutcomeKind.Succeeded, (await run).Kind);
    }

    [Fact]
    public async Task Cancel_export_and_close_cancels_and_waits_for_the_export_before_the_window_may_close()
    {
        var (vm, export) = Shell(CancelAndClose);
        var run = await StartExport(export);
        _export.HoldUnwinding();

        var closing = vm.PrepareToCloseAsync();
        await WaitUntil(() => _export.Token.IsCancellationRequested);
        Assert.False(closing.IsCompleted);                                          // waits for the export's cleanup
        _export.Release();

        Assert.True(await closing);
        Assert.False(export.IsExporting);
        Assert.Equal(ExportOutcomeKind.Cancelled, (await run).Kind);
        Assert.Single(_dialogs.Asked);                                              // a saved project: no second question
    }

    [Fact]
    public async Task Cancel_at_the_unsaved_question_after_Cancel_export_and_close_leaves_the_export_running()
    {
        var (vm, export) = Shell(CancelAndClose, 2 /* Cancel at Save / Don't Save / Cancel */);
        var run = await StartExport(export);
        Assert.True(_edit.AddTextClip(MediaTime.FromSeconds(20)).Success);         // edited during the export: unsaved

        Assert.False(await vm.PrepareToCloseAsync());

        Assert.Equal(2, _dialogs.Asked.Count);
        Assert.False(_export.Token.IsCancellationRequested);
        Assert.True(export.IsExporting);
        _export.Release();
        Assert.Equal(ExportOutcomeKind.Succeeded, (await run).Kind);
    }

    [Fact]
    public async Task Closing_without_an_export_asks_nothing_about_it()
    {
        var (vm, _) = Shell();
        Assert.True(await vm.PrepareToCloseAsync());
        Assert.Empty(_dialogs.Asked);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class Fonts : IFontCatalog
    {
        public IReadOnlyList<string> FamilyNames { get; } = new[] { "Segoe UI" };
    }

    private sealed class ProgressWindow : IExportProgressDialog
    {
        public int Activated { get; private set; }
        private bool _open;
        public void Show(ExportProgressViewModel progress) => _open = true;
        public void Activate() { if (_open) Activated++; }
        public void Close() => _open = false;
    }

    /// <summary>Runs until released; on cancellation it unwinds at once, or — <see cref="HoldUnwinding"/> — only when released.</summary>
    private sealed class BlockingExport : IExportService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdUnwinding;

        public Task Started => _started.Task;
        public CancellationToken Token { get; private set; }

        public void Release() => _release.TrySetResult();
        public void HoldUnwinding() => _holdUnwinding = true;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task ExportAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct = default)
        {
            Token = ct;
            _started.TrySetResult();
            await Task.Yield();
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (_holdUnwinding) await _release.Task;
                throw;
            }
        }
    }
}
