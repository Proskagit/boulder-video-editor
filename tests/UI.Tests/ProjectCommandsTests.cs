using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// New / Open / Save / Save As / Close through <see cref="ProjectFileWorkflow"/>: the "save
/// changes?" prompt, pickers, failures, and how recovery files follow. Real ProjectService,
/// AutosaveService and TimelineEditService (edits are real undoable timeline commands) on temp
/// folders; dialog and picker answers are scripted.
/// </summary>
public sealed class ProjectCommandsTests : IDisposable
{
    private const int Save = 0, DontSave = 1, Cancel = 2;
    private const int Replace = 0;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly RecoveryStore _store;
    private readonly AutosaveService _autosave;
    private readonly StatusService _status = new();

    public ProjectCommandsTests() : this(new ProjectFileStore())
    {
    }

    private ProjectCommandsTests(ProjectFileStore projectFiles)
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, projectFiles);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _store = new RecoveryStore(Path.Combine(_root, "recovery"));
        _autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance);
    }

    public void Dispose()
    {
        _autosave.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (ProjectFileWorkflow Workflow, ScriptedDialogs Dialogs, ScriptedPicker Picker) Create(int?[]? answers = null, params string?[] folders)
    {
        var dialogs = new ScriptedDialogs(answers ?? Array.Empty<int?>());
        var picker = new ScriptedPicker(folders);
        var coordinator = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        return (new ProjectFileWorkflow(_projects, coordinator, _autosave, dialogs, picker, _status, NullLogger<ProjectFileWorkflow>.Instance), dialogs, picker);
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private static int?[] Answers(params int?[] answers) => answers;

    private string Folder(string name) => Path.Combine(_root, name);

    private void Edit() => Assert.True(_edit.AddTrack(TrackType.Video).Success);

    private int VideoTracks => _projects.Current.Timeline.VideoTracks.Count;

    private string RecoveryOf(Guid id) => _store.PathFor(id);

    private string WriteOtherProject(string name)
    {
        var folder = Folder(name);
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    private Core.Entities.Project SavedFile(string folder) =>
        ProjectSerializer.Deserialize(File.ReadAllText(ProjectFileStore.ProjectFilePath(folder)), folder);

    // ---- New ---------------------------------------------------------------------------

    [Fact]
    public async Task New_on_a_clean_project_asks_nothing()
    {
        var (workflow, dialogs, _) = Create();
        var before = _projects.Current;

        Assert.True(await workflow.NewProjectAsync());

        Assert.Empty(dialogs.Asked);
        Assert.NotSame(before, _projects.Current);
    }

    [Theory]
    [InlineData(Cancel)]
    [InlineData(null)] // prompt closed
    public async Task New_then_cancel_keeps_everything(int? answer)
    {
        Edit();
        var (workflow, dialogs, _) = Create(Answers(answer));
        var before = _projects.Current;

        Assert.False(await workflow.NewProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.True(before.IsDirty);
        Assert.Equal(2, VideoTracks);
        Assert.True(_undo.CanUndo);
        Assert.Equal(new[] { "Save", "Don't Save", "Cancel" }, Assert.Single(dialogs.Asked).Buttons);
        Assert.Contains("creating a new project", dialogs.Asked[0].Message);
    }

    [Fact]
    public async Task New_then_dont_save_discards_the_changes_and_their_recovery_file()
    {
        Edit();
        var oldId = _projects.Current.Id;
        await _autosave.AutosaveNowAsync();
        Assert.True(File.Exists(RecoveryOf(oldId)));
        var (workflow, _, _) = Create(Answers(DontSave));

        Assert.True(await workflow.NewProjectAsync());

        Assert.NotEqual(oldId, _projects.Current.Id);
        Assert.False(_projects.Current.IsDirty);
        Assert.False(_undo.CanUndo);
        Assert.Equal(1, VideoTracks);
        Assert.False(File.Exists(RecoveryOf(oldId)));
    }

    [Fact]
    public async Task New_then_save_on_a_saved_project_writes_it_first()
    {
        var folder = Folder("Film");
        await _projects.SaveAsAsync(folder);
        Edit();
        var (workflow, _, picker) = Create(Answers(Save));

        Assert.True(await workflow.NewProjectAsync());

        Assert.Empty(picker.Titles); // already has a folder: no Save As
        Assert.Equal(2, SavedFile(folder).Timeline.VideoTracks.Count);
        Assert.Equal("Untitled Project", _projects.Current.Name);
    }

    [Fact]
    public async Task New_then_save_on_a_never_saved_project_goes_through_save_as()
    {
        Edit();
        var folder = Folder("First Film");
        var (workflow, _, picker) = Create(Answers(Save), folder);

        Assert.True(await workflow.NewProjectAsync());

        Assert.Single(picker.Titles);
        Assert.Equal(2, SavedFile(folder).Timeline.VideoTracks.Count);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task New_then_save_then_cancel_the_folder_picker_keeps_everything()
    {
        Edit();
        var before = _projects.Current;
        var (workflow, _, _) = Create(Answers(Save), (string?)null);

        Assert.False(await workflow.NewProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.True(before.IsDirty);
        Assert.Null(before.ProjectFolderPath);
    }

    [Fact]
    public async Task New_then_save_that_fails_keeps_the_project_and_reports_it()
    {
        Edit();
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        var before = _projects.Current;
        var (workflow, _, _) = Create(Answers(Save), blocker);

        Assert.False(await workflow.NewProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.True(before.IsDirty);
        Assert.StartsWith("Couldn't save", _status.Message);
    }

    // ---- Open ----------------------------------------------------------------------------

    [Fact]
    public async Task Open_with_the_picker_cancelled_does_nothing()
    {
        Edit();
        var before = _projects.Current;
        var (workflow, dialogs, _) = Create(null, (string?)null);

        Assert.False(await workflow.OpenProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task Open_then_cancel_keeps_everything()
    {
        Edit();
        var before = _projects.Current;
        var (workflow, dialogs, _) = Create(Answers(Cancel), WriteOtherProject("Other"));

        Assert.False(await workflow.OpenProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.True(before.IsDirty);
        Assert.Contains("opening another project", Assert.Single(dialogs.Asked).Message);
    }

    [Fact]
    public async Task Open_on_a_clean_project_opens_without_asking()
    {
        var (workflow, dialogs, _) = Create(null, WriteOtherProject("Other"));

        Assert.True(await workflow.OpenProjectAsync());

        Assert.Equal("Other", _projects.Current.Name);
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task Open_then_dont_save_opens_and_removes_the_old_recovery_file()
    {
        Edit();
        var oldId = _projects.Current.Id;
        await _autosave.AutosaveNowAsync();
        var (workflow, _, _) = Create(Answers(DontSave), WriteOtherProject("Other"));

        Assert.True(await workflow.OpenProjectAsync());

        Assert.Equal("Other", _projects.Current.Name);
        Assert.False(_projects.Current.IsDirty);
        Assert.False(File.Exists(RecoveryOf(oldId)));
    }

    [Fact]
    public async Task Open_then_dont_save_but_open_fails_keeps_the_project_its_changes_and_recovery()
    {
        Edit();
        var before = _projects.Current;
        await _autosave.AutosaveNowAsync();
        var broken = Folder("Broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(broken), "{ damaged");
        var (workflow, _, _) = Create(Answers(DontSave), broken);

        Assert.False(await workflow.OpenProjectAsync());

        Assert.Same(before, _projects.Current);
        Assert.True(before.IsDirty);
        Assert.Equal(2, VideoTracks);
        Assert.True(File.Exists(RecoveryOf(before.Id)));
        Assert.StartsWith("Couldn't open the project.", _status.Message);
    }

    [Fact]
    public async Task Open_then_save_saves_first_and_then_opens()
    {
        var folder = Folder("Film");
        await _projects.SaveAsAsync(folder);
        Edit();
        var (workflow, _, _) = Create(Answers(Save), WriteOtherProject("Other"));

        Assert.True(await workflow.OpenProjectAsync());

        Assert.Equal("Other", _projects.Current.Name);
        Assert.Equal(2, SavedFile(folder).Timeline.VideoTracks.Count);
    }

    // ---- Save / Save As -------------------------------------------------------------------

    [Fact]
    public async Task Save_of_a_never_saved_project_goes_through_save_as()
    {
        Edit();
        var folder = Folder("My Film");
        var (workflow, _, picker) = Create(null, folder);

        Assert.True(await workflow.SaveAsync());

        Assert.StartsWith("Save Project As", Assert.Single(picker.Titles));
        Assert.Equal(folder, _projects.Current.ProjectFolderPath);
        Assert.Equal("My Film", _projects.Current.Name);
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal($"Saved project \"My Film\" to {folder}.", _status.Message);
    }

    [Fact]
    public async Task Save_of_a_saved_project_writes_it_without_asking()
    {
        var folder = Folder("Film");
        await _projects.SaveAsAsync(folder);
        Edit();
        var (workflow, dialogs, picker) = Create();

        Assert.True(await workflow.SaveAsync());

        Assert.Empty(picker.Titles);
        Assert.Empty(dialogs.Asked);
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal("Saved project \"Film\".", _status.Message);
    }

    [Fact]
    public async Task Save_as_with_the_picker_cancelled_changes_nothing()
    {
        Edit();
        var (workflow, _, _) = Create(null, (string?)null);

        Assert.False(await workflow.SaveAsAsync());

        Assert.True(_projects.Current.IsDirty);
        Assert.Null(_projects.Current.ProjectFolderPath);
    }

    [Fact]
    public async Task Save_as_over_another_project_asks_and_cancel_keeps_it()
    {
        var other = WriteOtherProject("Other");
        var otherFile = File.ReadAllText(ProjectFileStore.ProjectFilePath(other));
        Edit();
        var (workflow, dialogs, _) = Create(Answers(1), other);

        Assert.False(await workflow.SaveAsAsync());

        Assert.Equal("Replace project?", Assert.Single(dialogs.Asked).Title);
        Assert.Equal(otherFile, File.ReadAllText(ProjectFileStore.ProjectFilePath(other)));
        Assert.True(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Save_as_over_another_project_replaces_it_when_confirmed()
    {
        var other = WriteOtherProject("Other");
        Edit();
        var (workflow, _, _) = Create(Answers(Replace), other);

        Assert.True(await workflow.SaveAsAsync());

        Assert.Equal(_projects.Current.Id, SavedFile(other).Id);
    }

    [Fact]
    public async Task Save_as_into_the_projects_own_folder_does_not_ask_to_replace()
    {
        var folder = Folder("Film");
        await _projects.SaveAsAsync(folder);
        Edit();
        var (workflow, dialogs, _) = Create(null, folder);

        Assert.True(await workflow.SaveAsAsync());

        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task Failed_save_keeps_the_project_dirty_and_reports_it()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        Edit();
        var (workflow, _, _) = Create(null, blocker);

        Assert.False(await workflow.SaveAsync());

        Assert.True(_projects.Current.IsDirty);
        Assert.Null(_projects.Current.ProjectFolderPath);
        Assert.StartsWith("Couldn't save \"Untitled Project\".", _status.Message);
    }

    [Fact]
    public async Task Successful_save_removes_the_recovery_file()
    {
        Edit();
        await _autosave.AutosaveNowAsync();
        var (workflow, _, _) = Create(null, Folder("Film"));

        await workflow.SaveAsync();

        Assert.False(File.Exists(RecoveryOf(_projects.Current.Id)));
    }

    // ---- edits while saving -----------------------------------------------------------------

    [Fact]
    public async Task Edit_made_while_saving_from_the_prompt_is_not_lost()
    {
        using var reachedCommit = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var t = new ProjectCommandsTests(new ProjectFileStore
        {
            BeforeCommit = _ =>
            {
                reachedCommit.Release();
                release.Wait();
            }
        });
        try
        {
            t.Edit();
            // Save → (edit lands during the write) → asked again → Cancel.
            var (workflow, dialogs, _) = t.Create(Answers(Save, Cancel), t.Folder("Film"));

            var closing = workflow.PrepareToCloseAsync();
            await reachedCommit.WaitAsync();
            t.Edit();
            release.Release();

            Assert.False(await closing);                     // not closed
            Assert.Equal(2, dialogs.Asked.Count);            // asked again after the save
            Assert.True(t._projects.Current.IsDirty);         // the later edit is still unsaved
            Assert.Equal(3, t.VideoTracks);
            Assert.Equal(2, t.SavedFile(t.Folder("Film")).Timeline.VideoTracks.Count);
        }
        finally
        {
            t.Dispose();
        }
    }

    // ---- Close ------------------------------------------------------------------------------

    [Fact]
    public async Task Close_of_a_clean_project_asks_nothing_and_leaves_no_recovery_file()
    {
        var (workflow, dialogs, _) = Create();

        Assert.True(await workflow.PrepareToCloseAsync());

        Assert.Empty(dialogs.Asked);
        Assert.False(Directory.Exists(_store.RootFolder) && Directory.GetFiles(_store.RootFolder).Length > 0);
    }

    [Theory]
    [InlineData(Cancel)]
    [InlineData(null)]
    public async Task Close_then_cancel_keeps_the_window_the_changes_and_autosave(int? answer)
    {
        Edit();
        await _autosave.AutosaveNowAsync();
        var (workflow, dialogs, _) = Create(Answers(answer));

        Assert.False(await workflow.PrepareToCloseAsync());

        Assert.Contains("closing", Assert.Single(dialogs.Asked).Message);
        Assert.True(_projects.Current.IsDirty);
        Assert.True(File.Exists(RecoveryOf(_projects.Current.Id)));
        Edit();
        Assert.True(await _autosave.AutosaveNowAsync()); // autosave still works
    }

    [Fact]
    public async Task Close_then_dont_save_removes_the_recovery_file()
    {
        Edit();
        await _autosave.AutosaveNowAsync();
        var (workflow, _, _) = Create(Answers(DontSave));

        Assert.True(await workflow.PrepareToCloseAsync());

        Assert.False(File.Exists(RecoveryOf(_projects.Current.Id)));
    }

    [Fact]
    public async Task Close_then_save_saves_and_closes()
    {
        Edit();
        var folder = Folder("Film");
        var (workflow, _, _) = Create(Answers(Save), folder);

        Assert.True(await workflow.PrepareToCloseAsync());

        Assert.Equal(2, SavedFile(folder).Timeline.VideoTracks.Count);
        Assert.False(File.Exists(RecoveryOf(_projects.Current.Id)));
    }

    [Fact]
    public async Task Close_then_save_with_the_picker_cancelled_keeps_the_window_open()
    {
        Edit();
        var (workflow, _, _) = Create(Answers(Save), (string?)null);

        Assert.False(await workflow.PrepareToCloseAsync());

        Assert.True(_projects.Current.IsDirty);
    }

    // ---- recovered project -----------------------------------------------------------------

    [Fact]
    public async Task Recovered_project_stays_dirty_until_saved_and_save_clears_its_recovery()
    {
        var project = new Core.Entities.Project { Name = "Film" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        var folder = Folder("Film");
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(project.Id), ProjectSerializer.SerializeRecovery(project, new RecoveryInfo(folder, DateTimeOffset.UtcNow, 0, null)));
        var (workflow, dialogs, _) = Create(Answers(0 /* Recover */, Cancel));
        await workflow.StartSessionAsync();
        Assert.True(_projects.Current.IsDirty);

        Edit();
        _undo.Undo(); // back to the recovered state: still not saved
        Assert.True(_projects.Current.IsDirty);
        Assert.False(await workflow.NewProjectAsync()); // prompt → Cancel
        Assert.Equal(2, dialogs.Asked.Count);

        Assert.True(await workflow.SaveAsync()); // has its original folder: plain save
        Assert.False(_projects.Current.IsDirty);
        Assert.True(File.Exists(ProjectFileStore.ProjectFilePath(folder)));
        Assert.False(File.Exists(_store.PathFor(project.Id)));
    }

    [Fact]
    public async Task Dont_save_on_a_recovered_project_removes_the_recovery_file()
    {
        var project = new Core.Entities.Project { Name = "Film" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(project.Id), ProjectSerializer.SerializeRecovery(project, new RecoveryInfo(null, DateTimeOffset.UtcNow, 0, null)));
        var (workflow, _, _) = Create(Answers(0 /* Recover */, DontSave));
        await workflow.StartSessionAsync();

        Assert.True(await workflow.NewProjectAsync());

        Assert.False(File.Exists(_store.PathFor(project.Id)));
    }

    // ---- save point -------------------------------------------------------------------------

    [Fact]
    public async Task Undo_back_to_the_save_point_needs_no_prompt()
    {
        var (workflow, dialogs, _) = Create(null, Folder("Film"));
        Edit();
        await workflow.SaveAsync();
        Edit();
        Assert.True(_projects.Current.IsDirty);

        _undo.Undo();

        Assert.False(_projects.Current.IsDirty);
        Assert.True(await workflow.NewProjectAsync());
        Assert.Empty(dialogs.Asked);
    }
}
