namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>
/// Bound from the "Ffmpeg" section of appsettings.json. Leave <see cref="FfprobePath"/>
/// null/empty to fall back to PATH-based lookup — nothing assumes FFmpeg is
/// installed in a fixed location.
/// </summary>
public sealed class FfmpegOptions
{
    /// <summary>Absolute path to ffprobe(.exe). Optional — if unset or the file
    /// doesn't exist, <see cref="FfprobeLocator"/> falls back to checking whether
    /// "ffprobe" is runnable via the system PATH.</summary>
    public string? FfprobePath { get; set; }
}
