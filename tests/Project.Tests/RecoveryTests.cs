using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Finding recovery files at startup and restoring one (a "crash" is simulated by a
/// file written by a process that isn't running, or by an earlier session object).</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly RecoveryStore _store;
    private readonly AutosaveService _autosave;

    public RecoveryTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _store = new RecoveryStore(_temp.Combine("recovery"));
        _autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance);
    }

    public void Dispose()
    {
        _autosave.Dispose();
        _temp.Dispose();
    }

    private static Core.Entities.Project SampleProject(string name, string timelineName = "recovered")
    {
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.Timeline.Name = timelineName;
        return project;
    }

    /// <summary>A recovery file left by a session that crashed (its process is not running).</summary>
    private string WriteLeftover(Core.Entities.Project project, string? folder, DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(_store.RootFolder);
        var path = _store.PathFor(project.Id);
        var info = new RecoveryInfo(folder, at ?? DateTimeOffset.UtcNow, ProcessId: 0, ProcessStartTime: null);
        File.WriteAllText(path, ProjectSerializer.SerializeRecovery(project, info));
        return path;
    }

    // ---- finding ------------------------------------------------------------------

    [Fact]
    public async Task No_recovery_files()
    {
        Assert.Same(RecoveryScanResult.None, await _autosave.FindRecoveryAsync());
    }

    [Fact]
    public async Task Leftover_from_a_crashed_session_is_offered()
    {
        var project = SampleProject("Crashed");
        var at = new DateTimeOffset(2026, 9, 23, 8, 30, 0, TimeSpan.Zero);
        var path = WriteLeftover(project, _temp.Combine("Crashed"), at);

        var scan = await _autosave.FindRecoveryAsync();

        var candidate = Assert.IsType<RecoveryCandidate>(scan.Candidate);
        Assert.Equal(path, candidate.FilePath);
        Assert.Equal(project.Id, candidate.ProjectId);
        Assert.Equal("Crashed", candidate.ProjectName);
        Assert.Equal(_temp.Combine("Crashed"), candidate.ProjectFolderPath);
        Assert.Equal(at, candidate.AutosavedAt);
        Assert.Equal(1, scan.CandidateCount);
        Assert.Equal(0, scan.DamagedFiles);
    }

    [Fact]
    public async Task Leftover_of_a_dead_process_id_is_offered()
    {
        var project = SampleProject("Crashed");
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(project.Id), ProjectSerializer.SerializeRecovery(project,
            new RecoveryInfo(null, DateTimeOffset.UtcNow, int.MaxValue, DateTimeOffset.UtcNow.AddHours(-1))));

        Assert.NotNull((await _autosave.FindRecoveryAsync()).Candidate);
    }

    [Fact]
    public async Task Recovery_file_of_a_running_instance_is_not_offered()
    {
        // Written by this (running) process — like a second editor instance that is still open.
        _undo.Execute(new RenameTimelineCommand(_projects, "live"));
        await _autosave.AutosaveNowAsync();
        using var otherInstance = new AutosaveService(
            new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance), _store, NullLogger<AutosaveService>.Instance);

        var scan = await otherInstance.FindRecoveryAsync();

        Assert.Null(scan.Candidate);
        Assert.True(File.Exists(_store.PathFor(_projects.Current.Id)));
    }

    [Fact]
    public async Task Damaged_recovery_file_is_set_aside_and_counted()
    {
        Directory.CreateDirectory(_store.RootFolder);
        var damaged = _store.PathFor(Guid.NewGuid());
        File.WriteAllText(damaged, "{ \"format\": \"AiVideoEditor.Recovery\", ");

        var scan = await _autosave.FindRecoveryAsync();

        Assert.Null(scan.Candidate);
        Assert.Equal(1, scan.DamagedFiles);
        Assert.False(File.Exists(damaged));
        Assert.Single(Directory.GetFiles(_store.RootFolder, "*.damaged"));
        Assert.Same(RecoveryScanResult.None, await _autosave.FindRecoveryAsync()); // not reported again
    }

    [Fact]
    public async Task Damaged_file_does_not_hide_a_good_one()
    {
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(Guid.NewGuid()), "garbage");
        var good = SampleProject("Good");
        WriteLeftover(good, null);

        var scan = await _autosave.FindRecoveryAsync();

        Assert.Equal(good.Id, scan.Candidate!.ProjectId);
        Assert.Equal(1, scan.DamagedFiles);
    }

    [Fact]
    public async Task Recovery_older_than_the_saved_project_is_obsolete_and_removed()
    {
        var folder = _temp.Combine("Film");
        var project = SampleProject("Film");
        var path = WriteLeftover(project, folder, DateTimeOffset.UtcNow.AddMinutes(-10));
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder)); // saved later

        var scan = await _autosave.FindRecoveryAsync();

        Assert.Null(scan.Candidate);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Recovery_newer_than_the_saved_project_is_offered()
    {
        var folder = _temp.Combine("Film");
        var project = SampleProject("Film");
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        File.SetLastWriteTimeUtc(ProjectFileStore.ProjectFilePath(folder), DateTime.UtcNow.AddMinutes(-10));
        WriteLeftover(project, folder);

        Assert.NotNull((await _autosave.FindRecoveryAsync()).Candidate);
    }

    [Fact]
    public async Task Newest_of_several_leftovers_is_offered_first()
    {
        WriteLeftover(SampleProject("Older"), null, DateTimeOffset.UtcNow.AddHours(-2));
        WriteLeftover(SampleProject("Newer"), null, DateTimeOffset.UtcNow.AddHours(-1));

        var scan = await _autosave.FindRecoveryAsync();

        Assert.Equal("Newer", scan.Candidate!.ProjectName);
        Assert.Equal(2, scan.CandidateCount);
    }

    [Fact]
    public async Task Discarding_a_candidate_deletes_its_file()
    {
        WriteLeftover(SampleProject("Crashed"), null);
        var candidate = (await _autosave.FindRecoveryAsync()).Candidate!;

        await _autosave.DiscardRecoveryAsync(candidate);

        Assert.False(File.Exists(candidate.FilePath));
        Assert.Same(RecoveryScanResult.None, await _autosave.FindRecoveryAsync());
    }

    // ---- restoring ----------------------------------------------------------------

    [Fact]
    public async Task Crash_and_restore_brings_back_the_unsaved_state_of_a_saved_project()
    {
        // Session 1: save, edit, autosave, then "crash" (no shutdown).
        var folder = _temp.Combine("Film");
        await _projects.SaveAsAsync(folder);
        _undo.Execute(new RenameTimelineCommand(_projects, "unsaved work"));
        await _autosave.AutosaveNowAsync();
        var savedBefore = File.ReadAllText(ProjectFileStore.ProjectFilePath(folder));

        // Session 2.
        var undo2 = new UndoRedoService();
        var projects2 = new ProjectService(undo2, NullLogger<ProjectService>.Instance);
        using var autosave2 = new AutosaveService(projects2, _store, NullLogger<AutosaveService>.Instance);
        var candidate = (await autosave2.FindRecoveryAsync()).Candidate;
        // Session 1's process is this test process, which is still running — so treat the file as a
        // leftover by rewriting its process info, the way it would look after a real crash.
        Assert.Null(candidate);
        var (p, i) = ProjectSerializer.DeserializeRecovery(File.ReadAllText(_store.PathFor(_projects.Current.Id)));
        File.WriteAllText(_store.PathFor(p.Id), ProjectSerializer.SerializeRecovery(p, i with { ProcessId = 0, ProcessStartTime = null }));
        candidate = (await autosave2.FindRecoveryAsync()).Candidate!;

        var restored = await projects2.RestoreRecoveryAsync(candidate.FilePath);

        Assert.Same(restored, projects2.Current);
        Assert.Equal("unsaved work", restored.Timeline.Name);
        Assert.Equal("Film", restored.Name);
        Assert.Equal(folder, restored.ProjectFolderPath);
        Assert.True(restored.IsDirty);
        Assert.False(undo2.CanUndo);
        Assert.Equal(savedBefore, File.ReadAllText(ProjectFileStore.ProjectFilePath(folder))); // restore doesn't save

        await projects2.SaveAsync();

        Assert.False(restored.IsDirty);
        Assert.Equal("unsaved work", ProjectSerializer.Deserialize(File.ReadAllText(ProjectFileStore.ProjectFilePath(folder)), folder).Timeline.Name);
        Assert.True(await Wait.UntilAsync(() => !File.Exists(candidate.FilePath)));
    }

    [Fact]
    public async Task Restored_never_saved_project_needs_save_as()
    {
        var path = WriteLeftover(SampleProject("Untitled Project"), null);

        var restored = await _projects.RestoreRecoveryAsync(path);

        Assert.Null(restored.ProjectFolderPath);
        Assert.True(restored.IsDirty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.SaveAsync());
    }

    [Fact]
    public async Task Restore_marks_missing_media_without_making_extra_changes()
    {
        var project = SampleProject("Film");
        var present = _temp.CreateFile("a.wav");
        project.MediaAssets.Add(new MediaAsset { FilePath = present, Kind = MediaKind.Audio });
        project.MediaAssets.Add(new MediaAsset { FilePath = _temp.Combine("gone.wav"), Kind = MediaKind.Audio });

        var restored = await _projects.RestoreRecoveryAsync(WriteLeftover(project, null));

        Assert.False(restored.MediaAssets[0].IsMissing);
        Assert.True(restored.MediaAssets[1].IsMissing);
    }

    [Fact]
    public async Task Damaged_recovery_file_on_restore_changes_nothing()
    {
        _undo.Execute(new RenameTimelineCommand(_projects, "current work"));
        var current = _projects.Current;
        Directory.CreateDirectory(_store.RootFolder);
        var path = _store.PathFor(Guid.NewGuid());
        File.WriteAllText(path, "{ damaged");
        var events = 0;
        _projects.ProjectChanged += (_, _) => events++;

        await Assert.ThrowsAsync<ProjectFileException>(() => _projects.RestoreRecoveryAsync(path));

        Assert.Same(current, _projects.Current);
        Assert.Equal("current work", current.Timeline.Name);
        Assert.True(_undo.CanUndo);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Structurally_invalid_recovery_is_rejected_like_open()
    {
        var project = SampleProject("Broken");
        project.Timeline.AudioTracks[0].Clips.Add(new AudioClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromFrame(5, FrameRate.Default) });
        var path = WriteLeftover(project, null);
        var current = _projects.Current;

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _projects.RestoreRecoveryAsync(path));

        Assert.Contains("not in the project", ex.Message);
        Assert.Same(current, _projects.Current);
    }

    [Fact]
    public async Task Missing_recovery_file_on_restore_changes_nothing()
    {
        var current = _projects.Current;

        await Assert.ThrowsAsync<ProjectFileException>(() => _projects.RestoreRecoveryAsync(_temp.Combine("nope.json")));

        Assert.Same(current, _projects.Current);
    }
}
