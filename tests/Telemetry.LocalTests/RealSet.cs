using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// The product owner's set of 2026-09-27 (personal data, never committed): the FIT, its folder and the 16 videos beside
/// it, probed with the app's own ffprobe service — shared by <see cref="RealStudyTests"/> and the reference-study tool.
/// </summary>
internal static class RealSet
{
    public const int VideoCount = 16;
    public const int ClimbCount = 14;

    /// <summary>The FIT of the set: <c>AIVE_COROS_FIT</c>, or the owner's default path.</summary>
    public static string FitPath =>
        Environment.GetEnvironmentVariable("AIVE_COROS_FIT") is { Length: > 0 } path
            ? path
            : @"D:\Времянка для монтажа\Боулдер 27.09.2026\Болдеринг20260927100239.fit";

    /// <summary>The <c>video_*.mp4</c> files beside the FIT, in ordinal order.</summary>
    public static IReadOnlyList<string> VideoPaths(string fitPath) =>
        Directory.GetFiles(Path.GetDirectoryName(fitPath)!, "video_*.mp4").OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>The videos as the app sees them: each probed by ffprobe (the app's limits), the probe required to succeed.</summary>
    public static async Task<IReadOnlyList<MediaAsset>> ProbeAsync(IEnumerable<string> paths)
    {
        var locator = new FfprobeLocator(Options.Create(new FfmpegOptions()), NullLogger<FfprobeLocator>.Instance);
        var analysis = new FfprobeMediaAnalysisService(locator, NullLogger<FfprobeMediaAnalysisService>.Instance);
        var videos = new List<MediaAsset>();
        foreach (var path in paths)
        {
            var result = await analysis.AnalyzeAsync(path);
            Assert.True(result.Outcome == MediaAnalysisOutcome.Success, $"{Path.GetFileName(path)}: {result.Outcome} {result.ErrorMessage}");
            videos.Add(new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = result.Metadata });
        }
        return videos;
    }

    /// <summary>The FIT parsed by the app's parser.</summary>
    public static async Task<ClimbingWorkout> WorkoutAsync(string fitPath)
    {
        Assert.True(File.Exists(fitPath), $"The COROS export was not found at {fitPath} (set AIVE_COROS_FIT).");
        return CorosWorkoutParser.Parse(await File.ReadAllBytesAsync(fitPath));
    }
}
