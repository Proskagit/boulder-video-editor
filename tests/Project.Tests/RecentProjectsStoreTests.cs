using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>The recent-projects list file (D026 §6, Step 11.7): order, limit, dedupe, damaged / newer / unreadable files,
/// atomic writes, several instances, and availability — all on real files in a temporary folder.</summary>
public sealed class RecentProjectsStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _file;
    private DateTimeOffset _now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public RecentProjectsStoreTests() => _file = _temp.Combine("config", "recent-projects.json");

    public void Dispose() => _temp.Dispose();

    private RecentProjectsStore Store(ProjectFileStore? files = null, TimeSpan? lockTimeout = null,
        Func<string, bool>? projectFileExists = null, Action? onFileWork = null) =>
        new(_file, NullLogger<RecentProjectsStore>.Instance, files ?? new ProjectFileStore())
        {
            Clock = () => _now = _now.AddMinutes(1),
            LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2),
            ProjectFileExists = projectFileExists ?? File.Exists,
            OnFileWork = onFileWork
        };

    private string Folder(string name) => _temp.Combine("projects", name);

    private static IEnumerable<string> Paths(IReadOnlyList<RecentProject> list) => list.Select(e => e.FolderPath);

    private string Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, json);
        return json;
    }

    private static string Entry(string folder, string name = "P", string time = "2026-10-01T10:00:00+00:00") =>
        $$"""{ "folderPath": {{System.Text.Json.JsonSerializer.Serialize(folder)}}, "name": "{{name}}", "lastUsedAt": "{{time}}" }""";

    private static string List(params string[] entries) =>
        $$"""{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 1, "projects": [ {{string.Join(", ", entries)}} ] }""";

    private string[] DamagedFiles() => Directory.GetFiles(Path.GetDirectoryName(_file)!, "*.damaged");

    private string[] TempFiles() => Directory.GetFiles(Path.GetDirectoryName(_file)!, "*.tmp");

    // ---- Reading, adding, order -----------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_file_the_list_is_empty_and_no_folder_is_created()
    {
        Assert.Empty(await Store().GetAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(_file)));
    }

    [Fact]
    public async Task Added_projects_come_most_recent_first_with_their_name_and_time()
    {
        var store = Store();

        Assert.True(await store.AddAsync(Folder("A"), "Alpha"));
        Assert.True(await store.AddAsync(Folder("B"), "Beta"));

        var list = await store.GetAsync();
        Assert.Equal(new[] { Folder("B"), Folder("A") }, Paths(list));
        Assert.Equal(new[] { "Beta", "Alpha" }, list.Select(e => e.Name));
        Assert.True(list[0].LastUsedAt > list[1].LastUsedAt);
        Assert.True(File.Exists(_file));
    }

    [Fact]
    public async Task Adding_a_listed_project_again_moves_it_first_with_the_new_name_time_and_spelling()
    {
        var store = Store();
        await store.AddAsync(Folder("A"), "Alpha");
        await store.AddAsync(Folder("B"), "Beta");
        var before = (await store.GetAsync())[1];

        var respelled = Folder("A").ToUpperInvariant() + Path.DirectorySeparatorChar;
        await store.AddAsync(respelled, "Alpha 2");

        var list = await store.GetAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(Path.TrimEndingDirectorySeparator(respelled), list[0].FolderPath);
        Assert.Equal("Alpha 2", list[0].Name);
        Assert.True(list[0].LastUsedAt > before.LastUsedAt);
        Assert.Equal(Folder("B"), list[1].FolderPath);
    }

    public static TheoryData<string> SpellingsOfFolderA => new()
    {
        "UPPER",          // case
        "trailing",       // a trailing separator
        "dotdot",         // not normalised
        "dot"
    };

    [Theory]
    [MemberData(nameof(SpellingsOfFolderA))]
    public async Task One_folder_spelled_differently_is_one_entry(string spelling)
    {
        var a = Folder("A");
        var other = spelling switch
        {
            "UPPER" => a.ToUpperInvariant(),
            "trailing" => a + Path.DirectorySeparatorChar,
            "dotdot" => Path.Combine(_temp.Path, "projects", "X", "..", "A"),
            _ => Path.Combine(_temp.Path, "projects", ".", "A", ".")
        };
        var store = Store();

        await store.AddAsync(a, "first");
        await store.AddAsync(other, "second");

        var entry = Assert.Single(await store.GetAsync());
        Assert.Equal("second", entry.Name);
        Assert.Equal(a, entry.FolderPath, ignoreCase: true);
        // Stored once, in the normalised form — not only collapsed when read.
        var text = await File.ReadAllTextAsync(_file);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "folderPath"));
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(entry.FolderPath), text);
    }

    [Fact]
    public async Task At_most_ten_projects_are_kept_the_oldest_dropped()
    {
        var store = Store();
        for (var i = 1; i <= 11; i++)
            await store.AddAsync(Folder($"P{i}"), $"P{i}");

        var list = await store.GetAsync();
        Assert.Equal(IRecentProjectsStore.MaxEntries, list.Count);
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => Folder($"P{i}")), Paths(list));
    }

    [Fact]
    public async Task Remove_takes_out_only_that_project()
    {
        var store = Store();
        await store.AddAsync(Folder("A"), "A");
        await store.AddAsync(Folder("B"), "B");
        await store.AddAsync(Folder("C"), "C");

        Assert.True(await store.RemoveAsync(Folder("B").ToUpperInvariant() + Path.DirectorySeparatorChar));

        Assert.Equal(new[] { Folder("C"), Folder("A") }, Paths(await store.GetAsync()));
    }

    [Fact]
    public async Task Removing_a_project_that_is_not_listed_changes_nothing()
    {
        var store = Store();
        await store.AddAsync(Folder("A"), "A");
        var bytes = await File.ReadAllBytesAsync(_file);
        var time = File.GetLastWriteTimeUtc(_file);

        Assert.True(await store.RemoveAsync(Folder("other")));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_file));
        Assert.Equal(time, File.GetLastWriteTimeUtc(_file));
    }

    [Fact]
    public async Task A_new_store_reads_the_saved_list()
    {
        await Store().AddAsync(Folder("A"), "Alpha");
        await Store().AddAsync(Folder("B"), "Beta");
        var written = await Store().GetAsync();

        var reread = await new RecentProjectsStore(_file, NullLogger<RecentProjectsStore>.Instance).GetAsync();

        Assert.Equal(written, reread);
        var text = await File.ReadAllTextAsync(_file);
        Assert.Contains("\"format\": \"AiVideoEditor.RecentProjects\"", text);
        Assert.Contains("\"formatVersion\": 1", text);
        Assert.Contains("\"folderPath\"", text);
        Assert.Contains("\"lastUsedAt\"", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json {")]
    [InlineData("[]")]
    [InlineData("""{ "format": "AiVideoEditor.Project", "formatVersion": 1, "projects": [] }""")]
    [InlineData("""{ "formatVersion": 1, "projects": [] }""")]
    [InlineData("""{ "format": "AiVideoEditor.RecentProjects", "projects": [] }""")]
    [InlineData("""{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 1 }""")]
    [InlineData("""{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 1, "projects": {} }""")]
    public async Task A_damaged_file_gives_an_empty_list_is_set_aside_and_the_list_starts_again(string content)
    {
        Write(content);

        Assert.Empty(await Store().GetAsync());
        var damaged = Assert.Single(DamagedFiles());
        Assert.Equal(content, await File.ReadAllTextAsync(damaged));

        Assert.True(await Store().AddAsync(Folder("A"), "A"));
        Assert.Equal(new[] { Folder("A") }, Paths(await Store().GetAsync()));
    }

    [Fact]
    public async Task A_change_on_a_damaged_file_sets_it_aside_too()
    {
        Write("garbage");

        Assert.True(await Store().AddAsync(Folder("A"), "A"));

        Assert.Equal("garbage", await File.ReadAllTextAsync(Assert.Single(DamagedFiles())));
        Assert.Equal(new[] { Folder("A") }, Paths(await Store().GetAsync()));
    }

    [Fact]
    public async Task Bad_entries_are_dropped_and_the_others_kept()
    {
        Write(List(
            Entry(Folder("A"), "A"),
            """{ "name": "no path", "lastUsedAt": "2026-10-01T10:00:00+00:00" }""",
            Entry("relative\\path"),
            Entry(Folder("B"), "B", time: "not a time"),
            """ "just a string" """,
            Entry(Folder("C"), "C")));

        var list = await Store().GetAsync();

        Assert.Equal(new[] { Folder("A"), Folder("C") }, Paths(list));
        Assert.Empty(DamagedFiles());
    }

    [Fact]
    public async Task An_entry_without_a_name_is_named_after_its_folder()
    {
        Write(List($$"""{ "folderPath": {{System.Text.Json.JsonSerializer.Serialize(Folder("Trip"))}}, "lastUsedAt": "2026-10-01T10:00:00+00:00" }"""));

        Assert.Equal("Trip", Assert.Single(await Store().GetAsync()).Name);
    }

    [Fact]
    public async Task Duplicates_and_entries_beyond_ten_in_the_file_are_removed_and_the_file_written_clean()
    {
        var entries = new List<string> { Entry(Folder("A"), "newest A"), Entry(Folder("A").ToUpperInvariant() + "\\", "older A") };
        entries.AddRange(Enumerable.Range(1, 11).Select(i => Entry(Folder($"P{i}"), $"P{i}")));
        Write(List(entries.ToArray()));

        var list = await Store().GetAsync();
        Assert.Equal(10, list.Count);
        Assert.Equal("newest A", list[0].Name);
        Assert.Equal(Folder("P9"), list[9].FolderPath);

        Assert.True(await Store().RemoveAsync(Folder("nothing")));   // nothing removed, but the file is rewritten clean
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(await File.ReadAllTextAsync(_file), "folderPath").Count);
    }

    [Fact]
    public async Task A_file_of_a_newer_format_version_is_not_used_and_not_overwritten()
    {
        var newer = Write("""{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 2, "projects": [], "more": 1 }""");

        Assert.Empty(await Store().GetAsync());
        Assert.False(await Store().AddAsync(Folder("A"), "A"));
        Assert.False(await Store().RemoveAsync(Folder("A")));

        Assert.Equal(newer, await File.ReadAllTextAsync(_file));
        Assert.Empty(DamagedFiles());
    }

    [Fact]
    public async Task A_file_that_cant_be_read_gives_an_empty_list_and_is_never_overwritten()
    {
        await Store().AddAsync(Folder("A"), "A");
        var bytes = await File.ReadAllBytesAsync(_file);

        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(await Store().GetAsync());
            Assert.False(await Store().AddAsync(Folder("B"), "B"));
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_file));
        Assert.Empty(DamagedFiles());
        Assert.Equal(new[] { Folder("A") }, Paths(await Store().GetAsync()));
    }

    [Fact]
    public async Task A_failed_write_returns_false_and_leaves_the_previous_file_and_no_temporary_file()
    {
        await Store().AddAsync(Folder("A"), "A");
        var bytes = await File.ReadAllBytesAsync(_file);
        var failing = new ProjectFileStore { BeforeCommit = _ => throw new IOException("disk full") };

        Assert.False(await Store(failing).AddAsync(Folder("B"), "B"));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_file));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task A_configuration_folder_that_cant_be_created_makes_a_change_return_false()
    {
        Directory.CreateDirectory(_temp.Path);
        File.WriteAllText(_temp.Combine("config"), "a file where the folder should be");

        Assert.False(await Store().AddAsync(Folder("A"), "A"));
        Assert.Empty(await Store().GetAsync());
    }

    [Fact]
    public async Task Invalid_folder_paths_are_not_added()
    {
        var store = Store();

        Assert.False(await store.AddAsync("", "x"));
        Assert.False(await store.AddAsync("bad\0path", "x"));

        Assert.Empty(await store.GetAsync());
    }

    // ---- Several instances ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_instances_never_lose_each_others_entries()
    {
        var first = Store();
        var second = Store();

        await first.AddAsync(Folder("A"), "A");
        await second.AddAsync(Folder("B"), "B");
        await first.AddAsync(Folder("C"), "C");
        await second.RemoveAsync(Folder("A"));
        await first.AddAsync(Folder("D"), "D");

        Assert.Equal(new[] { Folder("D"), Folder("C"), Folder("B") }, Paths(await second.GetAsync()));
    }

    [Fact]
    public async Task Parallel_adds_from_several_instances_all_end_up_in_the_list()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => Store(lockTimeout: TimeSpan.FromSeconds(20))).ToList();

        var results = await Task.WhenAll(stores.Select((s, i) => s.AddAsync(Folder($"P{i}"), $"P{i}")));

        Assert.All(results, Assert.True);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => Folder($"P{i}")).OrderBy(p => p),
            Paths(await Store().GetAsync()).OrderBy(p => p));
    }

    [Fact]
    public async Task A_lock_held_by_another_instance_skips_the_change_after_the_timeout_and_keeps_the_file()
    {
        await Store().AddAsync(Folder("A"), "A");
        var bytes = await File.ReadAllBytesAsync(_file);
        var store = Store(lockTimeout: TimeSpan.FromMilliseconds(300));

        using (new FileStream(Path.Combine(Path.GetDirectoryName(_file)!, RecentProjectsStore.LockFileName),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(await store.AddAsync(Folder("B"), "B"));
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { Folder("A") }, Paths(await store.GetAsync()));   // still readable meanwhile
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_file));
        Assert.True(await store.AddAsync(Folder("B"), "B"));
        Assert.Equal(new[] { Folder("B"), Folder("A") }, Paths(await store.GetAsync()));
    }

    // ---- Availability ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_folder_with_a_project_file_is_available()
    {
        _temp.CreateFile(Path.Combine("projects", "A", ProjectFileStore.ProjectFileName), "{}");

        Assert.True(await Store().IsAvailableAsync(Folder("A")));
        Assert.True(await Store().IsAvailableAsync(Folder("A").ToUpperInvariant() + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("folder without project.json")]
    [InlineData("no folder")]
    [InlineData("")]
    [InlineData("bad\0path")]
    [InlineData("<>|")]
    public async Task Anything_else_is_unavailable(string which)
    {
        Directory.CreateDirectory(Folder("Empty"));
        var path = which switch
        {
            "folder without project.json" => Folder("Empty"),
            "no folder" => Folder("Gone"),
            _ => which
        };

        Assert.False(await Store().IsAvailableAsync(path));
    }

    [Fact]
    public async Task An_error_of_the_check_counts_as_unavailable()
    {
        var store = Store(projectFileExists: _ => throw new UnauthorizedAccessException());

        Assert.False(await store.IsAvailableAsync(Folder("A")));
    }

    [Fact]
    public async Task A_hanging_availability_check_holds_up_neither_the_caller_nor_changes_of_the_list()
    {
        using var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var checkThread = -1;
        var store = Store(projectFileExists: _ =>
        {
            checkThread = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(30));
            return true;
        });

        var check = store.IsAvailableAsync(Folder("A"));   // returns at once
        Assert.False(check.IsCompleted);

        Assert.True(await store.AddAsync(Folder("B"), "B"));
        Assert.Equal(new[] { Folder("B") }, Paths(await store.GetAsync()));
        Assert.False(check.IsCompleted);

        release.Set();
        Assert.True(await check);
        Assert.NotEqual(callerThread, checkThread);
    }

    [Fact]
    public async Task Reading_and_changing_the_list_happen_off_the_calling_thread()
    {
        using var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var workThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        var store = Store(onFileWork: () =>
        {
            workThreads.Add(Environment.CurrentManagedThreadId);
            release.Wait(TimeSpan.FromSeconds(30));
        });

        var add = store.AddAsync(Folder("A"), "A");
        var get = store.GetAsync();
        var remove = store.RemoveAsync(Folder("B"));
        Assert.False(add.IsCompleted || get.IsCompleted || remove.IsCompleted);   // the caller was not held up
        // All three started while this thread is still busy here, so none of them can be running on it.
        Assert.True(SpinWait.SpinUntil(() => workThreads.Count == 3, TimeSpan.FromSeconds(10)));

        release.Set();
        Assert.True(await add);
        await get;
        Assert.True(await remove);
        Assert.DoesNotContain(callerThread, workThreads);
    }
}
