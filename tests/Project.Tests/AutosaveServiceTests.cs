using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Autosave into recovery files: what is written, when it is removed, and how it
/// coexists with Save. Real files in a temp folder; the timer is only used where noted.</summary>
public sealed class AutosaveServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly RecoveryStore _store;
    private readonly AutosaveService _autosave;

    public AutosaveServiceTests() : this(new ProjectFileStore(), null)
    {
    }

    private AutosaveServiceTests(ProjectFileStore projectFiles, Func<Task>? beforeRecoveryWrite)
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, projectFiles);
        _store = new RecoveryStore(_temp.Combine("recovery"), new ProjectFileStore()) { BeforeWrite = beforeRecoveryWrite };
        _autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance);
    }

    public void Dispose()
    {
        _autosave.Dispose();
        _temp.Dispose();
    }

    private void Edit(string name) => _undo.Execute(new RenameTimelineCommand(_projects, name));

    private string RecoveryPath => _store.PathFor(_projects.Current.Id);

    private (Core.Entities.Project Project, RecoveryInfo Info) ReadRecovery() =>
        ProjectSerializer.DeserializeRecovery(File.ReadAllText(RecoveryPath));

    private static string ProjectFile(string folder) => ProjectFileStore.ProjectFilePath(folder);

    // ---- writing -------------------------------------------------------------

    [Fact]
    public async Task Clean_project_is_not_autosaved()
    {
        Assert.False(await _autosave.AutosaveNowAsync());

        Assert.False(File.Exists(RecoveryPath));
    }

    [Fact]
    public async Task Unsaved_new_project_is_autosaved_with_its_current_state()
    {
        Edit("first cut");
        string? completed = null;
        _autosave.AutosaveCompleted += (_, path) => completed = path;

        Assert.True(await _autosave.AutosaveNowAsync());

        var (project, info) = ReadRecovery();
        Assert.Equal("first cut", project.Timeline.Name);
        Assert.Equal(_projects.Current.Id, project.Id);
        Assert.Null(info.ProjectFolderPath);
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.Equal(RecoveryPath, completed);
        Assert.Empty(Directory.GetFiles(_temp.Path, "project.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Autosave_never_touches_project_json()
    {
        var folder = _temp.Combine("Film");
        Edit("saved");
        await _projects.SaveAsAsync(folder);
        var savedBytes = File.ReadAllBytes(ProjectFile(folder));
        var savedTime = File.GetLastWriteTimeUtc(ProjectFile(folder));

        Edit("unsaved");
        Assert.True(await _autosave.AutosaveNowAsync());

        Assert.Equal(savedBytes, File.ReadAllBytes(ProjectFile(folder)));
        Assert.Equal(savedTime, File.GetLastWriteTimeUtc(ProjectFile(folder)));
        var (project, info) = ReadRecovery();
        Assert.Equal("unsaved", project.Timeline.Name);
        Assert.Equal(folder, info.ProjectFolderPath);
        Assert.True(_projects.Current.IsDirty); // autosave is not a save
    }

    [Fact]
    public async Task Each_autosave_reflects_the_state_at_that_moment()
    {
        Edit("a");
        await _autosave.AutosaveNowAsync();
        Edit("b");
        await _autosave.AutosaveNowAsync();

        Assert.Equal("b", ReadRecovery().Project.Timeline.Name);

        _undo.Undo(); // back to "a" — still unsaved
        await _autosave.AutosaveNowAsync();
        Assert.Equal("a", ReadRecovery().Project.Timeline.Name);
    }

    [Fact]
    public async Task Media_import_is_autosaved()
    {
        _projects.AddMediaAssets(new[] { new MediaAsset { FilePath = _temp.Combine("a.wav"), Kind = MediaKind.Audio } });

        Assert.True(await _autosave.AutosaveNowAsync());

        Assert.Single(ReadRecovery().Project.MediaAssets);
    }

    // ---- becoming obsolete ---------------------------------------------------

    [Fact]
    public async Task Successful_save_removes_the_recovery_file()
    {
        Edit("a");
        await _autosave.AutosaveNowAsync();
        Assert.True(File.Exists(RecoveryPath));

        await _projects.SaveAsAsync(_temp.Combine("Film"));

        Assert.True(await Wait.UntilAsync(() => !File.Exists(RecoveryPath)));
    }

    [Fact]
    public async Task Failed_save_keeps_the_recovery_file()
    {
        var failing = new AutosaveServiceTests(new ProjectFileStore { BeforeCommit = _ => throw new IOException("disk full") }, null);
        try
        {
            failing.Edit("a");
            await failing._autosave.AutosaveNowAsync();

            await Assert.ThrowsAsync<ProjectFileException>(() => failing._projects.SaveAsAsync(failing._temp.Combine("Film")));

            await Task.Delay(50);
            Assert.True(File.Exists(failing.RecoveryPath));
        }
        finally
        {
            failing.Dispose();
        }
    }

    [Fact]
    public async Task Undo_back_to_the_saved_state_removes_this_sessions_recovery_at_the_next_autosave()
    {
        await _projects.SaveAsAsync(_temp.Combine("Film"));
        Edit("a");
        await _autosave.AutosaveNowAsync();

        _undo.Undo();
        Assert.False(await _autosave.AutosaveNowAsync());

        Assert.False(File.Exists(RecoveryPath));
    }

    [Fact]
    public async Task Clean_project_does_not_remove_a_recovery_file_left_by_another_session()
    {
        // e.g. the user postponed recovery at startup and then opened the same project normally.
        var id = _projects.Current.Id;
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(id), "left by a crashed session");

        await _autosave.AutosaveNowAsync();

        Assert.True(File.Exists(_store.PathFor(id)));
    }

    [Fact]
    public async Task Discarding_the_current_recovery_removes_it()
    {
        Edit("a");
        await _autosave.AutosaveNowAsync();

        await _autosave.DiscardRecoveryAsync(_projects.Current.Id);

        Assert.False(File.Exists(RecoveryPath));
    }

    // ---- Save and autosave together ---------------------------------------------

    [Fact]
    public async Task Autosave_snapshotted_before_a_save_does_not_bring_the_recovery_file_back()
    {
        using var snapshotTaken = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var t = new AutosaveServiceTests(new ProjectFileStore(), async () =>
        {
            snapshotTaken.Release();
            await release.WaitAsync();
        });
        try
        {
            t.Edit("a");
            var autosave = t._autosave.AutosaveNowAsync();   // snapshot of "a" taken, write held back
            await snapshotTaken.WaitAsync();

            await t._projects.SaveAsAsync(t._temp.Combine("Film")); // saves "a", deletes the recovery
            release.Release();

            Assert.False(await autosave);                      // skipped: it was already obsolete
            Assert.False(File.Exists(t.RecoveryPath));
            Assert.False(t._projects.Current.IsDirty);
        }
        finally
        {
            t.Dispose();
        }
    }

    [Fact]
    public async Task Save_and_autosave_running_together_leave_both_files_valid()
    {
        using var reachedCommit = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var t = new AutosaveServiceTests(new ProjectFileStore
        {
            BeforeCommit = _ =>
            {
                reachedCommit.Release();
                release.Wait();
            }
        }, null);
        try
        {
            var folder = t._temp.Combine("Film");
            t.Edit("saved");
            var save = t._projects.SaveAsAsync(folder);
            await reachedCommit.WaitAsync();

            t.Edit("edited while saving");
            Assert.True(await t._autosave.AutosaveNowAsync()); // runs while project.json is being written
            release.Release();
            await save;

            var saved = ProjectSerializer.Deserialize(File.ReadAllText(ProjectFile(folder)), folder);
            Assert.Equal("saved", saved.Timeline.Name);
            // Still dirty (edited during the save), so the recovery file is kept and is newer.
            Assert.True(t._projects.Current.IsDirty);
            await Task.Delay(50);
            Assert.Equal("edited while saving", t.ReadRecovery().Project.Timeline.Name);
        }
        finally
        {
            t.Dispose();
        }
    }

    // ---- timer and shutdown -----------------------------------------------------------

    [Fact]
    public async Task Timer_autosaves_periodically_and_stop_ends_it()
    {
        using var autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance, TimeSpan.FromMilliseconds(30));
        Edit("a");

        autosave.Start();
        Assert.True(await Wait.UntilAsync(() => File.Exists(RecoveryPath)));
        autosave.Stop();
        await Task.Delay(100); // let an in-flight tick finish

        File.Delete(RecoveryPath);
        await Task.Delay(150);
        Assert.False(File.Exists(RecoveryPath));
    }

    [Fact]
    public void Default_interval_is_two_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), AutosaveService.DefaultInterval);
    }

    [Fact]
    public async Task Shutdown_with_unsaved_changes_keeps_a_final_recovery_file()
    {
        Edit("a");
        await _autosave.AutosaveNowAsync();
        Edit("last change before closing");

        await _autosave.ShutdownAsync();

        Assert.Equal("last change before closing", ReadRecovery().Project.Timeline.Name);
    }

    [Fact]
    public async Task Shutdown_of_a_clean_project_leaves_no_recovery_file()
    {
        Edit("a");
        await _autosave.AutosaveNowAsync();
        await _projects.SaveAsAsync(_temp.Combine("Film"));

        await _autosave.ShutdownAsync();

        Assert.False(File.Exists(RecoveryPath));
        Assert.Empty(Directory.GetFiles(_store.RootFolder));
    }

    [Fact]
    public async Task Shutdown_stops_the_timer()
    {
        using var autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance, TimeSpan.FromMilliseconds(30));
        autosave.Start();
        await autosave.ShutdownAsync(); // clean: nothing written

        Edit("after shutdown");
        await Task.Delay(200);

        Assert.False(File.Exists(RecoveryPath));
    }
}
