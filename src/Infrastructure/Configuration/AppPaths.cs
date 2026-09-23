namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>Central place for all "where does this file live on disk" decisions.</summary>
public static class AppPaths
{
    public const string AppFolderName = "AiVideoEditor";

    /// <summary>%LOCALAPPDATA%\AiVideoEditor (Windows) or the XDG-equivalent elsewhere.</summary>
    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string LogsFolder => EnsureExists(Path.Combine(AppDataRoot, "logs"));

    public static string ConfigFolder => EnsureExists(Path.Combine(AppDataRoot, "config"));

    /// <summary>Per-project cache: thumbnails, waveform, decoded-frame cache.</summary>
    public static string ProjectCacheFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "cache"));

    public static string ProjectThumbnailsFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "thumbnails"));

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
