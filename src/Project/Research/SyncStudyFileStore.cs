using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Persistence;

namespace AiVideoEditor.Project.Research;

/// <summary>
/// Reads and writes study files (Step 35.3b1, D050 B5). A study is never written into a project: a path inside the project
/// folder or equal to its <c>project.json</c> is refused; the name must end with <c>.syncstudy.json</c>. The default is
/// <c>&lt;FIT name&gt;.syncstudy.json</c> next to the project folder — the user chooses the actual path. Writing replaces
/// the file atomically (<see cref="ProjectFileStore.WriteAtomicAsync"/>): on a failure the previous file is unchanged.
/// </summary>
public sealed class SyncStudyFileStore
{
    public const string Extension = ".syncstudy.json";

    private readonly ProjectFileStore _files = new();

    /// <summary>The default path: next to the project folder, named after the FIT.</summary>
    public static string DefaultPath(string projectFolder, string fitPath)
    {
        var folder = Path.GetFullPath(projectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(folder) ?? folder;
        return Path.Combine(parent, Path.GetFileNameWithoutExtension(fitPath) + Extension);
    }

    /// <summary>Why <paramref name="path"/> may not hold a study, or null when it may.</summary>
    public static string? PathProblem(string path, string? projectFolder)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return $"a study file must end with {Extension}";
        if (projectFolder is null) return null;
        var folder = Path.GetFullPath(projectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, Path.GetFullPath(ProjectFileStore.ProjectFilePath(folder)), StringComparison.OrdinalIgnoreCase))
            return "a study file cannot be the project file";
        if (full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "a study file cannot be inside the project folder";
        return null;
    }

    public async Task<SyncStudy> LoadAsync(string path, CancellationToken ct = default)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SyncStudyFileException(SyncStudyFileError.Unreadable, ex.Message, ex);
        }
        return SyncStudySerializer.Deserialize(text);
    }

    public async Task SaveAsync(string path, SyncStudy study, string? projectFolder, CancellationToken ct = default)
    {
        if (PathProblem(path, projectFolder) is { } problem) throw new SyncStudyFileException(SyncStudyFileError.InvalidPath, problem);
        var text = SyncStudySerializer.Serialize(study);
        try
        {
            await _files.WriteAtomicAsync(path, text, ct).ConfigureAwait(false);
        }
        catch (ProjectFileException ex)
        {
            throw new SyncStudyFileException(SyncStudyFileError.Unreadable, ex.Message, ex);
        }
    }
}
