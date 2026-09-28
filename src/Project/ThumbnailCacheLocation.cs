using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// <see cref="IThumbnailCacheLocation"/> over the current project (D024 Step 9.4, PO-1 / PO-2 / PO-6).
/// <list type="bullet">
/// <item>Saved project: <c>&lt;project folder&gt;/cache/thumbnails</c> — the one path for thumbnails in a project folder.</item>
/// <item>Never saved: <c>&lt;unsaved root&gt;/&lt;project id&gt;/thumbnails</c>; the id stays the same for the project's whole
/// life (also across a recovery), so a recovered project finds its thumbnails again, and two unsaved projects never
/// share a folder.</item>
/// <item>Save / Save As to another folder: the thumbnails already made are carried over in the background — moved out
/// of the unsaved folder, copied from another project folder (whose cache stays as it is); per asset only the current
/// variant (latest last-write time) is carried, it replaces a file of the same name in the target and the asset's other
/// thumbnails there are removed — best effort: a failure only means they are made again, never loses the current
/// thumbnail and never affects the save.</item>
/// <item>At startup the unsaved folders without a recovery file are removed: nothing can open those projects again.</item>
/// </list>
/// The folder follows the project's state at every read; events come from <see cref="IProjectService"/> on the UI
/// thread.
/// </summary>
public sealed class ThumbnailCacheLocation : IThumbnailCacheLocation
{
    public const string CacheFolderName = "cache";
    public const string ThumbnailsFolderName = "thumbnails";
    private const string ThumbnailPattern = "*.thumb";

    private readonly IProjectService _projects;
    private readonly RecoveryStore _recovery;
    private readonly string _unsavedRoot;
    private readonly ILogger<ThumbnailCacheLocation> _logger;
    private string _folder;

    public ThumbnailCacheLocation(IProjectService projects, RecoveryStore recovery, string unsavedRoot, ILogger<ThumbnailCacheLocation> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unsavedRoot);
        _projects = projects;
        _recovery = recovery;
        _unsavedRoot = Path.GetFullPath(unsavedRoot);
        _logger = logger;
        _folder = FolderFor(projects.Current);
        projects.ProjectChanged += (_, _) => OnProjectChanged();
        projects.ProjectSaved += (_, _) => OnProjectSaved();
    }

    public string CurrentFolder => FolderFor(_projects.Current);

    public event EventHandler? Changed;

    /// <summary>The last carry-over of thumbnails after a save (tests).</summary>
    internal Task LastTransfer { get; private set; } = Task.CompletedTask;

    /// <summary><c>&lt;project folder&gt;/cache/thumbnails</c>.</summary>
    public static string SavedFolder(string projectFolderPath) =>
        Path.Combine(projectFolderPath, CacheFolderName, ThumbnailsFolderName);

    public string UnsavedFolder(Guid projectId) => Path.Combine(_unsavedRoot, projectId.ToString("N"), ThumbnailsFolderName);

    public string FolderFor(Core.Entities.Project project) =>
        project.ProjectFolderPath is { } folder ? SavedFolder(folder) : UnsavedFolder(project.Id);

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
    /// Moves (from the unsaved folder) or copies (from another project's folder) the thumbnails, one per asset: the
    /// source's files are grouped by asset (file name prefix up to the first '-'), and of each group only the current
    /// variant is carried — the latest last-write time, as the offline lookup of the thumbnail service picks it; on a
    /// tie the ordinally last name. It replaces a file of the same name in the target. Only after that copy succeeded
    /// are the asset's other variants in the target removed and, for a move, all of the asset's source variants. If a
    /// variant's time can't be read or the copy fails, nothing of that asset is removed, neither in the source nor in the
    /// target. Other assets are never touched. Never throws.
    /// </summary>
    private void CarryOver(string from, string to, bool move)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            var files = Directory.GetFiles(from, ThumbnailPattern);
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
                    _logger.LogDebug(ex, "Thumbnail '{File}' was not carried over to '{Folder}'; it stays where it was.", current, to);
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
            if (move) TryDeleteUnsavedProjectFolder(from);
            _logger.LogInformation("{Count} thumbnail(s) {Verb} to the project's cache '{Folder}'.", carried, move ? "moved" : "copied", to);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The thumbnails in '{From}' could not be carried over to '{To}'; they will be made again.", from, to);
        }
    }

    /// <summary>The asset a thumbnail file belongs to: its name up to the first '-' (the asset id); a name without one
    /// is a group of its own.</summary>
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
                    throw new FileNotFoundException("The thumbnail file is gone.", variant);
                timed.Add((variant, time));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The time of thumbnail '{File}' could not be read; the thumbnails of its asset stay where they are.", variant);
                return null;
            }
        }
        return timed
            .OrderByDescending(t => t.Time)
            .ThenByDescending(t => Path.GetFileName(t.File), StringComparer.Ordinal)
            .First().File;
    }

    /// <summary>Removes the target's other thumbnails of the asset <paramref name="kept"/> belongs to (file names start
    /// with the asset id and a '-'); a file that can't be removed is left and logged.</summary>
    private void RemoveOtherVariants(string folder, string kept)
    {
        var dash = kept.IndexOf('-');
        if (dash <= 0) return; // not named by an asset id: nothing to match
        try
        {
            foreach (var other in Directory.GetFiles(folder, kept[..(dash + 1)] + ThumbnailPattern))
            {
                if (!string.Equals(Path.GetFileName(other), kept, StringComparison.OrdinalIgnoreCase))
                    TryDelete(other);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Older thumbnails of '{File}' in '{Folder}' were not removed.", kept, folder);
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

    /// <summary>After a move out of <c>…/unsaved/&lt;id&gt;/thumbnails</c>: the now empty <c>&lt;id&gt;</c> folder.</summary>
    private void TryDeleteUnsavedProjectFolder(string thumbnailsFolder)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(thumbnailsFolder).Any()) return;
            Directory.Delete(thumbnailsFolder);
            var projectFolder = Path.GetDirectoryName(thumbnailsFolder)!;
            if (!Directory.EnumerateFileSystemEntries(projectFolder).Any()) Directory.Delete(projectFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The empty unsaved thumbnail folder '{Folder}' was left in place.", thumbnailsFolder);
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
                // Only folders this class makes (a project id); a recovery file means the project can still come back.
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id) || id == current || File.Exists(_recovery.PathFor(id)))
                    continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _logger.LogDebug("Removed the thumbnail cache of unsaved project {ProjectId} (no recovery file).", id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "The thumbnail cache '{Folder}' could not be removed; trying again at the next start.", folder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleaning up the thumbnail caches of unsaved projects failed.");
        }
    });
}
