namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// The folder of autosave recovery files, one per project: <c>&lt;root&gt;\&lt;projectId&gt;.json</c>.
/// Kept in one application-wide folder (not inside the project folder) so a recovery file can
/// be found at startup without knowing which project was open, and so a never-saved project
/// can be recovered too.
/// </summary>
/// <remarks>
/// Writes and deletes of recovery files are serialized. Each delete of a project's recovery
/// file bumps that project's generation; a write carries the generation it was snapshotted at
/// and is skipped if a delete happened since — so an autosave snapshot taken before a Save can
/// never recreate the recovery file the Save just made obsolete.
/// </remarks>
public sealed class RecoveryStore
{
    private readonly ProjectFileStore _files;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<Guid, long> _generations = new();

    public RecoveryStore(string rootFolder) : this(rootFolder, new ProjectFileStore())
    {
    }

    internal RecoveryStore(string rootFolder, ProjectFileStore files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
        RootFolder = Path.GetFullPath(rootFolder);
        _files = files;
    }

    public string RootFolder { get; }

    /// <summary>Test hook: runs before a write takes the lock (i.e. between an autosave's
    /// snapshot and its write).</summary>
    internal Func<Task>? BeforeWrite { get; init; }

    public string PathFor(Guid projectId) => Path.Combine(RootFolder, $"{projectId:N}.json");

    public long GenerationOf(Guid projectId)
    {
        lock (_generations)
            return _generations.GetValueOrDefault(projectId);
    }

    /// <summary>Atomically writes the recovery file unless it was deleted after
    /// <paramref name="generation"/> was read. Returns true if written.</summary>
    public async Task<bool> WriteAsync(Guid projectId, long generation, string json, CancellationToken ct = default)
    {
        if (BeforeWrite is { } hook) await hook().ConfigureAwait(false);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GenerationOf(projectId) != generation)
                return false;
            await _files.WriteAtomicAsync(PathFor(projectId), json, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> DeleteAsync(Guid projectId) => DeleteFileAsync(PathFor(projectId), projectId);

    /// <summary>Deletes <paramref name="path"/> (a file in this store). Returns true if a file
    /// was deleted.</summary>
    public async Task<bool> DeleteFileAsync(string path, Guid? projectId = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (projectId is { } id)
                lock (_generations)
                    _generations[id] = _generations.GetValueOrDefault(id) + 1;

            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Recovery files, most recently written first.</summary>
    public IReadOnlyList<string> ListFiles()
    {
        if (!Directory.Exists(RootFolder)) return Array.Empty<string>();
        return Directory.EnumerateFiles(RootFolder, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public Task<string> ReadAsync(string path, CancellationToken ct = default) => _files.ReadAsync(path, ct);

    /// <summary>Renames a damaged recovery file to <c>*.damaged</c> so it is kept (for manual
    /// inspection) but no longer offered. Returns the new path.</summary>
    public async Task<string> SetAsideAsync(string path)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var target = Path.ChangeExtension(path, $".{DateTime.UtcNow:yyyyMMddHHmmss}.damaged");
            File.Move(path, target);
            return target;
        }
        finally
        {
            _lock.Release();
        }
    }
}
