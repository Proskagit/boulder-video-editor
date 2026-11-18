namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>Central place for all "where does this file live on disk" decisions.</summary>
public static class AppPaths
{
    /// <summary>D068 §1: the folder carries the product's name, Boulder Video Editor.</summary>
    public const string AppFolderName = "BoulderVideoEditor";

    /// <summary>The folder of 1.0 and earlier, moved into <see cref="AppDataRoot"/> at startup (<see cref="AppDataMigration"/>).</summary>
    public const string LegacyAppFolderName = "AiVideoEditor";

    /// <summary>%LOCALAPPDATA%\BoulderVideoEditor (Windows) or the XDG-equivalent elsewhere.</summary>
    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>%LOCALAPPDATA%\AiVideoEditor — read only by <see cref="AppDataMigration"/>.</summary>
    public static string LegacyAppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppFolderName);

    public static string LogsFolder => EnsureExists(Path.Combine(AppDataRoot, "logs"));

    public static string ConfigFolder => EnsureExists(ConfigFolderPath);

    /// <summary>The <see cref="ConfigFolder"/> path without creating the folder.</summary>
    public static string ConfigFolderPath => Path.Combine(AppDataRoot, "config");

    /// <summary>The recent-projects list (D026 §6, Step 11.7). Not created here: the folder is created only when the
    /// list is written, so a configuration folder that can't be created never prevents startup.</summary>
    public static string RecentProjectsFile => Path.Combine(ConfigFolderPath, "recent-projects.json");

    /// <summary>The global start calibration (D051 §6.1, Step 37.2) — the second per-user file. Not created here: it is
    /// created on its first write.</summary>
    public static string FitCalibrationFile => Path.Combine(ConfigFolderPath, "fit-calibration.json");

    /// <summary>The application's global settings (D057 §3.8). Created on its first write.</summary>
    public static string UserSettingsFile => Path.Combine(ConfigFolderPath, "settings.json");

    /// <summary>The user's working layout — the window, the panels, the Inspector's sections (D057 §3.1, §3.4). Created on
    /// its first write.</summary>
    public static string UiStateFile => Path.Combine(ConfigFolderPath, "ui-state.json");

    /// <summary>Per-project cache. Thumbnails live in its <c>thumbnails</c> subfolder — the one place for them in a
    /// project folder (D024 Step 9.4) —, waveforms in <c>waveforms</c> (Step 9.5); see <c>ThumbnailCacheLocation</c> /
    /// <c>WaveformCacheLocation</c>.</summary>
    public static string ProjectCacheFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "cache"));

    /// <summary>Media caches (thumbnails, waveforms) of projects that were never saved, one folder per project id
    /// (<c>&lt;root&gt;\&lt;projectId&gt;\thumbnails</c>, <c>…\waveforms</c>). Not created here.</summary>
    public static string UnsavedCacheRoot => Path.Combine(AppDataRoot, "cache", "unsaved");

    /// <summary>The HDR → SDR LUTs (D033 §3), shared by every project. Not created here (the store creates it on first
    /// use).</summary>
    public static string ColorLutFolder => Path.Combine(AppDataRoot, "cache", "color-luts");

    /// <summary>The analysed camera-motion tracks of the hold tracking (R2 → E, D4), shared by every project, keyed by the
    /// video's content. Not created here (the store creates it on its first write).</summary>
    public static string CameraMotionCacheFolder => Path.Combine(AppDataRoot, "cache", "camera-motion");

    /// <summary>D061: the people analyses (tracks, masks) kept between sessions, keyed by the video's content.</summary>
    public static string PeopleCacheFolder => Path.Combine(AppDataRoot, "cache", "people");

    /// <summary>D064 §5: the cleaned sounds and the speech analyses, keyed by the source's content and the settings.</summary>
    public static string AudioCacheFolder => Path.Combine(AppDataRoot, "cache", "audio");

    public static string ProjectMediaFolder(string projectFolderPath) =>
        EnsureExists(Path.Combine(projectFolderPath, "media"));

    public static string ProjectFile(string projectFolderPath) =>
        Path.Combine(projectFolderPath, "project.json");

    /// <summary>Autosave recovery files of all projects (one per project id). Application-wide, so
    /// recovery can be offered at startup without knowing which project was open.</summary>
    public static string RecoveryFolder => Path.Combine(AppDataRoot, "recovery");

    /// <summary>The local AI models (D034 §2): one subfolder per model, placed there by the user (docs/AI_MODEL.md). The
    /// application never downloads, writes or replaces anything here.</summary>
    public static string ModelsFolder => Path.Combine(AppDataRoot, "models");

    private static string EnsureExists(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
