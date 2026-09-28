using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// One kind of media cache of the current project — thumbnails (D024 Step 9.4, PO-1 / PO-2 / PO-6) or waveforms
/// (Step 9.5, PO-W4) —, each in a folder of its own named by the kind.
/// <list type="bullet">
/// <item>Saved project: <c>&lt;project folder&gt;/cache/&lt;kind&gt;</c>.</item>
/// <item>Never saved: <c>&lt;unsaved root&gt;/&lt;project id&gt;/&lt;kind&gt;</c>; the id stays the same for the project's
/// whole life (also across a recovery), so a recovered project finds its cache again, and two unsaved projects never
/// share a folder.</item>
/// <item>Save / Save As to another folder: the files already made are carried over in the background — moved out of
/// the unsaved folder, copied from another project folder (whose cache stays as it is); per asset only the current
/// variant (latest last-write time) is carried, it replaces a file of the same name in the target and the asset's other
/// files of this kind there are removed — best effort: a failure only means they are made again, never loses the
/// current file and never affects the save.</item>
/// <item>At startup this kind's folders of unsaved projects without a recovery file are removed (nothing can open
/// those projects again), and a project's unsaved folder once it holds nothing else.</item>
/// </list>
/// The folder follows the project's state at every read; events come from <see cref="IProjectService"/> on the UI
/// thread. Each kind only ever touches its own folder and files.
/// </summary>
public abstract class MediaCacheLocation : IMediaCacheLocation
{
    public const string CacheFolderName = "cache";

    private readonly IProjectService _projects;
    private readonly RecoveryStore _recovery;
    private readonly string _unsavedRoot;
    private readonly string _kindFolderName;
    private readonly string _pattern;
    private readonly string _kind;
    private readonly ILogger _logger;
    private string _folder;

    /// <param name="kindFolderName">The kind's folder name (<c>thumbnails</c>, <c>waveforms</c>).</param>
    /// <param name="filePattern">The kind's cache files (<c>*.thumb</c>, <c>*.peaks</c>).</param>
    /// <param name="kind">The kind in log messages (<c>thumbnail</c>, <c>waveform</c>).</param>
    protected MediaCacheLocation(IProjectService projects, RecoveryStore recovery, string unsavedRoot,
        string kindFolderName, string filePattern, string kind, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unsavedRoot);
        _projects = projects;
        _recovery = recovery;
        _unsavedRoot = Path.GetFullPath(unsavedRoot);
        _kindFolderName = kindFolderName;
        _pattern = filePattern;
        _kind = kind;
        _logger = logger;
        _folder = FolderFor(projects.Current);
        projects.ProjectChanged += (_, _) => OnProjectChanged();
        projects.ProjectSaved += (_, _) => OnProjectSaved();
    }

    public string CurrentFolder => FolderFor(_projects.Current);

    public event EventHandler? Changed;

    /// <summary>The last carry-over after a save (tests).</summary>
    internal Task LastTransfer { get; private set; } = Task.CompletedTask;

    protected static string SavedFolder(string projectFolderPath, string kindFolderName) =>
        Path.Combine(projectFolderPath, CacheFolderName, kindFolderName);

    public string UnsavedFolder(Guid projectId) => Path.Combine(_unsavedRoot, projectId.ToString("N"), _kindFolderName);

    public string FolderFor(Core.Entities.Project project) =>
        project.ProjectFolderPath is { } folder ? SavedFolder(folder, _kindFolderName) : UnsavedFolder(project.Id);

    private void OnProjectChanged()
    {
        _folder = CurrentFolder;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectSaved()
    {
        var previous = _folder;
        var current = CurrentFolder;
        if (string.Equals(Path.GetFullPath(previous), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            return; // saved where it already was

        _folder = current;
        var fromUnsaved = IsUnsaved(previous);
        LastTransfer = Task.Run(() => CarryOver(previous, current, move: fromUnsaved));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsUnsaved(string folder) =>
        Path.GetFullPath(folder).StartsWith(_unsavedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a file's last-write time (tests replace it to simulate a failure).</summary>
    internal Func<string, DateTime> ReadLastWriteUtc { get; init; } = File.GetLastWriteTimeUtc;

    /// <summary>
    /// Moves (from the unsaved folder) or copies (from another project's folder) the cache files, one per asset: the
    /// source's files are grouped by asset (file name prefix up to the first '-'), and of each group only the current
    /// variant is carried — the latest last-write time, as the services' offline lookup picks it; on a tie the
    /// ordinally last name. It replaces a file of the same name in the target. Only after that copy succeeded are the
    /// asset's other variants in the target removed and, for a move, all of the asset's source variants. If a variant's
    /// time can't be read or the copy fails, nothing of that asset is removed, neither in the source nor in the target.
    /// Other assets are never touched. Never throws.
    /// </summary>
    private void CarryOver(string from, string to, bool move)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            var files = Directory.GetFiles(from, _pattern);
            if (files.Length == 0) return;
            Directory.CreateDirectory(to);
            var carried = 0;
            foreach (var variants in files.GroupBy(f => AssetKey(Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase))
            {
                if (CurrentVariant(variants.ToList()) is not { } current)
                    continue; // logged; the group stays as it is

                var name = Path.GetFileName(current);
                try
                {
                    File.Copy(current, Path.Combine(to, name), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "The {Kind} '{File}' was not carried over to '{Folder}'; it stays where it was.", _kind, current, to);
                    continue;
                }

                carried++;
                RemoveOtherVariants(to, name);
                if (move)
                {
                    foreach (var variant in variants)
                        TryDelete(variant);
                }
            }
            if (move) TryDeleteUnsavedFolder(from);
            _logger.LogInformation("{Count} {Kind}(s) {Verb} to the project's cache '{Folder}'.", carried, _kind, move ? "moved" : "copied", to);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {Kind}s in '{From}' could not be carried over to '{To}'; they will be made again.", _kind, from, to);
        }
    }

    /// <summary>The asset a cache file belongs to: its name up to the first '-' (the asset id); a name without one is a
    /// group of its own.</summary>
    private static string AssetKey(string fileName)
    {
        var dash = fileName.IndexOf('-');
        return dash > 0 ? fileName[..dash] : fileName;
    }

    /// <summary>The variant with the latest last-write time (on a tie the ordinally last name), or null — logged — when
    /// the time of any variant can't be read: then no variant is chosen at all.</summary>
    private string? CurrentVariant(IReadOnlyList<string> variants)
    {
        var timed = new List<(string File, DateTime Time)>(variants.Count);
        foreach (var variant in variants)
        {
            try
            {
                var time = ReadLastWriteUtc(variant);
                if (time == DateTime.FromFileTimeUtc(0)) // what .NET returns for a file that isn't there
                    throw new FileNotFoundException($"The {_kind} file is gone.", variant);
                timed.Add((variant, time));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The time of {Kind} '{File}' could not be read; the files of its asset stay where they are.", _kind, variant);
                return null;
            }
        }
        return timed
            .OrderByDescending(t => t.Time)
            .ThenByDescending(t => Path.GetFileName(t.File), StringComparer.Ordinal)
            .First().File;
    }

    /// <summary>Removes the target's other files of this kind of the asset <paramref name="kept"/> belongs to (file names
    /// start with the asset id and a '-'); a file that can't be removed is left and logged.</summary>
    private void RemoveOtherVariants(string folder, string kept)
    {
        var dash = kept.IndexOf('-');
        if (dash <= 0) return; // not named by an asset id: nothing to match
        try
        {
            foreach (var other in Directory.GetFiles(folder, kept[..(dash + 1)] + _pattern))
            {
                if (!string.Equals(Path.GetFileName(other), kept, StringComparison.OrdinalIgnoreCase))
                    TryDelete(other);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Older {Kind}s of '{File}' in '{Folder}' were not removed.", _kind, kept, folder);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "'{File}' could not be removed.", path);
        }
    }

    /// <summary>After a move out of <c>…/unsaved/&lt;id&gt;/&lt;kind&gt;</c>: that folder once empty, and the
    /// <c>&lt;id&gt;</c> folder once nothing else is left in it (another kind may still be there).</summary>
    private void TryDeleteUnsavedFolder(string kindFolder)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(kindFolder).Any()) return;
            Directory.Delete(kindFolder);
            var projectFolder = Path.GetDirectoryName(kindFolder)!;
            if (!Directory.EnumerateFileSystemEntries(projectFolder).Any()) Directory.Delete(projectFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The empty unsaved {Kind} folder '{Folder}' was left in place.", _kind, kindFolder);
        }
    }

    public Task CleanUpUnsavedAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(_unsavedRoot)) return;
            var current = _projects.Current.Id;
            foreach (var folder in Directory.GetDirectories(_unsavedRoot))
            {
                // Only folders named by a project id; a recovery file means the project can still come back.
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id) || id == current || File.Exists(_recovery.PathFor(id)))
                    continue;
                try
                {
                    var kindFolder = Path.Combine(folder, _kindFolderName);
                    if (Directory.Exists(kindFolder))
                        Directory.Delete(kindFolder, recursive: true);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                    _logger.LogDebug("Removed the {Kind} cache of unsaved project {ProjectId} (no recovery file).", _kind, id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "The {Kind} cache in '{Folder}' could not be removed; trying again at the next start.", _kind, folder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleaning up the {Kind} caches of unsaved projects failed.", _kind);
        }
    });
}
