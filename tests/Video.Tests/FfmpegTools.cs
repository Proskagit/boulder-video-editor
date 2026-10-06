using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// ffmpeg/ffprobe as found by the application's own locators (PATH here), found once per test run and shared: a new
/// locator per analysis would repeat the PATH probe every time.
/// </summary>
/// <remarks>
/// Limits (Phase 14 Step 14.4, D029). The app counts a tool whose <c>-version</c> takes longer than 5 s as not found,
/// and gives an ffprobe run 20 s and a decoder 20 s for its first frame. Those limits are the subject of their own
/// tests (<c>ExecutableLocatorTests</c>, <c>FfmpegDiagnosticsTests</c>, <c>FfprobeTerminationTests</c>,
/// <c>AnalysisConcurrencyIntegrationTests</c>), which set short limits on purpose. Every other test only needs the
/// tools to work, so on a loaded machine (a CI runner) it must not get its verdict from them: these locators probe
/// with <see cref="ProbeTimeout"/>, and analyses / decoders that are not about their timeout use
/// <see cref="ProcessTimeout"/>. A tool really missing from PATH still fails at once (it can't be started).
/// </remarks>
internal static class FfmpegTools
{
    /// <summary>The PATH probe's limit for the shared locators.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The ffprobe run / first decoded frame limit for tests whose subject is not that limit.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    private static readonly IOptions<FfmpegOptions> Options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());

    public static readonly FfmpegLocator FfmpegLocator = new(Options, NullLogger<FfmpegLocator>.Instance, ProbeTimeout);

    public static readonly FfprobeLocator FfprobeLocator = new(Options, NullLogger<FfprobeLocator>.Instance, ProbeTimeout);

    public static readonly string? Ffmpeg = FfmpegLocator.GetFfmpegPathAsync().GetAwaiter().GetResult();

    public static readonly string? Ffprobe = FfprobeLocator.GetFfprobePathAsync().GetAwaiter().GetResult();

    public static string? SkipReason => Ffmpeg is null || Ffprobe is null ? "ffmpeg/ffprobe not found on PATH." : null;

    /// <summary>An analysis service on the shared ffprobe, with the test limit.</summary>
    public static FfprobeMediaAnalysisService Analysis() =>
        new(FfprobeLocator, NullLogger<FfprobeMediaAnalysisService>.Instance) { ProcessTimeout = ProcessTimeout };

    /// <summary>Audio decoder settings with the test limit for the first frame (the rest as in the app).</summary>
    public static FfmpegAudioDecoderSettings AudioDecoderSettings => new() { FirstFrameTimeout = ProcessTimeout };
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
