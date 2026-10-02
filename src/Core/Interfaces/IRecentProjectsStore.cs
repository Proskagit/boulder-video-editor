namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The per-user list of recently used project folders (D026 §6): at most <see cref="MaxEntries"/>, most recent first, no
/// two entries for the same folder (full path, compared ignoring case and a trailing separator). Stored outside every
/// project. Whether a project is there now is runtime state, asked with <see cref="IsAvailableAsync"/> and never stored.
/// </summary>
/// <remarks>
/// Every method does its file work off the calling thread. A problem with the list file never throws (it is logged):
/// reading gives an empty list, a change that couldn't be written returns false and leaves the file as it was.
/// </remarks>
public interface IRecentProjectsStore
{
    const int MaxEntries = 10;

    /// <summary>The list, most recent first. Read from the file every time — another running instance may have
    /// changed it.</summary>
    Task<IReadOnlyList<RecentProject>> GetAsync(CancellationToken ct = default);

    /// <summary>Puts the project first (an entry for the same folder is replaced: its name, time and path spelling are
    /// updated) and drops the oldest entries beyond <see cref="MaxEntries"/>. False if the list wasn't written.</summary>
    Task<bool> AddAsync(string projectFolderPath, string projectName, CancellationToken ct = default);

    /// <summary>Removes the entry for the folder, if any. False if the list wasn't written.</summary>
    Task<bool> RemoveAsync(string projectFolderPath, CancellationToken ct = default);

    /// <summary>Whether the folder holds a project.json now. Any error counts as unavailable; there is no time limit
    /// (a disconnected network path may take long), and the check never waits for, or holds up, a change of the
    /// list.</summary>
    Task<bool> IsAvailableAsync(string projectFolderPath, CancellationToken ct = default);
}

/// <summary>An entry of the recent-projects list: the project folder (full path), the project's name when it was last
/// used, and when that was.</summary>
public sealed record RecentProject(string FolderPath, string Name, DateTimeOffset LastUsedAt);
