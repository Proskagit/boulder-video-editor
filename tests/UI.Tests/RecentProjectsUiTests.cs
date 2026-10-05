using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiVideoEditor.UI.ViewModels.Panels.RecentProjectAvailability;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// The <c>Recent ▾</c> drop-down (D026 §6, Step 11.8): reading the list at every opening, Checking / Available /
/// Unavailable, stale and duplicate checks, opening through the workflow, removing, the editing lock. Real ProjectService,
/// ProjectFileWorkflow and RecentProjectsStore on temporary files; the store is wrapped only to hold availability checks
/// (a slow drive), count them and make calls fail. Everything runs on a UI-thread stand-in, as in the app.
/// </summary>
public sealed class RecentProjectsUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly RecentProjectsStore _real;
    private readonly ControlledStore _store;
    private readonly QueuedDialogs _dialogs = new();
    private readonly ProjectFileWorkflow _workflow;
    private readonly RecentProjectsViewModel _recent;
    private readonly UiThread _ui = new();

    public RecentProjectsUiTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _real = new RecentProjectsStore(Path.Combine(_root, "config", "recent-projects.json"), NullLogger<RecentProjectsStore>.Instance);
        _store = new ControlledStore(_real);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        _workflow = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), _dialogs, new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance, recentProjects: _store);
        _recent = new RecentProjectsViewModel(_store, _workflow, _status, _lock);
    }

    public void Dispose()
    {
        _store.ReleaseAll();
        _ui.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Success(new MediaMetadata { Duration = new MediaTime(1) }));
    }

    /// <summary>The real store, with availability checks that can be held (a slow drive) or fail, counted per folder, and a
    /// removal that can fail.</summary>
    private sealed class ControlledStore(RecentProjectsStore inner) : IRecentProjectsStore
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Folder, int Thread)> Checks { get; } = new();
        public ConcurrentQueue<string> Removes { get; } = new();
        public int Reads;
        public Func<string, bool> CheckThrows { get; set; } = _ => false;
        public bool RemoveFails { get; set; }
        public bool RemoveThrows { get; set; }

        public void Hold(string folder) => _holds[folder] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string folder) { if (_holds.TryRemove(folder, out var h)) h.TrySetResult(); }
        public void ReleaseAll() { foreach (var key in _holds.Keys) Release(key); }
        public int ChecksOf(string folder) => Checks.Count(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));

        /// <summary>Holds the next reading after it read the file — a reading that answers late with what was listed.</summary>
        public TaskCompletionSource? HoldNextRead { get; set; }

        public async Task<IReadOnlyList<RecentProject>> GetAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            var hold = HoldNextRead;
            HoldNextRead = null;
            var list = await inner.GetAsync(ct);
            if (hold is not null) await hold.Task;
            return list;
        }

        public Task<bool> AddAsync(string projectFolderPath, string projectName, CancellationToken ct = default) =>
            inner.AddAsync(projectFolderPath, projectName, ct);

        public async Task<bool> RemoveAsync(string projectFolderPath, CancellationToken ct = default)
        {
            Removes.Enqueue(projectFolderPath);
            if (RemoveThrows) throw new IOException("broken");
            if (RemoveFails) return false;
            return await inner.RemoveAsync(projectFolderPath, ct);
        }

        public async Task<bool> IsAvailableAsync(string projectFolderPath, CancellationToken ct = default)
        {
            Checks.Enqueue((projectFolderPath, Environment.CurrentManagedThreadId));
            if (_holds.TryGetValue(projectFolderPath, out var hold)) await hold.Task;
            if (CheckThrows(projectFolderPath)) throw new UnauthorizedAccessException("no access");
            return await inner.IsAvailableAsync(projectFolderPath, ct);
        }
    }

    /// <summary>Answers dialogs from <see cref="Answers"/> (none left = closed without choosing) and records them.</summary>
    private sealed class QueuedDialogs : IDialogService
    {
        public Queue<int?> Answers { get; } = new();
        public List<DialogRequest> Asked { get; } = new();

        public Task<int?> AskAsync(DialogRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
        }
    }

    /// <summary>A UI thread stand-in: one dedicated thread that runs everything posted to its synchronization context, as
    /// the Avalonia dispatcher does (continuations of an await started on it come back to it).</summary>
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted) _queue.Add((d, state));
        }

        /// <summary>Starts <paramref name="work"/> on the UI thread; the task completes when it does.</summary>
        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { done.SetResult(await work()); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    private sealed class NamedEdit(IProjectService projects, string name) : IUndoableCommand
    {
        private string _old = "";
        public string Description => "Rename";
        public void Execute() { _old = projects.Current.Timeline.Name; projects.Current.Timeline.Name = name; projects.NotifyTimelineChanged(); }
        public void Undo() { projects.Current.Timeline.Name = _old; projects.NotifyTimelineChanged(); }
    }

    private string WriteProject(string folderName, string? projectName = null)
    {
        var folder = Path.Combine(_root, "projects", folderName);
        var project = new Core.Entities.Project { Name = projectName ?? folderName };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    private string Gone(string name) => Path.Combine(_root, "projects", name);

    /// <summary>Lists the folders, the first one most recent.</summary>
    private async Task List(params (string Folder, string Name)[] entries)
    {
        foreach (var (folder, name) in entries.Reverse())
            Assert.True(await _real.AddAsync(folder, name));
    }

    private Task OnUi(Func<Task> work) => _ui.Run(async () => { await work(); return true; });

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), what);
    }

    private RecentProjectItemViewModel Item(string folder) =>
        _recent.Items.Single(i => string.Equals(i.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

    private async Task OpenDropDownAndSettle()
    {
        await _recent.OnOpenedAsync();
        await Eventually(() => _recent.Items.All(i => i.Availability != Checking), "a check did not finish");
    }

    // ---- reading the list -------------------------------------------------------------------------------------------

    [Fact]
    public Task An_empty_list_shows_the_empty_state() => OnUi(async () =>
    {
        Assert.False(_recent.IsEmpty);   // not read yet

        await _recent.OnOpenedAsync();

        Assert.Empty(_recent.Items);
        Assert.True(_recent.IsEmpty);
        Assert.True(_recent.IsEnabled);
    });

    [Fact]
    public Task The_entries_are_shown_in_the_stores_order_with_name_and_folder() => OnUi(async () =>
    {
        var a = WriteProject("A", "Alpha");
        var b = WriteProject("B", "Beta");
        await List((b, "Beta"), (a, "Alpha"));

        await OpenDropDownAndSettle();

        Assert.Equal(new[] { b, a }, _recent.Items.Select(i => i.FolderPath));
        Assert.Equal(new[] { "Beta", "Alpha" }, _recent.Items.Select(i => i.Name));
        Assert.False(_recent.IsEmpty);
    });

    [Fact]
    public Task Projects_of_one_name_are_told_apart_by_their_folder_and_long_names_are_kept_whole() => OnUi(async () =>
    {
        var longName = new string('L', 300);
        var one = WriteProject(Path.Combine("x", "Film"), "Film");
        var two = WriteProject(Path.Combine("y", "Film"), "Film");
        var three = WriteProject("Long", longName);
        await List((one, "Film"), (two, "Film"), (three, longName));

        await OpenDropDownAndSettle();

        Assert.Equal(3, _recent.Items.Count);
        Assert.Equal(new[] { "Film", "Film" }, _recent.Items.Take(2).Select(i => i.Name));
        Assert.NotEqual(_recent.Items[0].FolderPath, _recent.Items[1].FolderPath);
        Assert.Contains(one, _recent.Items[0].ToolTip);
        Assert.Equal(longName, _recent.Items[2].Name);   // trimmed by the view (TextTrimming), never in the model
    });

    [Fact]
    public Task Every_opening_reads_the_list_again() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        await OpenDropDownAndSettle();
        _recent.OnClosed();

        var b = WriteProject("B");
        await _real.AddAsync(b, "B");                       // e.g. another instance
        await OpenDropDownAndSettle();

        Assert.Equal(2, _store.Reads);
        Assert.Equal(new[] { b, a }, _recent.Items.Select(i => i.FolderPath));
    });

    [Fact]
    public Task Open_save_as_and_recover_show_at_the_next_opening() => OnUi(async () =>
    {
        var a = WriteProject("A");
        Assert.True(await _workflow.OpenAsync(a));
        await OpenDropDownAndSettle();
        Assert.Equal(new[] { a }, _recent.Items.Select(i => i.FolderPath));
        _recent.OnClosed();

        var saved = Path.Combine(_root, "projects", "Saved");
        var saveAs = new ProjectFileWorkflow(_projects, new MediaAnalysisCoordinator(new NoAnalysis(), _projects,
                NullLogger<MediaAnalysisCoordinator>.Instance), new NullAutosave(), _dialogs, new ScriptedPicker(saved), _status,
            NullLogger<ProjectFileWorkflow>.Instance, recentProjects: _store);
        Assert.True(await saveAs.SaveAsAsync());

        await OpenDropDownAndSettle();
        Assert.Equal(new[] { saved, a }, _recent.Items.Select(i => i.FolderPath));
        Assert.All(_recent.Items, i => Assert.Equal(Available, i.Availability));
    });

    [Fact]
    public Task A_reading_overtaken_by_a_later_one_is_dropped() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));

        var first = _recent.OnOpenedAsync();
        var second = _recent.OnOpenedAsync();
        await Task.WhenAll(first, second);

        Assert.Single(_recent.Items);
    });

    [Fact]
    public Task A_late_reading_never_replaces_a_newer_one() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldNextRead = held;

        var first = _recent.OnOpenedAsync();                                // reads [A], answers late
        await Eventually(() => _store.Reads == 1, "not read");
        await Task.Delay(50);
        var b = WriteProject("B");
        await _real.AddAsync(b, "B");
        _recent.OnClosed();
        await _recent.OnOpenedAsync();                                      // reads [B, A]
        held.SetResult();
        await first;

        Assert.Equal(new[] { b, a }, _recent.Items.Select(i => i.FolderPath));
    });

    [Fact]
    public Task A_reading_started_before_a_removal_does_not_bring_the_entry_back() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        await OpenDropDownAndSettle();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldNextRead = held;

        var reading = _recent.RefreshAsync();                              // reads [A, B], answers late
        await Eventually(() => _store.Reads == 2, "not read");
        await Task.Delay(50);
        await Item(a).RemoveCommand.ExecuteAsync(null);
        held.SetResult();
        await reading;

        Assert.Equal(new[] { b }, _recent.Items.Select(i => i.FolderPath));
    });

    // ---- availability -----------------------------------------------------------------------------------------------

    [Fact]
    public Task Entries_start_as_checking_and_become_available_or_unavailable() => OnUi(async () =>
    {
        var here = WriteProject("Here");
        var gone = Gone("Gone");
        await List((here, "Here"), (gone, "Gone"));
        _store.Hold(here);
        _store.Hold(gone);

        await _recent.OnOpenedAsync();
        Assert.All(_recent.Items, i => Assert.Equal(Checking, i.Availability));
        Assert.All(_recent.Items, i => Assert.Equal("Checking…", i.StateText));
        Assert.All(_recent.Items, i => Assert.False(i.OpenCommand.CanExecute(null)));   // checking: not openable
        Assert.All(_recent.Items, i => Assert.True(i.RemoveCommand.CanExecute(null)));

        _store.Release(here);
        await Eventually(() => Item(here).Availability == Available, "not available");
        Assert.True(Item(here).OpenCommand.CanExecute(null));
        Assert.Equal("", Item(here).StateText);
        Assert.Equal(Checking, Item(gone).Availability);

        _store.Release(gone);
        await Eventually(() => Item(gone).Availability == Unavailable, "not unavailable");
        Assert.Equal("Unavailable", Item(gone).StateText);
        Assert.True(Item(gone).IsUnavailable);
        Assert.False(Item(gone).OpenCommand.CanExecute(null));
        Assert.True(Item(gone).RemoveCommand.CanExecute(null));
    });

    [Fact]
    public Task A_failing_check_is_unavailable() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        _store.CheckThrows = _ => true;

        await OpenDropDownAndSettle();

        Assert.Equal(Unavailable, Item(a).Availability);
        Assert.False(Item(a).OpenCommand.CanExecute(null));
    });

    [Fact]
    public Task Checks_run_off_the_ui_thread_and_a_slow_one_blocks_neither_the_ui_nor_the_list() => OnUi(async () =>
    {
        var slow = Gone("NetworkShare");
        var b = WriteProject("B");
        await List((slow, "Share"), (b, "B"));
        _store.Hold(slow);

        await _recent.OnOpenedAsync();                                   // returns although the check hangs
        await Eventually(() => Item(b).Availability == Available, "the other entry waited for the slow one");
        Assert.Equal(Checking, Item(slow).Availability);

        await Item(b).RemoveCommand.ExecuteAsync(null);                 // the list can still change…
        await Item(slow).RemoveCommand.ExecuteAsync(null);              // …also the entry being checked
        Assert.Empty(_recent.Items);

        Assert.DoesNotContain(_ui.ThreadId, _store.Checks.Select(c => c.Thread));
        _store.Release(slow);
    });

    [Fact]
    public Task Item_state_changes_only_on_the_ui_thread() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        _store.Hold(a);
        await _recent.OnOpenedAsync();
        var threads = new ConcurrentBag<int>();
        Item(a).PropertyChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);

        _store.Release(a);
        await Eventually(() => Item(a).Availability == Available, "no result");

        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Equal(_ui.ThreadId, t));
    });

    [Fact]
    public Task Closing_before_a_check_ends_is_harmless_and_the_result_reaches_its_item() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        _store.Hold(a);

        await _recent.OnOpenedAsync();
        var item = Item(a);
        _recent.OnClosed();
        _store.Release(a);

        await Eventually(() => item.Availability == Available, "the result was lost");
    });

    [Fact]
    public Task Opening_again_while_a_check_runs_waits_for_it_instead_of_starting_another() => OnUi(async () =>
    {
        var slow = Gone("Slow");
        await List((slow, "Slow"));
        _store.Hold(slow);

        for (var i = 0; i < 5; i++)
        {
            await _recent.OnOpenedAsync();
            _recent.OnClosed();
        }
        await _recent.OnOpenedAsync();

        Assert.Equal(1, _store.ChecksOf(slow));
        Assert.Equal(Checking, Item(slow).Availability);

        _store.Release(slow);
        await Eventually(() => Item(slow).Availability == Unavailable, "the shared check's result did not reach the shown item");
        Assert.Equal(1, _store.ChecksOf(slow));

        await OpenDropDownAndSettle();                                   // finished: the next opening checks again
        Assert.Equal(2, _store.ChecksOf(slow));
    });

    [Fact]
    public Task A_result_for_items_no_longer_shown_changes_nothing_shown() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        _store.Hold(a);
        await _recent.OnOpenedAsync();
        var old = Item(a);

        // The project disappears and the list is read again while the first check still runs.
        Directory.Delete(a, recursive: true);
        _store.Release(a);
        await _recent.OnOpenedAsync();
        await Eventually(() => Item(a).Availability != Checking, "no result");

        Assert.Equal(Unavailable, Item(a).Availability);
        Assert.NotSame(old, Item(a));
        Assert.True(old.IsDetached);
        Assert.False(old.OpenCommand.CanExecute(null));
    });

    [Fact]
    public Task An_item_replaced_by_a_new_reading_can_neither_open_nor_remove() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        await OpenDropDownAndSettle();
        var old = Item(a);
        Assert.True(old.OpenCommand.CanExecute(null));
        var current = _projects.Current;

        await OpenDropDownAndSettle();                                     // the list is read again: new items
        Assert.False(old.OpenCommand.CanExecute(null));                    // e.g. a click still reaching the old row
        Assert.False(old.RemoveCommand.CanExecute(null));
        await old.OpenCommand.ExecuteAsync(null);
        await old.RemoveCommand.ExecuteAsync(null);

        Assert.Same(current, _projects.Current);
        Assert.Empty(_store.Removes);
        Assert.Equal(new[] { a }, _recent.Items.Select(i => i.FolderPath));
    });

    [Fact]
    public Task A_removed_entry_does_not_come_back_with_a_late_check_or_reading() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        _store.Hold(a);
        await _recent.OnOpenedAsync();

        await Item(a).RemoveCommand.ExecuteAsync(null);
        _store.Release(a);
        await Task.Delay(100);

        Assert.Equal(new[] { b }, _recent.Items.Select(i => i.FolderPath));
        Assert.Equal(new[] { b }, (await _real.GetAsync()).Select(e => e.FolderPath));
    });

    // ---- opening ------------------------------------------------------------------------------------------------------

    [Fact]
    public Task Choosing_an_available_project_opens_it_through_the_workflow_and_lists_it_first() => OnUi(async () =>
    {
        var a = WriteProject("A", "Alpha");
        var b = WriteProject("B", "Beta");
        await List((a, "Alpha"), (b, "Beta"));
        await OpenDropDownAndSettle();
        var closed = 0;
        _recent.CloseRequested += (_, _) => closed++;

        await Item(b).OpenCommand.ExecuteAsync(null);

        Assert.Equal("Beta", _projects.Current.Name);
        Assert.Equal(b, _projects.Current.ProjectFolderPath);
        Assert.Equal("Opened project \"Beta\".", _status.Message);
        Assert.Equal(1, closed);
        Assert.Equal(new[] { b, a }, (await _real.GetAsync()).Select(e => e.FolderPath));   // by the workflow
    });

    [Fact]
    public Task Checking_and_unavailable_entries_never_open() => OnUi(async () =>
    {
        var checking = WriteProject("Checking");
        var gone = Gone("Gone");
        await List((checking, "Checking"), (gone, "Gone"));
        _store.Hold(checking);
        await _recent.OnOpenedAsync();
        await Eventually(() => Item(gone).Availability == Unavailable, "no result");
        var current = _projects.Current;

        await Item(checking).OpenCommand.ExecuteAsync(null);
        await Item(gone).OpenCommand.ExecuteAsync(null);

        Assert.Same(current, _projects.Current);
        Assert.Equal("Ready.", _status.Message);
    });

    [Fact]
    public Task A_second_choice_while_an_open_runs_starts_nothing() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        await OpenDropDownAndSettle();
        var opened = new List<string?>();
        _projects.ProjectChanged += (_, _) => opened.Add(_projects.Current.Name);

        var first = Item(a).OpenCommand.ExecuteAsync(null);
        Assert.True(_recent.IsBusy);
        Assert.False(Item(b).OpenCommand.CanExecute(null));
        Assert.False(Item(b).RemoveCommand.CanExecute(null));
        var second = Item(b).OpenCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "A" }, opened);
        Assert.False(_recent.IsBusy);
    });

    [Fact]
    public Task A_project_that_fails_to_open_keeps_the_current_project_and_its_entry() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        await OpenDropDownAndSettle();
        File.WriteAllText(ProjectFileStore.ProjectFilePath(a), "{ damaged");   // damaged after the check
        _undo.Execute(new NamedEdit(_projects, "unsaved"));
        _dialogs.Answers.Enqueue(1);                                          // Don't Save
        var current = _projects.Current;

        await Item(a).OpenCommand.ExecuteAsync(null);

        Assert.Same(current, _projects.Current);
        Assert.True(_projects.Current.IsDirty);
        Assert.StartsWith("Couldn't open the project.", _status.Message);
        Assert.Equal(new[] { a }, (await _real.GetAsync()).Select(e => e.FolderPath));
    });

    [Fact]
    public Task Cancel_at_the_unsaved_changes_question_keeps_the_project_and_the_list() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        await OpenDropDownAndSettle();
        var bytes = await File.ReadAllBytesAsync(_real.FilePath);
        _undo.Execute(new NamedEdit(_projects, "unsaved"));
        _dialogs.Answers.Enqueue(2);                                          // Cancel
        var current = _projects.Current;

        await Item(b).OpenCommand.ExecuteAsync(null);

        Assert.Equal("Unsaved changes", Assert.Single(_dialogs.Asked).Title);
        Assert.Same(current, _projects.Current);
        Assert.True(current.IsDirty);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_real.FilePath));
    });

    [Fact]
    public Task Dont_save_at_the_question_opens_the_chosen_project() => OnUi(async () =>
    {
        var b = WriteProject("B");
        await List((b, "B"));
        await OpenDropDownAndSettle();
        _undo.Execute(new NamedEdit(_projects, "unsaved"));
        _dialogs.Answers.Enqueue(1);                                          // Don't Save

        await Item(b).OpenCommand.ExecuteAsync(null);

        Assert.Equal("B", _projects.Current.Name);
        Assert.False(_projects.Current.IsDirty);
    });

    [Fact]
    public Task An_open_finishing_while_the_drop_down_is_open_again_shows_the_new_list() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        await OpenDropDownAndSettle();
        _undo.Execute(new NamedEdit(_projects, "unsaved"));
        var answer = new TaskCompletionSource<int?>();
        var dialogs = new HeldDialogs(answer.Task);
        var workflow = new ProjectFileWorkflow(_projects, new MediaAnalysisCoordinator(new NoAnalysis(), _projects,
                NullLogger<MediaAnalysisCoordinator>.Instance), new NullAutosave(), dialogs, new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance, recentProjects: _store);
        var recent = new RecentProjectsViewModel(_store, workflow, _status, _lock);
        await recent.OnOpenedAsync();
        await Eventually(() => recent.Items.All(i => i.Availability == Available), "no result");

        var opening = recent.Items[1].OpenCommand.ExecuteAsync(null);      // B; the question waits
        recent.OnClosed();
        await recent.OnOpenedAsync();                                       // opened again meanwhile
        answer.SetResult(1);                                                // Don't Save
        await opening;

        Assert.Equal(new[] { b, a }, recent.Items.Select(i => i.FolderPath));
    });

    private sealed class HeldDialogs(Task<int?> answer) : IDialogService
    {
        public Task<int?> AskAsync(DialogRequest request) => answer;
    }

    // ---- removing -----------------------------------------------------------------------------------------------------

    [Fact]
    public Task Removing_an_entry_takes_it_out_of_the_list_and_the_store_without_opening_it() => OnUi(async () =>
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        await List((a, "A"), (b, "B"));
        await OpenDropDownAndSettle();
        var current = _projects.Current;

        await Item(a).RemoveCommand.ExecuteAsync(null);

        Assert.Equal(new[] { b }, _recent.Items.Select(i => i.FolderPath));
        Assert.Equal(new[] { b }, (await _real.GetAsync()).Select(e => e.FolderPath));
        Assert.Same(current, _projects.Current);
        Assert.True(Directory.Exists(a));                                    // the project itself is untouched
    });

    [Fact]
    public Task Removing_the_last_entry_shows_the_empty_state() => OnUi(async () =>
    {
        var gone = Gone("Gone");
        await List((gone, "Gone"));
        await OpenDropDownAndSettle();
        Assert.Equal(Unavailable, Item(gone).Availability);

        await Item(gone).RemoveCommand.ExecuteAsync(null);                 // an unavailable entry can be removed

        Assert.Empty(_recent.Items);
        Assert.True(_recent.IsEmpty);
        Assert.Empty(await _real.GetAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_failed_removal_is_reported_and_the_entry_stays(bool throws) => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        await OpenDropDownAndSettle();
        _store.RemoveFails = !throws;
        _store.RemoveThrows = throws;
        var readsBefore = _store.Reads;

        await Item(a).RemoveCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't remove \"A\" from the recent projects.", _status.Message);
        Assert.Equal(new[] { a }, _recent.Items.Select(i => i.FolderPath));
        Assert.Equal(readsBefore + 1, _store.Reads);                        // read again: the actual state
        Assert.Equal(new[] { a }, (await _real.GetAsync()).Select(e => e.FolderPath));
        Assert.False(_recent.IsBusy);
    });

    // ---- editing lock -----------------------------------------------------------------------------------------------------

    [Fact]
    public Task During_an_export_nothing_can_be_opened_or_removed_and_everything_comes_back_after() => OnUi(async () =>
    {
        var a = WriteProject("A");
        await List((a, "A"));
        await OpenDropDownAndSettle();
        var current = _projects.Current;
        var changes = new List<string?>();
        _recent.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        using (_lock.Acquire())
        {
            Assert.False(_recent.IsEnabled);
            Assert.Contains(nameof(RecentProjectsViewModel.IsEnabled), changes);
            Assert.False(Item(a).OpenCommand.CanExecute(null));
            Assert.False(Item(a).RemoveCommand.CanExecute(null));

            await Item(a).OpenCommand.ExecuteAsync(null);
            await Item(a).RemoveCommand.ExecuteAsync(null);
            Assert.Same(current, _projects.Current);
            Assert.Single(_recent.Items);
            Assert.Empty(_store.Removes);
        }

        Assert.True(_recent.IsEnabled);
        Assert.True(Item(a).OpenCommand.CanExecute(null));
        Assert.True(Item(a).RemoveCommand.CanExecute(null));
    });
}
