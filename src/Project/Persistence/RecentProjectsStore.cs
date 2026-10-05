using System.Text.Json;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// The recent-projects list in one JSON file of the application's configuration (D026 §6, Step 11.7):
/// <c>{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 1, "projects": [ { "folderPath", "name",
/// "lastUsedAt" } ] }</c>, most recent first.
/// </summary>
/// <remarks>
/// <para>A change reads the file again and writes it atomically (<see cref="ProjectFileStore.WriteAtomicAsync"/>) under
/// a lock shared by every running instance — <c>recent-projects.lock</c> next to it, opened with
/// <see cref="FileShare.None"/> — and an in-process semaphore, so instances never lose each other's entries. A lock not
/// obtained within <see cref="LockTimeout"/> skips the change (logged).</para>
/// <para>A file that isn't a list of this format is set aside as <c>*.damaged</c> (like a damaged recovery file) and
/// the list starts empty; single bad entries are dropped, duplicates and entries beyond the limit removed. A file of a
/// newer format version is neither used nor overwritten. A file that can't be read is never overwritten. The folder is
/// created only when the list is written.</para>
/// </remarks>
public sealed class RecentProjectsStore : IRecentProjectsStore
{
    public const string FormatId = "AiVideoEditor.RecentProjects";
    public const int CurrentFormatVersion = 1;
    public const string LockFileName = "recent-projects.lock";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ProjectFileStore _files;
    private readonly ILogger<RecentProjectsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecentProjectsStore(string filePath, ILogger<RecentProjectsStore> logger)
        : this(filePath, logger, new ProjectFileStore())
    {
    }

    internal RecentProjectsStore(string filePath, ILogger<RecentProjectsStore> logger, ProjectFileStore files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        LockFilePath = Path.Combine(Path.GetDirectoryName(FilePath)!, LockFileName);
        _logger = logger;
        _files = files;
    }

    public string FilePath { get; }

    public string LockFilePath { get; }

    /// <summary>How long a change waits for the lock of another instance (or another change of this one).</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: the time stored with an added entry.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Test hook: the availability check of a project file (the real one is <see cref="File.Exists"/>).</summary>
    internal Func<string, bool> ProjectFileExists { get; init; } = File.Exists;

    /// <summary>Test hook: runs on the thread that does the file work of a read or a change, before it starts.</summary>
    internal Action? OnFileWork { get; init; }

    public Task<IReadOnlyList<RecentProject>> GetAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            OnFileWork?.Invoke();
            if (!File.Exists(FilePath)) return (IReadOnlyList<RecentProject>)Array.Empty<RecentProject>();

            // Under the lock, so a damaged file is set aside only if no other instance replaced it meanwhile. Without
            // the lock the list is still read (a replace is atomic), only nothing is set aside.
            using var held = await TryLockAsync(ct).ConfigureAwait(false);
            var read = await ReadAsync(setAsideDamaged: held is not null, ct).ConfigureAwait(false);
            return read.Entries;
        }, ct);

    public Task<bool> AddAsync(string projectFolderPath, string projectName, CancellationToken ct = default)
    {
        return ChangeAsync("add", projectFolderPath, entries =>
        {
            var folder = NormalizeOrNull(projectFolderPath);
            if (folder is null) return null;
            var name = string.IsNullOrWhiteSpace(projectName) ? FolderName(folder) : projectName;
            var entry = new RecentProject(folder, name, Clock());
            return new[] { entry }.Concat(entries.Where(e => !SameFolder(e.FolderPath, folder))).ToList();
        }, ct);
    }

    public Task<bool> RemoveAsync(string projectFolderPath, CancellationToken ct = default)
    {
        return ChangeAsync("remove", projectFolderPath, entries =>
        {
            var folder = NormalizeOrNull(projectFolderPath);
            if (folder is null) return null;
            return entries.Any(e => SameFolder(e.FolderPath, folder))
                ? entries.Where(e => !SameFolder(e.FolderPath, folder)).ToList()
                : entries; // nothing to remove: no write
        }, ct);
    }

    public Task<bool> IsAvailableAsync(string projectFolderPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                var folder = NormalizeOrNull(projectFolderPath);
                return folder is not null && ProjectFileExists(ProjectFileStore.ProjectFilePath(folder));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }, ct);

    /// <summary>The full path of a project folder without a trailing separator — the form entries are stored and
    /// compared in (ignoring case). Null for a path that isn't a valid absolute one.</summary>
    public static string? NormalizeOrNull(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            return Path.IsPathFullyQualified(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SameFolder(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FolderName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    // ---- Changing the list ----------------------------------------------------------------------------------------

    /// <summary>Reads the list again under the lock, applies <paramref name="change"/> (null = invalid input; the same
    /// list instance = nothing to write) and writes the result.</summary>
    private Task<bool> ChangeAsync(string what, string folderPath,
        Func<IReadOnlyList<RecentProject>, IReadOnlyList<RecentProject>?> change, CancellationToken ct) =>
        Task.Run(async () =>
        {
            OnFileWork?.Invoke();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var held = await TryLockAsync(ct).ConfigureAwait(false);
                if (held is null)
                {
                    _logger.LogWarning("Recent projects: couldn't {What} \"{Folder}\" — the list is in use by another instance.",
                        what, folderPath);
                    return false;
                }

                var read = await ReadAsync(setAsideDamaged: true, ct).ConfigureAwait(false);
                if (!read.Writable)
                    return false; // logged by ReadAsync

                var changed = change(read.Entries);
                if (changed is null)
                {
                    _logger.LogWarning("Recent projects: \"{Folder}\" is not a valid project folder path.", folderPath);
                    return false;
                }
                if (ReferenceEquals(changed, read.Entries) && !read.NeedsRewrite)
                    return true;

                var list = Normalize(changed);
                await _files.WriteAtomicAsync(FilePath, Serialize(list), ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFileException
                                           or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Recent projects: couldn't {What} \"{Folder}\"; the list was left as it was.",
                    what, folderPath);
                return false;
            }
        }, ct);

    private static IReadOnlyList<RecentProject> Normalize(IEnumerable<RecentProject> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => seen.Add(e.FolderPath)).Take(IRecentProjectsStore.MaxEntries).ToList();
    }

    private static string Serialize(IReadOnlyList<RecentProject> entries) => JsonSerializer.Serialize(new ListDto
    {
        Format = FormatId,
        FormatVersion = CurrentFormatVersion,
        Projects = entries.Select(e => new EntryDto { FolderPath = e.FolderPath, Name = e.Name, LastUsedAt = e.LastUsedAt })
            .ToList()
    }, Options);

    private sealed class ListDto
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public List<EntryDto> Projects { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string FolderPath { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset LastUsedAt { get; set; }
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    /// <param name="Entries">The usable list (empty if there is none).</param>
    /// <param name="Writable">False when the file must not be overwritten: it couldn't be read, or it is of a newer
    /// format version.</param>
    /// <param name="NeedsRewrite">The file held entries that were dropped (bad, duplicate, beyond the limit).</param>
    private sealed record ReadResult(IReadOnlyList<RecentProject> Entries, bool Writable, bool NeedsRewrite);

    private async Task<ReadResult> ReadAsync(bool setAsideDamaged, CancellationToken ct)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadResult(Array.Empty<RecentProject>(), Writable: true, NeedsRewrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recent projects: {File} couldn't be read; it is left as it is.", FilePath);
            return new ReadResult(Array.Empty<RecentProject>(), Writable: false, NeedsRewrite: false);
        }

        var parsed = Parse(json);
        switch (parsed.Problem)
        {
            case null:
                return new ReadResult(parsed.Entries, Writable: true, NeedsRewrite: parsed.Dropped);
            case NewerVersion:
                _logger.LogWarning("Recent projects: {File} is of a newer format version; it is not used or overwritten.",
                    FilePath);
                return new ReadResult(Array.Empty<RecentProject>(), Writable: false, NeedsRewrite: false);
            default:
                _logger.LogWarning("Recent projects: {File} is damaged ({Problem}); the list starts empty.", FilePath,
                    parsed.Problem);
                if (setAsideDamaged)
                    SetAside();
                return new ReadResult(Array.Empty<RecentProject>(), Writable: true, NeedsRewrite: true);
        }
    }

    private const string NewerVersion = "a newer format version";

    private sealed record Parsed(IReadOnlyList<RecentProject> Entries, bool Dropped, string? Problem);

    private static Parsed Parse(string json)
    {
        static Parsed Damaged(string problem) => new(Array.Empty<RecentProject>(), false, problem);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Damaged("not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Damaged("not an object");
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() != FormatId)
                return Damaged("not a recent-projects list");
            if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var v) || v < 1)
                return Damaged("no valid format version");
            if (v > CurrentFormatVersion)
                return new Parsed(Array.Empty<RecentProject>(), false, NewerVersion);
            if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
                return Damaged("no list of projects");

            var entries = new List<RecentProject>();
            var count = 0;
            foreach (var item in projects.EnumerateArray())
            {
                count++;
                if (EntryOrNull(item) is { } entry)
                    entries.Add(entry);
            }

            var list = Normalize(entries);
            return new Parsed(list, list.Count != count, null);
        }
    }

    private static RecentProject? EntryOrNull(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("folderPath", out var path) || path.ValueKind != JsonValueKind.String) return null;
        var raw = path.GetString();
        if (raw is null || !Path.IsPathFullyQualified(raw) || NormalizeOrNull(raw) is not { } folder) return null;
        if (!item.TryGetProperty("lastUsedAt", out var time) || time.ValueKind != JsonValueKind.String ||
            !time.TryGetDateTimeOffset(out var lastUsedAt)) return null;
        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!
            : FolderName(folder);
        return new RecentProject(folder, name, lastUsedAt);
    }

    private void SetAside()
    {
        try
        {
            var target = Path.ChangeExtension(FilePath, $".{DateTime.UtcNow:yyyyMMddHHmmssfff}.damaged");
            File.Move(FilePath, target);
            _logger.LogWarning("Recent projects: the damaged list was kept as {File}.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recent projects: the damaged list couldn't be set aside.");
        }
    }

    // ---- Lock -------------------------------------------------------------------------------------------------------

    /// <summary>The in-process semaphore and then the lock file, both within <see cref="LockTimeout"/>; null if not
    /// obtained (the lock file's folder must exist).</summary>
    private async Task<IDisposable?> TryLockAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        if (!await _gate.WaitAsync(LockTimeout, ct).ConfigureAwait(false))
            return null;

        try
        {
            while (true)
            {
                try
                {
                    var stream = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Held(stream, _gate);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Held by another instance (a sharing violation): try again shortly.
                }

                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _gate.Release();
            return null;
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class Held(FileStream stream, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            gate.Release();
        }
    }
}
