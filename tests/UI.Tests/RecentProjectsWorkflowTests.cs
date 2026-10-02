using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Which project operations put a project in the recent-projects list (D026 §6, PO-8): a successful Open, Save As
/// and Recover with a folder, nothing else. Real ProjectService, AutosaveService / RecoveryStore and RecentProjectsStore
/// on temporary files; only dialogs and pickers are scripted.</summary>
public sealed class RecentProjectsWorkflowTests : IDisposable
{
    private const int Recover = 0;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly RecoveryStore _recovery;
    private readonly AutosaveService _autosave;
    private readonly StatusService _status = new();
    private readonly RecentProjectsStore _recent;

    public RecentProjectsWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _recovery = new RecoveryStore(Path.Combine(_root, "recovery"));
        _autosave = new AutosaveService(_projects, _recovery, NullLogger<AutosaveService>.Instance);
        _recent = new RecentProjectsStore(Path.Combine(_root, "config", "recent-projects.json"),
            NullLogger<RecentProjectsStore>.Instance);
    }

    public void Dispose()
    {
        _autosave.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ProjectFileWorkflow Workflow(ScriptedDialogs? dialogs = null, ScriptedPicker? picker = null,
        IRecentProjectsStore? recent = null)
    {
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        return new ProjectFileWorkflow(_projects, analysis, _autosave, dialogs ?? new ScriptedDialogs(),
            picker ?? new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance,
            recentProjects: recent ?? _recent);
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Success(new MediaMetadata { Duration = new MediaTime(1) }));
    }

    /// <summary>A store whose every call fails unexpectedly.</summary>
    private sealed class ThrowingStore : IRecentProjectsStore
    {
        public int Adds { get; private set; }
        public Task<IReadOnlyList<RecentProject>> GetAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<bool> AddAsync(string projectFolderPath, string projectName, CancellationToken ct = default)
        {
            Adds++;
            throw new InvalidOperationException("broken store");
        }
        public Task<bool> RemoveAsync(string projectFolderPath, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<bool> IsAvailableAsync(string projectFolderPath, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class NamedEdit(IProjectService projects, string name) : IUndoableCommand
    {
        private string _old = "";
        public string Description => "Rename";
        public void Execute() { _old = projects.Current.Timeline.Name; projects.Current.Timeline.Name = name; projects.NotifyTimelineChanged(); }
        public void Undo() { projects.Current.Timeline.Name = _old; projects.NotifyTimelineChanged(); }
    }

    private static Core.Entities.Project NewProject(string name)
    {
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private string WriteProject(string folderName, string? projectName = null)
    {
        var folder = Path.Combine(_root, "projects", folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder),
            ProjectSerializer.Serialize(NewProject(projectName ?? folderName), folder));
        return folder;
    }

    private string WriteRecoveryFile(string name, string? folder)
    {
        var project = NewProject(name);
        Directory.CreateDirectory(_recovery.RootFolder);
        var path = _recovery.PathFor(project.Id);
        File.WriteAllText(path, ProjectSerializer.SerializeRecovery(project, new RecoveryInfo(folder, DateTimeOffset.UtcNow, 0, null)));
        return path;
    }

    private async Task<string[]> Listed() => (await _recent.GetAsync()).Select(e => e.FolderPath).ToArray();

    /// <summary>The list file's bytes (empty when there is no file) — to show that something didn't touch it.</summary>
    private byte[] ListBytes() => File.Exists(_recent.FilePath) ? File.ReadAllBytes(_recent.FilePath) : Array.Empty<byte>();

    private async Task<byte[]> SeedList()
    {
        Assert.True(await _recent.AddAsync(Path.Combine(_root, "projects", "Seed"), "Seed"));
        return ListBytes();
    }

    // ---- Open -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_open_lists_the_project_by_its_full_path_and_name()
    {
        var folder = WriteProject("Trip", "Summer trip");

        Assert.True(await Workflow().OpenAsync(folder.ToUpperInvariant() + Path.DirectorySeparatorChar));

        var entry = Assert.Single(await _recent.GetAsync());
        Assert.Equal(Path.GetFullPath(folder.ToUpperInvariant()), entry.FolderPath);
        Assert.Equal("Summer trip", entry.Name);
        Assert.True(await _recent.IsAvailableAsync(entry.FolderPath));
    }

    [Fact]
    public async Task The_interactive_open_lists_the_picked_project()
    {
        var folder = WriteProject("Trip");

        Assert.True(await Workflow(picker: new ScriptedPicker(folder)).OpenProjectAsync());

        Assert.Equal(new[] { folder }, await Listed());
    }

    [Fact]
    public async Task Opening_a_listed_project_again_moves_it_first()
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        var workflow = Workflow();

        await workflow.OpenAsync(a);
        await workflow.OpenAsync(b);
        await workflow.OpenAsync(a);

        Assert.Equal(new[] { a, b }, await Listed());
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("no project file")]
    [InlineData("no folder")]
    public async Task A_failed_open_changes_nothing(string why)
    {
        var bytes = await SeedList();
        var folder = Path.Combine(_root, "projects", "Bad");
        if (why != "no folder") Directory.CreateDirectory(folder);
        if (why == "damaged") File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), "{ not a project");

        Assert.False(await Workflow().OpenAsync(folder));

        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_changes_nothing()
    {
        var bytes = await SeedList();

        Assert.False(await Workflow(picker: new ScriptedPicker((string?)null)).OpenProjectAsync());

        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task Cancel_at_the_unsaved_changes_question_changes_nothing()
    {
        var bytes = await SeedList();
        var folder = WriteProject("Other");
        _undo.Execute(new NamedEdit(_projects, "edited"));
        var dialogs = new ScriptedDialogs(2 /* Cancel */);

        Assert.False(await Workflow(dialogs, new ScriptedPicker(folder)).OpenProjectAsync());

        Assert.Single(dialogs.Asked);
        Assert.Equal(bytes, ListBytes());
    }

    // ---- Save As / Save ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_save_as_lists_the_new_folder_named_after_it()
    {
        var folder = Path.Combine(_root, "projects", "Saved here");

        Assert.True(await Workflow(picker: new ScriptedPicker(folder + Path.DirectorySeparatorChar)).SaveAsAsync());

        var entry = Assert.Single(await _recent.GetAsync());
        Assert.Equal(folder, entry.FolderPath);
        Assert.Equal("Saved here", entry.Name);
        Assert.True(await _recent.IsAvailableAsync(folder));
    }

    [Fact]
    public async Task Save_as_into_the_projects_own_folder_moves_it_first()
    {
        var a = WriteProject("A");
        var workflow = Workflow(picker: new ScriptedPicker(a));
        await workflow.OpenAsync(a);
        await _recent.AddAsync(Path.Combine(_root, "projects", "B"), "B");

        Assert.True(await workflow.SaveAsAsync());

        Assert.Equal(a, (await Listed())[0]);
    }

    [Fact]
    public async Task The_first_save_of_a_new_project_lists_it_through_save_as()
    {
        var folder = Path.Combine(_root, "projects", "First");
        _projects.CreateNew("Untitled Project");

        Assert.True(await Workflow(picker: new ScriptedPicker(folder)).SaveAsync());

        Assert.Equal(new[] { folder }, await Listed());
    }

    [Fact]
    public async Task Cancelling_save_as_changes_nothing()
    {
        var bytes = await SeedList();

        Assert.False(await Workflow(picker: new ScriptedPicker((string?)null)).SaveAsAsync());

        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task Refusing_to_replace_another_project_changes_nothing()
    {
        var bytes = await SeedList();
        var other = WriteProject("Other");
        var dialogs = new ScriptedDialogs(1 /* Cancel */);

        Assert.False(await Workflow(dialogs, new ScriptedPicker(other)).SaveAsAsync());

        Assert.Equal("Replace project?", Assert.Single(dialogs.Asked).Title);
        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task A_failed_save_as_changes_nothing()
    {
        var bytes = await SeedList();
        var folder = Path.Combine(_root, "projects", "Blocked");
        Directory.CreateDirectory(ProjectFileStore.ProjectFilePath(folder)); // a folder where project.json must go

        Assert.False(await Workflow(picker: new ScriptedPicker(folder)).SaveAsAsync());

        Assert.StartsWith("Couldn't save", _status.Message);
        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task Save_new_and_close_leave_the_list_as_it_is()
    {
        var a = WriteProject("A");
        var workflow = Workflow(new ScriptedDialogs(1 /* Don't Save */));
        await workflow.OpenAsync(a);
        await _recent.AddAsync(Path.Combine(_root, "projects", "B"), "B");
        var bytes = ListBytes();

        _undo.Execute(new NamedEdit(_projects, "edited"));
        Assert.True(await workflow.SaveAsync());
        Assert.True(await workflow.NewProjectAsync());
        Assert.True(await workflow.PrepareToCloseAsync());

        Assert.Equal(bytes, ListBytes());
    }

    // ---- Recover --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recovering_a_project_with_a_folder_lists_it()
    {
        var folder = WriteProject("Film");
        WriteRecoveryFile("Film", folder);

        await Workflow(new ScriptedDialogs(Recover)).StartSessionAsync();

        Assert.Equal("Film", _projects.Current.Name);
        var entry = Assert.Single(await _recent.GetAsync());
        Assert.Equal((folder, "Film"), (entry.FolderPath, entry.Name));
    }

    [Fact]
    public async Task Recovering_a_project_whose_folder_is_gone_lists_it_as_unavailable()
    {
        var folder = Path.Combine(_root, "projects", "Gone");
        WriteRecoveryFile("Gone", folder);

        await Workflow(new ScriptedDialogs(Recover)).StartSessionAsync();

        Assert.Equal(new[] { folder }, await Listed());
        Assert.False(await _recent.IsAvailableAsync(folder));
    }

    [Fact]
    public async Task Recovering_a_project_that_was_never_saved_changes_nothing()
    {
        var bytes = await SeedList();
        WriteRecoveryFile("Never saved", folder: null);

        await Workflow(new ScriptedDialogs(Recover)).StartSessionAsync();

        Assert.Equal("Never saved", _projects.Current.Name);
        Assert.Equal(bytes, ListBytes());
    }

    [Fact]
    public async Task A_failed_recover_changes_nothing()
    {
        var bytes = await SeedList();
        var path = WriteRecoveryFile("Film", WriteProject("Film"));
        // The file disappears while the offer is shown: restoring it fails.
        var dialogs = new ScriptedDialogs(Recover) { OnAsk = () => File.Delete(path) };

        await Workflow(dialogs).StartSessionAsync();

        Assert.StartsWith("Couldn't recover", _status.Message);
        Assert.Equal(bytes, ListBytes());
    }

    // ---- A broken store, unavailable entries ----------------------------------------------------------------------------

    [Fact]
    public async Task A_store_that_fails_never_fails_open_save_as_or_recover()
    {
        var store = new ThrowingStore();
        var a = WriteProject("A");
        var workflow = Workflow(new ScriptedDialogs(Recover), new ScriptedPicker(Path.Combine(_root, "projects", "B")), store);

        Assert.True(await workflow.OpenAsync(a));
        Assert.Equal("Opened project \"A\".", _status.Message);
        Assert.True(await workflow.SaveAsAsync());
        Assert.StartsWith("Saved project \"B\"", _status.Message);

        WriteRecoveryFile("Film", WriteProject("Film"));
        await workflow.StartSessionAsync();
        Assert.Equal("Film", _projects.Current.Name);
        Assert.StartsWith("Recovered unsaved changes", _status.Message);
        Assert.Equal(3, store.Adds);
    }

    [Fact]
    public async Task A_list_that_cant_be_written_never_fails_an_open()
    {
        File.WriteAllText(Path.Combine(_root, "config"), "a file where the configuration folder should be");
        var a = WriteProject("A");

        Assert.True(await Workflow().OpenAsync(a));

        Assert.Equal("A", _projects.Current.Name);
        Assert.Equal("Opened project \"A\".", _status.Message);
        Assert.Empty(await _recent.GetAsync());
    }

    [Fact]
    public async Task Projects_that_became_unavailable_stay_listed()
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        var workflow = Workflow();
        await workflow.OpenAsync(a);
        await workflow.OpenAsync(b);
        await workflow.NewProjectAsync();
        Directory.Move(a, a + " moved");   // renamed: the old path is gone

        var c = WriteProject("C");
        await workflow.OpenAsync(c);
        await workflow.OpenAsync(a + " moved");

        Assert.Equal(new[] { a + " moved", c, b, a }, await Listed());
        Assert.False(await _recent.IsAvailableAsync(a));
        Assert.True(await _recent.IsAvailableAsync(b));
    }
}
