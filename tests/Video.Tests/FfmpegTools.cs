using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>ffmpeg/ffprobe as found by the application's own locators (PATH here).</summary>
internal static class FfmpegTools
{
    private static readonly IOptions<FfmpegOptions> Options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());

    public static readonly FfmpegLocator FfmpegLocator = new(Options, NullLogger<FfmpegLocator>.Instance);

    public static readonly string? Ffmpeg = FfmpegLocator.GetFfmpegPathAsync().GetAwaiter().GetResult();

    public static readonly string? Ffprobe =
        new FfprobeLocator(Options, NullLogger<FfprobeLocator>.Instance).GetFfprobePathAsync().GetAwaiter().GetResult();

    public static string? SkipReason => Ffmpeg is null || Ffprobe is null ? "ffmpeg/ffprobe not found on PATH." : null;
}

/// <summary>A fact that is skipped (not failed) when ffmpeg/ffprobe are unavailable.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute() => Skip = FfmpegTools.SkipReason;
}

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute() => Skip = FfmpegTools.SkipReason;
}
