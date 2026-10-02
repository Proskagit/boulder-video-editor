namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>Central place for all "where does this file live on disk" decisions.</summary>
public static class AppPaths
{
    public const string AppFolderName = "AiVideoEditor";

    /// <summary>%LOCALAPPDATA%\AiVideoEditor (Windows) or the XDG-equivalent elsewhere.</summary>
    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string LogsFolder => EnsureExists(Path.Combine(AppDataRoot, "logs"));

    public static string ConfigFolder => EnsureExists(ConfigFolderPath);

    /// <summary>The <see cref="ConfigFolder"/> path without creating the folder.</summary>
    public static string ConfigFolderPath => Path.Combine(AppDataRoot, "config");

    /// <summary>The recent-projects list (D026 §6, Step 11.7). Not created here: the folder is created only when the
    /// list is written, so a configuration folder that can't be created never prevents startup.</summary>
    public static string RecentProjectsFile => Path.Combine(ConfigFolderPath, "recent-projects.json");

    /// <summary>Per-project cache. Thumbnails live in its <c>thumbnails</c> subfolder — the one place for them in a
    /// project folder (D024 Step 9.4) —, waveforms in <c>waveforms</c> (Step 9.5); see <c>ThumbnailCacheLocation</c> /
    /// <c>WaveformCacheLocation</c>.</summary>
    public static string ProjectCacheFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "cache"));

    /// <summary>Media caches (thumbnails, waveforms) of projects that were never saved, one folder per project id
    /// (<c>&lt;root&gt;\&lt;projectId&gt;\thumbnails</c>, <c>…\waveforms</c>). Not created here.</summary>
    public static string UnsavedCacheRoot => Path.Combine(AppDataRoot, "cache", "unsaved");

    public static string ProjectMediaFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "media"));

    public static string ProjectFile(string projectFolderPath) =>
        Path.Combine(projectFolderPath, "project.json");

    /// <summary>Autosave recovery files of all projects (one per project id). Application-wide, so
    /// recovery can be offered at startup without knowing which project was open.</summary>
    public static string RecoveryFolder => Path.Combine(AppDataRoot, "recovery");

    private static string EnsureExists(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
