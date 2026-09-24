using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Open / Save / Save As and dirty tracking against the save point, with real files
/// in a temp folder.</summary>
public sealed class ProjectServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private UndoRedoService _undo = null!;
    private ProjectService _service;
    private readonly List<string> _events = new();

    public ProjectServiceTests()
    {
        _service = Create(new ProjectFileStore());
    }

    public void Dispose() => _temp.Dispose();

    private ProjectService Create(ProjectFileStore store)
    {
        _undo = new UndoRedoService();
        var service = new ProjectService(_undo, NullLogger<ProjectService>.Instance, store);
        service.ProjectChanged += (_, _) => _events.Add("Project");
        service.MediaAssetsChanged += (_, _) => _events.Add("Media");
        service.TimelineChanged += (_, _) => _events.Add("Timeline");
        service.SaveStateChanged += (_, _) => _events.Add("SaveState");
        return service;
    }

    /// <summary>An undoable edit that goes through NotifyTimelineChanged like the real timeline commands.</summary>
    private sealed class RenameTimeline(IProjectService projects, string name) : IUndoableCommand
    {
        private string? _old;
        public string Description => "Rename";

        public void Execute()
        {
            _old = projects.Current.Timeline.Name;
            projects.Current.Timeline.Name = name;
            projects.NotifyTimelineChanged();
        }

        public void Undo()
        {
            projects.Current.Timeline.Name = _old!;
            projects.NotifyTimelineChanged();
        }
    }

    private void Edit(string name) => _undo.Execute(new RenameTimeline(_service, name));

    private bool Dirty => _service.Current.IsDirty;

    private string ProjectFile(string folder) => ProjectFileStore.ProjectFilePath(folder);

    private string SavedTimelineName(string folder) =>
        ProjectSerializer.Deserialize(File.ReadAllText(ProjectFile(folder)), folder).Timeline.Name;

    private string WriteProjectFolder(string folderName, Action<Core.Entities.Project>? configure = null)
    {
        var folder = _temp.Combine(folderName);
        var project = new Core.Entities.Project { Name = folderName };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.Timeline.Name = "from disk";
        configure?.Invoke(project);
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFile(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    private static ProjectFileStore FailingStore() => new() { BeforeCommit = _ => throw new IOException("disk full") };

    // ---- New / initial state ------------------------------------------------------

    [Fact]
    public void Initial_project_is_clean_and_unsaved()
    {
        Assert.False(Dirty);
        Assert.Null(_service.Current.ProjectFolderPath);
    }

    [Fact]
    public void Edits_make_the_project_dirty_and_undo_makes_it_clean_again()
    {
        Edit("a");
        Assert.True(Dirty);

        _undo.Undo();
        Assert.False(Dirty);

        _undo.Redo();
        Assert.True(Dirty);
    }

    [Fact]
    public void Media_import_makes_the_project_dirty_and_is_not_undone()
    {
        _service.AddMediaAssets(new[] { new MediaAsset { FilePath = _temp.Combine("a.wav"), Kind = MediaKind.Audio } });

        Assert.True(Dirty);
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public async Task Duplicate_only_import_does_not_make_the_project_dirty()
    {
        var path = _temp.Combine("a.wav");
        _service.AddMediaAssets(new[] { new MediaAsset { FilePath = path, Kind = MediaKind.Audio } });
        await _service.SaveAsAsync(_temp.Combine("P"));

        var result = _service.AddMediaAssets(new[] { new MediaAsset { FilePath = path, Kind = MediaKind.Audio } });

        Assert.Equal(1, result.DuplicateCount);
        Assert.False(Dirty);
    }

    [Fact]
    public void New_resets_history_and_is_clean()
    {
        Edit("a");
        _service.AddMediaAssets(new[] { new MediaAsset { FilePath = _temp.Combine("a.wav"), Kind = MediaKind.Audio } });
        _events.Clear();

        _service.CreateNew("Fresh");

        Assert.False(Dirty);
        Assert.False(_undo.CanUndo);
        Assert.False(_undo.CanRedo);
        Assert.Equal("Fresh", _service.Current.Name);
        Assert.Contains("SaveState", _events);
    }

    // ---- Save As / Save -----------------------------------------------------------------

    [Fact]
    public async Task Save_without_a_folder_requires_save_as()
    {
        Edit("a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveAsync());

        Assert.True(Dirty);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Save_as_writes_the_file_and_adopts_the_folder_and_its_name()
    {
        Edit("edited");
        var folder = _temp.Combine("My Film");
        _events.Clear();

        await _service.SaveAsAsync(folder);

        Assert.True(File.Exists(ProjectFile(folder)));
        Assert.Equal(folder, _service.Current.ProjectFolderPath);
        Assert.Equal("My Film", _service.Current.Name);
        Assert.False(Dirty);
        Assert.Contains("SaveState", _events);
        var saved = ProjectSerializer.Deserialize(File.ReadAllText(ProjectFile(folder)), folder);
        Assert.Equal("My Film", saved.Name);
        Assert.Equal("edited", saved.Timeline.Name);
    }

    [Fact]
    public async Task Save_as_accepts_a_trailing_separator()
    {
        var folder = _temp.Combine("Trailing");

        await _service.SaveAsAsync(folder + Path.DirectorySeparatorChar);

        Assert.Equal(folder, _service.Current.ProjectFolderPath);
        Assert.Equal("Trailing", _service.Current.Name);
    }

    [Fact]
    public async Task Save_writes_to_the_project_folder_and_becomes_the_save_point()
    {
        var folder = _temp.Combine("P");
        await _service.SaveAsAsync(folder);
        Edit("second");
        Assert.True(Dirty);

        await _service.SaveAsync();

        Assert.False(Dirty);
        Assert.Equal("second", SavedTimelineName(folder));
    }

    [Fact]
    public async Task Undo_and_redo_back_to_the_saved_state_make_the_project_clean()
    {
        Edit("a");
        Edit("b");
        await _service.SaveAsAsync(_temp.Combine("P"));

        _undo.Undo();
        Assert.True(Dirty);
        _undo.Redo();
        Assert.False(Dirty);

        Edit("c");
        Assert.True(Dirty);
        _undo.Undo();
        Assert.False(Dirty);
    }

    [Fact]
    public async Task Edit_after_undoing_past_the_save_point_keeps_the_project_dirty()
    {
        Edit("a");
        await _service.SaveAsAsync(_temp.Combine("P"));
        _undo.Undo();

        Edit("b"); // the saved state can no longer be reached

        Assert.True(Dirty);
        _undo.Undo();
        Assert.True(Dirty);
    }

    [Fact]
    public async Task Media_import_after_save_is_unsaved_until_the_next_save()
    {
        await _service.SaveAsAsync(_temp.Combine("P"));
        _service.AddMediaAssets(new[] { new MediaAsset { FilePath = _temp.Combine("a.wav"), Kind = MediaKind.Audio } });
        Assert.True(Dirty);

        Edit("x");
        _undo.Undo();
        Assert.True(Dirty); // the import isn't undoable, so the history alone doesn't make it clean

        await _service.SaveAsync();
        Assert.False(Dirty);
    }

    [Fact]
    public async Task Failed_save_as_changes_nothing()
    {
        _service = Create(FailingStore());
        Edit("a");
        var name = _service.Current.Name;
        var folder = _temp.Combine("P");
        _events.Clear();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _service.SaveAsAsync(folder));

        Assert.Contains("disk full", ex.Message);
        Assert.True(Dirty);
        Assert.Null(_service.Current.ProjectFolderPath);
        Assert.Equal(name, _service.Current.Name);
        Assert.False(File.Exists(ProjectFile(folder)));
        Assert.DoesNotContain("SaveState", _events);
        _undo.Undo();
        Assert.False(Dirty); // save point still the original clean state
    }

    [Fact]
    public async Task Failed_save_keeps_the_previous_file_and_save_point()
    {
        var fail = false;
        _service = Create(new ProjectFileStore { BeforeCommit = _ => { if (fail) throw new IOException("disk full"); } });
        var folder = _temp.Combine("P");
        Edit("saved");
        await _service.SaveAsAsync(folder);
        var fileBefore = File.ReadAllText(ProjectFile(folder));
        Edit("unsaved");
        fail = true;

        await Assert.ThrowsAsync<ProjectFileException>(() => _service.SaveAsync());

        Assert.Equal(fileBefore, File.ReadAllText(ProjectFile(folder)));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.True(Dirty);
        Assert.Equal(folder, _service.Current.ProjectFolderPath);
        _undo.Undo();
        Assert.False(Dirty); // the save point is still the successful save

        fail = false;
        _undo.Redo();
        await _service.SaveAsync();
        Assert.False(Dirty);
        Assert.Equal("unsaved", SavedTimelineName(folder));
    }

    [Fact]
    public async Task Cancelled_save_changes_nothing()
    {
        Edit("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var folder = _temp.Combine("P");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SaveAsAsync(folder, cts.Token));

        Assert.True(Dirty);
        Assert.Null(_service.Current.ProjectFolderPath);
        Assert.False(File.Exists(ProjectFile(folder)));
    }

    [Fact]
    public async Task Edits_made_while_the_file_is_being_written_stay_unsaved()
    {
        using var reachedCommit = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        _service = Create(new ProjectFileStore
        {
            BeforeCommit = _ =>
            {
                reachedCommit.Release();
                release.Wait();
            }
        });
        Edit("saved");
        var folder = _temp.Combine("P");

        var save = _service.SaveAsAsync(folder);
        await reachedCommit.WaitAsync();
        Edit("made during save");
        release.Release();
        await save;

        Assert.Equal("saved", SavedTimelineName(folder));
        Assert.True(Dirty);
        _undo.Undo();
        Assert.False(Dirty);
    }

    [Fact]
    public async Task Project_replaced_during_save_is_not_affected()
    {
        using var reachedCommit = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        _service = Create(new ProjectFileStore
        {
            BeforeCommit = _ =>
            {
                reachedCommit.Release();
                release.Wait();
            }
        });
        Edit("old project");
        var folder = _temp.Combine("P");

        var save = _service.SaveAsAsync(folder);
        await reachedCommit.WaitAsync();
        var fresh = _service.CreateNew("Fresh");
        Edit("fresh edit");
        release.Release();
        await save;

        Assert.Same(fresh, _service.Current);
        Assert.Null(fresh.ProjectFolderPath);
        Assert.Equal("Fresh", fresh.Name);
        Assert.True(Dirty);
        Assert.Equal("old project", SavedTimelineName(folder));
    }

    [Fact]
    public async Task Save_as_to_another_folder_moves_the_project_there()
    {
        var first = _temp.Combine("First");
        var second = _temp.Combine("Second");
        await _service.SaveAsAsync(first);

        Edit("changed");
        await _service.SaveAsAsync(second);
        Edit("again");
        await _service.SaveAsync();

        Assert.Equal(second, _service.Current.ProjectFolderPath);
        Assert.Equal("Second", _service.Current.Name);
        Assert.Equal("again", SavedTimelineName(second));
        Assert.Equal("Main Sequence", SavedTimelineName(first));
    }

    [Fact]
    public async Task Save_as_into_a_path_that_is_a_file_fails_cleanly()
    {
        var blocker = _temp.CreateFile("blocker");
        Edit("a");

        await Assert.ThrowsAsync<ProjectFileException>(() => _service.SaveAsAsync(blocker));

        Assert.Null(_service.Current.ProjectFolderPath);
        Assert.True(Dirty);
    }

    // ---- Open -------------------------------------------------------------------

    [Fact]
    public async Task Open_replaces_the_project_with_a_clean_one_and_fresh_history()
    {
        var folder = WriteProjectFolder("Opened");
        Edit("a");
        Edit("b");
        _undo.Undo();
        _events.Clear();

        var opened = await _service.OpenAsync(folder);

        Assert.Same(opened, _service.Current);
        Assert.Equal("Opened", opened.Name);
        Assert.Equal("from disk", opened.Timeline.Name);
        Assert.Equal(folder, opened.ProjectFolderPath);
        Assert.False(Dirty);
        Assert.False(_undo.CanUndo);
        Assert.False(_undo.CanRedo);
        Assert.Equal(new[] { "Project", "Media", "Timeline", "SaveState" }, _events);
    }

    [Fact]
    public async Task After_open_edits_and_undo_track_the_opened_state()
    {
        var folder = WriteProjectFolder("Opened");
        await _service.OpenAsync(folder);

        Edit("x");
        Assert.True(Dirty);
        _undo.Undo();
        Assert.False(Dirty);
        Assert.Equal("from disk", _service.Current.Timeline.Name);
    }

    [Fact]
    public async Task Undo_after_open_cannot_reach_the_previous_project()
    {
        var previous = _service.Current;
        Edit("in previous project");
        await _service.OpenAsync(WriteProjectFolder("Opened"));

        _undo.Undo();

        Assert.Equal("in previous project", previous.Timeline.Name);
        Assert.Equal("from disk", _service.Current.Timeline.Name);
    }

    [Fact]
    public async Task Open_then_save_round_trips()
    {
        var folder = WriteProjectFolder("RT");
        var before = File.ReadAllText(ProjectFile(folder));
        await _service.OpenAsync(folder);

        await _service.SaveAsync();

        Assert.Equal(before, File.ReadAllText(ProjectFile(folder)));
        Assert.False(Dirty);
    }

    public static TheoryData<string, string?> BrokenProjects => new()
    {
        { "missing file", null },
        { "damaged json", "{ \"format\": \"AiVideoEditor.Project\", " },
        { "not a project", "{ \"hello\": 1 }" },
        { "newer version", "{ \"format\": \"AiVideoEditor.Project\", \"formatVersion\": 999 }" },
    };

    [Theory]
    [MemberData(nameof(BrokenProjects))]
    public async Task Failed_open_leaves_the_current_project_and_history_untouched(string description, string? content)
    {
        var folder = _temp.Combine("Broken");
        Directory.CreateDirectory(folder);
        if (content is not null) File.WriteAllText(ProjectFile(folder), content);

        var savedFolder = _temp.Combine("Current");
        await _service.SaveAsAsync(savedFolder);
        Edit("a");
        Edit("b");
        _undo.Undo();
        var current = _service.Current;
        _events.Clear();

        await Assert.ThrowsAsync<ProjectFileException>(() => _service.OpenAsync(folder));

        Assert.Same(current, _service.Current);
        Assert.Equal(savedFolder, current.ProjectFolderPath);
        Assert.Equal("a", current.Timeline.Name);
        Assert.True(Dirty, description);
        Assert.True(_undo.CanUndo);
        Assert.True(_undo.CanRedo);
        Assert.Empty(_events);
        _undo.Undo();
        Assert.False(Dirty); // save point is still the one from before the failed open
    }

    [Fact]
    public async Task Failed_open_of_a_structurally_invalid_project_changes_nothing()
    {
        var folder = WriteProjectFolder("Invalid", p =>
            p.Timeline.AudioTracks[0].Clips.Add(new AudioClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromFrame(10, FrameRate.Default) }));
        var current = _service.Current;

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _service.OpenAsync(folder));

        Assert.Contains("not in the project", ex.Message);
        Assert.Same(current, _service.Current);
    }

    [Theory]
    [InlineData("opacity")]
    [InlineData("font size")]
    [InlineData("color")]
    public async Task Failed_open_of_a_project_with_an_invalid_clip_property_changes_nothing(string property)
    {
        var folder = WriteProjectFolder("InvalidProperty", p =>
        {
            var text = new TextClip { Text = "Title", Duration = MediaTime.FromFrame(10, FrameRate.Default) };
            switch (property)
            {
                case "opacity": text.Opacity = 1.5; break;
                case "font size": text.FontSize = 0; break;
                default: text.ColorHex = "white"; break;
            }
            p.Timeline.VideoTracks[0].Clips.Add(text);
        });
        var savedFolder = _temp.Combine("Current");
        await _service.SaveAsAsync(savedFolder);
        Edit("a");
        Edit("b");
        _undo.Undo();
        var current = _service.Current;
        _events.Clear();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _service.OpenAsync(folder));

        Assert.Contains("invalid property", ex.Message);
        Assert.Same(current, _service.Current);
        Assert.Equal(savedFolder, current.ProjectFolderPath);
        Assert.Equal("a", current.Timeline.Name);
        Assert.True(Dirty);
        Assert.True(_undo.CanUndo);
        Assert.True(_undo.CanRedo);
        Assert.Empty(_events);
        _undo.Undo();
        Assert.False(Dirty); // the save point from before the failed open is still there
    }

    [Fact]
    public async Task Cancelled_open_changes_nothing()
    {
        var folder = WriteProjectFolder("Opened");
        var current = _service.Current;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.OpenAsync(folder, cts.Token));

        Assert.Same(current, _service.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Open_with_no_folder_is_rejected(string folder)
    {
        var current = _service.Current;

        await Assert.ThrowsAsync<ProjectFileException>(() => _service.OpenAsync(folder));

        Assert.Same(current, _service.Current);
    }

    [Fact]
    public async Task Opened_media_keeps_metadata_and_is_not_marked_for_analysis()
    {
        var media = _temp.CreateFile("m.wav");
        var folder = WriteProjectFolder("WithMedia", p => p.MediaAssets.Add(new MediaAsset
        {
            FilePath = media,
            Kind = MediaKind.Audio,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = new MediaTime(123), AudioSampleRate = 48000 }
        }));

        await _service.OpenAsync(folder);

        var asset = Assert.Single(_service.Current.MediaAssets);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(123, asset.Metadata!.Duration.Ticks);
        Assert.False(Dirty);
    }
}
