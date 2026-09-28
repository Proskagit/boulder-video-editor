using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D024 Step 9.5b (PO-W4): the waveform cache lives next to the thumbnails — <c>&lt;project&gt;/cache/waveforms</c>,
/// unsaved <c>&lt;unsaved root&gt;/&lt;id&gt;/waveforms</c> — with their life cycle (New / Open / Recover, first Save moves,
/// Save As copies one current variant per asset, startup cleanup), and the two kinds never touch each other's files:
/// both locations run side by side here, as in the app. The shared rules in detail: <c>ThumbnailCacheLocationTests</c>.
/// </summary>
public sealed class WaveformCacheLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly string _unsavedRoot;
    private readonly RecoveryStore _recovery;
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly WaveformCacheLocation _waveforms;
    private readonly ThumbnailCacheLocation _thumbnails;
    private int _changes;

    public WaveformCacheLocationTests()
    {
        _unsavedRoot = Path.Combine(_root, "appdata", "cache", "unsaved");
        _recovery = new RecoveryStore(Path.Combine(_root, "appdata", "recovery"));
        _waveforms = new WaveformCacheLocation(_projects, _recovery, _unsavedRoot, NullLogger<WaveformCacheLocation>.Instance);
        _thumbnails = new ThumbnailCacheLocation(_projects, _recovery, _unsavedRoot, NullLogger<ThumbnailCacheLocation>.Instance);
        _waveforms.Changed += (_, _) => _changes++;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name) => Path.Combine(_root, "projects", name);
    private string UnsavedProject(Guid id) => Path.Combine(_unsavedRoot, id.ToString("N"));
    private string UnsavedOf(Guid id) => Path.Combine(UnsavedProject(id), "waveforms");
    private static string SavedOf(string projectFolder) => Path.Combine(projectFolder, "cache", "waveforms");
    private static string ThumbnailsOf(string projectFolder) => Path.Combine(projectFolder, "cache", "thumbnails");

    private static void Files(string folder, params string[] names)
    {
        Directory.CreateDirectory(folder);
        foreach (var name in names) File.WriteAllText(Path.Combine(folder, name), name);
    }

    private static string[] Names(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order().ToArray() : Array.Empty<string>();

    private async Task SaveAsAsync(string folder)
    {
        await _projects.SaveAsAsync(folder);
        await Task.WhenAll(_waveforms.LastTransfer, _thumbnails.LastTransfer);
    }

    // --- the folder of the current project ----------------------------------------------------------------------

    [Fact]
    public void An_unsaved_project_has_a_waveforms_folder_next_to_its_thumbnails_and_nothing_is_created_by_asking()
    {
        Assert.Equal(UnsavedOf(_projects.Current.Id), _waveforms.CurrentFolder);
        Assert.Equal(UnsavedProject(_projects.Current.Id), Path.GetDirectoryName(_thumbnails.CurrentFolder));
        Assert.False(Directory.Exists(_unsavedRoot));

        _projects.CreateNew("Second");
        Assert.Equal(UnsavedOf(_projects.Current.Id), _waveforms.CurrentFolder);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task An_opened_project_uses_its_folder_s_cache_waveforms_folder()
    {
        var folder = Folder("Film");
        var other = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        await other.SaveAsAsync(folder);

        await _projects.OpenAsync(folder);

        Assert.Equal(SavedOf(folder), _waveforms.CurrentFolder);
        Assert.Equal(Path.Combine(folder, "cache", "waveforms"), WaveformCacheLocation.SavedFolder(folder));
        Assert.Equal(1, _changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recovered_project_uses_the_folder_of_its_id_or_its_project_folder(bool saved)
    {
        var lost = new Core.Entities.Project { Name = "Lost" };
        lost.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        lost.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        var projectFolder = saved ? Folder("Saved") : null;
        if (projectFolder is not null) Directory.CreateDirectory(projectFolder);
        var file = _recovery.PathFor(lost.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ProjectSerializer.SerializeRecovery(lost, new RecoveryInfo(projectFolder, DateTimeOffset.Now, 0, null)));

        await _projects.RestoreRecoveryAsync(file);

        Assert.Equal(saved ? SavedOf(projectFolder!) : UnsavedOf(lost.Id), _waveforms.CurrentFolder);
    }

    // --- Save / Save As -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_save_moves_each_kind_into_its_own_folder_and_removes_the_unsaved_project_folder()
    {
        var id = _projects.Current.Id;
        Files(UnsavedOf(id), "a-1-1-v1.peaks", "b-2-2-v1.peaks");
        Files(_thumbnails.CurrentFolder, "a-1-1-v1.thumb");
        var folder = Folder("Film");

        await SaveAsAsync(folder);

        Assert.Equal(SavedOf(folder), _waveforms.CurrentFolder);
        Assert.Equal(new[] { "a-1-1-v1.peaks", "b-2-2-v1.peaks" }, Names(SavedOf(folder)));
        Assert.Equal(new[] { "a-1-1-v1.thumb" }, Names(ThumbnailsOf(folder)));
        Assert.False(Directory.Exists(UnsavedProject(id)));                       // gone once both kinds are out
        Assert.DoesNotContain(".peaks", File.ReadAllText(Path.Combine(folder, "project.json")));
    }

    [Fact]
    public async Task Each_kind_only_carries_its_own_files()
    {
        var id = _projects.Current.Id;
        Files(UnsavedOf(id), "a-1-1-v1.peaks", "a-1-1-v1.thumb", "notes.txt");   // strays in the waveforms folder

        await SaveAsAsync(Folder("Film"));

        Assert.Equal(new[] { "a-1-1-v1.peaks" }, Names(SavedOf(Folder("Film"))));
        Assert.Equal(new[] { "a-1-1-v1.thumb", "notes.txt" }, Names(UnsavedOf(id))); // not ours: left, so the folder stays
        Assert.Empty(Names(ThumbnailsOf(Folder("Film"))));                        // the thumbnails' folder had none
    }

    [Fact]
    public async Task The_unsaved_project_folder_stays_while_the_other_kind_is_still_there()
    {
        var id = _projects.Current.Id;
        Files(UnsavedOf(id), "a-1-1-v1.peaks");
        var thumbnails = Path.Combine(UnsavedProject(id), "thumbnails");
        Files(thumbnails, "notes.txt");                                          // something the thumbnails don't carry

        await SaveAsAsync(Folder("Film"));

        Assert.Equal(new[] { "a-1-1-v1.peaks" }, Names(SavedOf(Folder("Film"))));
        Assert.False(Directory.Exists(UnsavedOf(id)));                            // the waveforms folder is gone
        Assert.True(File.Exists(Path.Combine(thumbnails, "notes.txt")));          // the other folder and the project folder stay
    }

    [Fact]
    public async Task Save_As_copies_the_waveforms_and_the_old_project_keeps_its_own()
    {
        var first = Folder("First");
        await SaveAsAsync(first);
        Files(SavedOf(first), "a-1-1-v1.peaks");

        var second = Folder("Second");
        await SaveAsAsync(second);

        Assert.Equal(SavedOf(second), _waveforms.CurrentFolder);
        Assert.Equal(new[] { "a-1-1-v1.peaks" }, Names(SavedOf(second)));
        Assert.Equal(new[] { "a-1-1-v1.peaks" }, Names(SavedOf(first)));
    }

    [Fact]
    public async Task Save_As_over_an_earlier_copy_leaves_one_current_waveform_per_asset()
    {
        var asset = Guid.NewGuid().ToString("N");
        var first = Folder("First");
        await SaveAsAsync(first);
        Files(SavedOf(first), $"{asset}-9-1-v1.peaks", $"{asset}-10-2-v1.peaks");  // the current one sorts first by name
        File.SetLastWriteTimeUtc(Path.Combine(SavedOf(first), $"{asset}-9-1-v1.peaks"), DateTime.UtcNow.AddHours(-1));
        var second = Folder("Second");
        Files(SavedOf(second), $"{asset}-8-0-v1.peaks", $"{asset}-1-1-v1.thumb");

        await SaveAsAsync(second);

        Assert.Equal(new[] { $"{asset}-1-1-v1.thumb", $"{asset}-10-2-v1.peaks" }.Order().ToArray(), Names(SavedOf(second)));
    }

    [Fact]
    public async Task A_carry_over_that_fails_never_breaks_the_save()
    {
        var unsaved = _waveforms.CurrentFolder;
        Files(unsaved, "a-1-1-v1.peaks");
        var folder = Folder("Film");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "cache"), "a file where the cache folder would go");

        await SaveAsAsync(folder);

        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(SavedOf(folder), _waveforms.CurrentFolder);
        Assert.Equal(new[] { "a-1-1-v1.peaks" }, Names(unsaved));
    }

    // --- startup cleanup ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_cleanup_removes_orphaned_waveforms_and_the_project_folder_once_both_kinds_are_gone()
    {
        var recoverable = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        Files(UnsavedOf(recoverable), "a.peaks");
        Files(UnsavedOf(orphan), "b.peaks");
        Files(Path.Combine(UnsavedProject(orphan), "thumbnails"), "b.thumb");
        Files(UnsavedOf(_projects.Current.Id), "c.peaks");
        Files(Path.Combine(_unsavedRoot, "not-a-project", "waveforms"), "keep.peaks");
        Directory.CreateDirectory(Path.GetDirectoryName(_recovery.PathFor(recoverable))!);
        File.WriteAllText(_recovery.PathFor(recoverable), "{}");

        await _waveforms.CleanUpUnsavedAsync();

        Assert.False(Directory.Exists(UnsavedOf(orphan)));
        Assert.True(File.Exists(Path.Combine(UnsavedProject(orphan), "thumbnails", "b.thumb"))); // not the waveforms' to remove
        Assert.True(Directory.Exists(UnsavedOf(recoverable)));
        Assert.True(Directory.Exists(UnsavedOf(_projects.Current.Id)));
        Assert.True(File.Exists(Path.Combine(_unsavedRoot, "not-a-project", "waveforms", "keep.peaks")));

        await _thumbnails.CleanUpUnsavedAsync();

        Assert.False(Directory.Exists(UnsavedProject(orphan)));
    }

    [Fact]
    public async Task Startup_cleanup_without_any_cache_is_a_no_op()
    {
        await _waveforms.CleanUpUnsavedAsync();
        Assert.False(Directory.Exists(_unsavedRoot));
    }
}
