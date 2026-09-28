using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D024 Step 9.4b: where the current project's thumbnails are cached, through the project's life — New, Open, Recover,
/// Save, Save As — and the startup cleanup of unsaved caches. Everything in a temporary folder: the unsaved root and
/// the recovery folder are given to the class, the real %LOCALAPPDATA% is never touched.
/// </summary>
public sealed class ThumbnailCacheLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly string _unsavedRoot;
    private readonly RecoveryStore _recovery;
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly ThumbnailCacheLocation _location;
    private int _changes;

    public ThumbnailCacheLocationTests()
    {
        _unsavedRoot = Path.Combine(_root, "appdata", "cache", "unsaved");
        _recovery = new RecoveryStore(Path.Combine(_root, "appdata", "recovery"));
        _location = new ThumbnailCacheLocation(_projects, _recovery, _unsavedRoot, NullLogger<ThumbnailCacheLocation>.Instance);
        _location.Changed += (_, _) => _changes++;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name) => Path.Combine(_root, "projects", name);

    private string UnsavedOf(Guid id) => Path.Combine(_unsavedRoot, id.ToString("N"), "thumbnails");

    private static string SavedOf(string projectFolder) => Path.Combine(projectFolder, "cache", "thumbnails");

    private static void Thumbs(string folder, params string[] names)
    {
        Directory.CreateDirectory(folder);
        foreach (var name in names) File.WriteAllText(Path.Combine(folder, name), name);
    }

    private static string[] Names(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order().ToArray() : Array.Empty<string>();

    // --- the folder of the current project ----------------------------------------------------------------------

    [Fact]
    public void An_unsaved_project_has_its_own_folder_under_the_unsaved_root_and_nothing_is_created_by_asking()
    {
        Assert.Equal(UnsavedOf(_projects.Current.Id), _location.CurrentFolder);
        Assert.False(Directory.Exists(_unsavedRoot));
    }

    [Fact]
    public void Unsaved_projects_never_share_a_folder()
    {
        var first = _location.CurrentFolder;
        _projects.CreateNew("Second");
        var second = _location.CurrentFolder;
        _projects.CreateNew("Third");

        Assert.Equal(3, new[] { first, second, _location.CurrentFolder }.Distinct().Count());
        Assert.Equal(UnsavedOf(_projects.Current.Id), _location.CurrentFolder);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public async Task An_opened_project_uses_its_folder_s_cache_thumbnails_folder()
    {
        var folder = Folder("Film");
        var other = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        await other.SaveAsAsync(folder);

        await _projects.OpenAsync(folder);

        Assert.Equal(SavedOf(folder), _location.CurrentFolder);
        Assert.Equal(1, _changes);
        Assert.False(Directory.Exists(Path.Combine(folder, "thumbnails"))); // the former <project>/thumbnails is gone for good
    }

    [Theory]
    [InlineData(false)] // a project that was never saved: its unsaved folder, same id → its thumbnails are found again
    [InlineData(true)]  // a saved project with unsaved changes: its project folder
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

        Assert.Equal(lost.Id, _projects.Current.Id);
        Assert.Equal(saved ? SavedOf(projectFolder!) : UnsavedOf(lost.Id), _location.CurrentFolder);
    }

    // --- Save / Save As -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_save_switches_to_the_project_folder_and_moves_the_thumbnails_there()
    {
        var unsaved = _location.CurrentFolder;
        Thumbs(unsaved, "a-1-1-v1.thumb", "b-2-2-v1.thumb");
        var folder = Folder("Film");

        await _projects.SaveAsAsync(folder);
        await _location.LastTransfer;

        Assert.Equal(SavedOf(folder), _location.CurrentFolder);
        Assert.Equal(1, _changes);
        Assert.Equal(new[] { "a-1-1-v1.thumb", "b-2-2-v1.thumb" }, Names(SavedOf(folder)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(unsaved)));   // the unsaved project folder is gone
        Assert.DoesNotContain(".thumb", File.ReadAllText(Path.Combine(folder, "project.json"))); // nothing of it in project.json
    }

    [Fact]
    public async Task Saving_again_to_the_same_folder_changes_nothing()
    {
        await _projects.SaveAsAsync(Folder("Film"));
        await _location.LastTransfer;
        var transfer = _location.LastTransfer;

        await _projects.SaveAsync();

        Assert.Equal(1, _changes);
        Assert.Same(transfer, _location.LastTransfer);
    }

    [Fact]
    public async Task Save_As_gives_the_new_project_folder_its_own_cache_and_leaves_the_old_one_as_it_was()
    {
        var first = Folder("First");
        await _projects.SaveAsAsync(first);
        await _location.LastTransfer;
        Thumbs(SavedOf(first), "a-1-1-v1.thumb");

        var second = Folder("Second");
        await _projects.SaveAsAsync(second);
        await _location.LastTransfer;

        Assert.Equal(SavedOf(second), _location.CurrentFolder);
        Assert.Equal(new[] { "a-1-1-v1.thumb" }, Names(SavedOf(second)));  // copied: the same assets' thumbnails
        Assert.Equal(new[] { "a-1-1-v1.thumb" }, Names(SavedOf(first)));   // the first project keeps its cache
        File.Delete(Path.Combine(SavedOf(second), "a-1-1-v1.thumb"));      // separate files, not shared
        Assert.Single(Names(SavedOf(first)));
    }

    [Fact]
    public async Task A_carry_over_that_fails_never_breaks_the_save()
    {
        var unsaved = _location.CurrentFolder;
        Thumbs(unsaved, "a-1-1-v1.thumb");
        var folder = Folder("Film");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "cache"), "a file where the cache folder would go");

        await _projects.SaveAsAsync(folder);
        await _location.LastTransfer;                                        // completes, doesn't throw

        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(SavedOf(folder), _location.CurrentFolder);             // thumbnails will simply be made again
        Assert.Equal(new[] { "a-1-1-v1.thumb" }, Names(unsaved));          // nothing lost on the way
    }

    // --- carrying over into a folder that already has thumbnails (Save As over an earlier copy of the project) -------

    private static string Thumb(Guid asset, int size, int time) => $"{asset:N}-{size:x}-{time:x}-v1.thumb";

    [Fact]
    public async Task A_carried_file_replaces_a_file_of_the_same_name_in_the_target()
    {
        var asset = Guid.NewGuid();
        var first = Folder("First");
        await _projects.SaveAsAsync(first);
        await _location.LastTransfer;
        Directory.CreateDirectory(SavedOf(first));
        File.WriteAllText(Path.Combine(SavedOf(first), Thumb(asset, 1, 1)), "current");
        var second = Folder("Second");
        Directory.CreateDirectory(SavedOf(second));
        File.WriteAllText(Path.Combine(SavedOf(second), Thumb(asset, 1, 1)), "left by an earlier copy");

        await _projects.SaveAsAsync(second);
        await _location.LastTransfer;

        Assert.Equal("current", File.ReadAllText(Path.Combine(SavedOf(second), Thumb(asset, 1, 1))));
        Assert.Equal("current", File.ReadAllText(Path.Combine(SavedOf(first), Thumb(asset, 1, 1)))); // the source keeps its copy
    }

    [Fact]
    public async Task After_the_carry_over_each_asset_has_only_its_current_thumbnail_and_other_assets_are_untouched()
    {
        var asset = Guid.NewGuid();
        var other = Guid.NewGuid();
        Thumbs(_location.CurrentFolder, Thumb(asset, 7, 7));                        // the current one (unsaved project)
        var folder = Folder("Film");
        Thumbs(SavedOf(folder), Thumb(asset, 5, 5), Thumb(asset, 6, 6),            // older variants of the same asset
            Thumb(other, 1, 1), "notes.txt");                                       // another asset, a foreign file

        await _projects.SaveAsAsync(folder);
        await _location.LastTransfer;

        Assert.Equal(new[] { Thumb(asset, 7, 7), Thumb(other, 1, 1), "notes.txt" }.Order().ToArray(), Names(SavedOf(folder)));
    }

    [Fact]
    public async Task A_file_that_can_t_be_carried_over_stays_in_the_source_and_the_target_is_not_cleaned_for_it()
    {
        var asset = Guid.NewGuid();
        var unsaved = _location.CurrentFolder;
        Thumbs(unsaved, Thumb(asset, 7, 7));
        var folder = Folder("Film");
        Thumbs(SavedOf(folder), Thumb(asset, 7, 7), Thumb(asset, 5, 5));

        using (new FileStream(Path.Combine(SavedOf(folder), Thumb(asset, 7, 7)), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _projects.SaveAsAsync(folder);                                   // the same-name target is locked
            await _location.LastTransfer;                                          // completes, doesn't throw
        }

        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(new[] { Thumb(asset, 7, 7) }, Names(unsaved));               // the current thumbnail is not lost
        Assert.Equal(new[] { Thumb(asset, 5, 5), Thumb(asset, 7, 7) }.Order().ToArray(), Names(SavedOf(folder))); // nothing removed
    }

    // --- several source variants of one asset: only the current one (latest last-write time) is carried -------------

    /// <summary>Two variants of <paramref name="asset"/> in <paramref name="folder"/>, the current one with the later time.
    /// By default the source grew from 9 to 16 bytes, so the current name (…-10-…) sorts first by name; with
    /// <paramref name="currentSortsFirst"/> false it shrank from 16 to 9 bytes, so the current name sorts last.</summary>
    private static (string Old, string Current) TwoVariants(string folder, Guid asset, bool currentSortsFirst = true)
    {
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, Thumb(asset, currentSortsFirst ? 9 : 16, 1));
        var current = Path.Combine(folder, Thumb(asset, currentSortsFirst ? 16 : 9, 2));
        File.WriteAllText(old, "old");
        File.WriteAllText(current, "current");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(currentSortsFirst, string.CompareOrdinal(Path.GetFileName(current), Path.GetFileName(old)) < 0);
        return (Path.GetFileName(old), Path.GetFileName(current));
    }

    [Theory]
    [InlineData(true)]  // the current variant sorts first by name (the case that used to keep the old one)
    [InlineData(false)] // … and last: the choice follows the time, never the name order
    public async Task Moving_out_of_the_unsaved_cache_carries_only_the_latest_variant_and_removes_all_source_variants(bool currentSortsFirst)
    {
        var asset = Guid.NewGuid();
        var unsaved = _location.CurrentFolder;
        var (_, current) = TwoVariants(unsaved, asset, currentSortsFirst);
        var folder = Folder("Film");
        Thumbs(SavedOf(folder), Thumb(asset, 5, 5));                               // a stale one already in the target

        await _projects.SaveAsAsync(folder);
        await _location.LastTransfer;

        Assert.Equal(new[] { current }, Names(SavedOf(folder)));
        Assert.Equal("current", File.ReadAllText(Path.Combine(SavedOf(folder), current)));
        Assert.Empty(Names(unsaved));
    }

    [Fact]
    public async Task Copying_from_a_saved_cache_carries_only_the_latest_variant_and_leaves_the_source_as_it_was()
    {
        var asset = Guid.NewGuid();
        var first = Folder("First");
        await _projects.SaveAsAsync(first);
        await _location.LastTransfer;
        var (old, current) = TwoVariants(SavedOf(first), asset);

        var second = Folder("Second");
        await _projects.SaveAsAsync(second);
        await _location.LastTransfer;

        Assert.Equal(new[] { current }, Names(SavedOf(second)));
        Assert.Equal(new[] { current, old }.Order().ToArray(), Names(SavedOf(first)));
    }

    [Fact]
    public async Task When_the_copy_fails_every_source_variant_stays_and_the_target_is_not_cleaned_for_that_asset()
    {
        var asset = Guid.NewGuid();
        var unsaved = _location.CurrentFolder;
        var (old, current) = TwoVariants(unsaved, asset);
        var folder = Folder("Film");
        Thumbs(SavedOf(folder), current, Thumb(asset, 5, 5));

        using (new FileStream(Path.Combine(SavedOf(folder), current), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _projects.SaveAsAsync(folder);
            await _location.LastTransfer;
        }

        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.Equal(new[] { current, old }.Order().ToArray(), Names(unsaved));
        Assert.Equal(new[] { current, Thumb(asset, 5, 5) }.Order().ToArray(), Names(SavedOf(folder)));
    }

    [Fact]
    public async Task A_variant_whose_time_can_t_be_read_keeps_its_whole_asset_where_it_is_and_other_assets_are_carried()
    {
        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var unreadable = "";
        var location = new ThumbnailCacheLocation(projects, _recovery, _unsavedRoot, NullLogger<ThumbnailCacheLocation>.Instance)
        {
            ReadLastWriteUtc = path => string.Equals(Path.GetFileName(path), unreadable, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("no access to the file's time")
                : File.GetLastWriteTimeUtc(path)
        };
        var asset = Guid.NewGuid();
        var other = Guid.NewGuid();
        var unsaved = location.CurrentFolder;
        var (old, current) = TwoVariants(unsaved, asset);
        Thumbs(unsaved, Thumb(other, 1, 1));
        unreadable = old;
        var folder = Folder("Film");
        Thumbs(SavedOf(folder), Thumb(asset, 5, 5));

        await projects.SaveAsAsync(folder);
        await location.LastTransfer;

        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.Equal(new[] { current, old }.Order().ToArray(), Names(unsaved));                         // nothing of the asset removed
        Assert.Equal(new[] { Thumb(asset, 5, 5), Thumb(other, 1, 1) }.Order().ToArray(), Names(SavedOf(folder))); // its target untouched, the other asset carried
    }

    // --- startup cleanup ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_cleanup_removes_unsaved_caches_that_no_recovery_file_can_bring_back()
    {
        var recoverable = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        Thumbs(UnsavedOf(recoverable), "a.thumb");
        Thumbs(UnsavedOf(orphan), "b.thumb");
        Thumbs(UnsavedOf(_projects.Current.Id), "c.thumb");                  // the project open right now
        Thumbs(Path.Combine(_unsavedRoot, "not-a-project"), "keep.txt");     // not a folder this class makes
        Directory.CreateDirectory(Path.GetDirectoryName(_recovery.PathFor(recoverable))!);
        File.WriteAllText(_recovery.PathFor(recoverable), "{}");

        await _location.CleanUpUnsavedAsync();

        Assert.True(Directory.Exists(UnsavedOf(recoverable)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(UnsavedOf(orphan))));
        Assert.True(Directory.Exists(UnsavedOf(_projects.Current.Id)));
        Assert.True(File.Exists(Path.Combine(_unsavedRoot, "not-a-project", "keep.txt")));
    }

    [Fact]
    public async Task Startup_cleanup_without_any_cache_is_a_no_op()
    {
        await _location.CleanUpUnsavedAsync();
        Assert.False(Directory.Exists(_unsavedRoot));
    }
}
