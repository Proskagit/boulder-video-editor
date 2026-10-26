namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>
/// Bound from the "Ffmpeg" section of appsettings.json. Leave <see cref="FfprobePath"/> /
/// <see cref="FfmpegPath"/> null/empty to fall back to PATH-based lookup — nothing
/// assumes FFmpeg is installed in a fixed location.
/// </summary>
public sealed class FfmpegOptions
{
    /// <summary>Path to ffprobe(.exe): absolute, or relative to the application folder
    /// (never to the current directory). Optional — if unset or the file doesn't exist,
    /// <see cref="FfprobeLocator"/> falls back to checking whether "ffprobe" is runnable
    /// via the system PATH.</summary>
    public string? FfprobePath { get; set; }

    /// <summary>Path to ffmpeg(.exe), absolute or relative to the application folder. Optional — same rules as
    /// <see cref="FfprobePath"/>, see <see cref="FfmpegLocator"/>.</summary>
    public string? FfmpegPath { get; set; }
}
