using System.Text.Json;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 independent validation (<c>artifacts/d048-colour-audit/independent-validation/PLAN.md</c> §1.2), local research
/// only, no hold search: for every whole second <c>T</c> of each clip, whether the app's route selection could work there
/// — the clean frame at <c>T − 5 s</c> and the frame at <c>T</c> decoded by <see cref="RouteSelectionFrameSource"/>, the
/// click transfer by <see cref="RouteSelectionTransfer"/> (a click at the frame's centre), the camera motion from the
/// clean frame to every second up to <c>T</c> by <see cref="CameraMotionEstimator"/> (max shift of 9 grid points, px at
/// width 1920). Frames are read once per second, a sliding window of six. Clips are SDR only (asserted). Writes
/// <c>windows.json</c> to <c>AIVE_IV_CHECK</c>; the clip list is <c>AIVE_IV_FILES</c> (a JSON array of paths).
/// </summary>
public sealed class IndependentValidationWindowTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [Fact]
    public async Task Check_the_windows()
    {
        var dir = Environment.GetEnvironmentVariable("AIVE_IV_CHECK");
        var list = Environment.GetEnvironmentVariable("AIVE_IV_FILES");
        if (dir is null || list is null || !File.Exists(list))
        {
            output.WriteLine("SKIPPED: AIVE_IV_CHECK / AIVE_IV_FILES not set");
            return;
        }
        Directory.CreateDirectory(dir);
        Assert.False(File.Exists(Path.Combine(dir, "windows.json")), "windows.json exists — results are never overwritten");
        var files = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(list))!;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var probe = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance), NullLogger<FfprobeMediaAnalysisService>.Instance);
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));

        var results = new List<object>();
        foreach (var file in files)
        {
            var metadata = (await probe.AnalyzeAsync(file)).Metadata!;
            Assert.False(HdrColor.Classify(MediaKind.Video, metadata).Conversion is not null, $"{file}: HDR is not expected in this set");
            var asset = new MediaAsset { FilePath = file, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            var seconds = (int)Math.Floor(metadata.Duration.TotalSeconds - 0.1);
            var window = new List<(GrayImage Image, MediaTime Time)>();
            var perT = new List<object>();
            for (var s = 0; s <= seconds; s++)
            {
                var f = await frames.DecodeAsync(asset, MediaTime.FromSeconds(s), RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                window.Add((GrayImage.FromBgra(f.Frame), f.SourceTime));
                if (window.Count > 6) window.RemoveAt(0);
                if (window.Count < 6) continue;

                var (clean, cleanTime) = window[0];
                var (atT, tTime) = window[5];
                var transfer = RouteSelectionTransfer.Transfer(clean, cleanTime, atT, tTime, new NormalizedPoint(0.5, 0.5));
                var estimator = new CameraMotionEstimator(clean, cleanTime);
                for (var k = 1; k < 6; k++) estimator.Add(window[k].Image, window[k].Time);
                var built = estimator.Build();
                double maxShift = 0;
                var reliable = true;
                foreach (var sample in built.Samples.Where(x => x.SourceTime != cleanTime))
                {
                    reliable &= sample.IsReliable;
                    foreach (var gx in new[] { 0.05, 0.5, 0.95 })
                    foreach (var gy in new[] { 0.05, 0.5, 0.95 })
                    {
                        var (x, y) = sample.ReferenceToFrame.Apply(gx, gy);
                        maxShift = Math.Max(maxShift, Math.Sqrt(Math.Pow((x - gx) * W, 2) + Math.Pow((y - gy) * H, 2)));
                    }
                }
                perT.Add(new { t_s = s, transfer_ok = transfer.Succeeded, reliable, max_shift_px_1920 = Math.Round(maxShift, 2),
                    usable = transfer.Succeeded && reliable && maxShift <= 3 });
            }
            var usable = perT.Count(p => (bool)p.GetType().GetProperty("usable")!.GetValue(p)!);
            output.WriteLine($"{Path.GetFileName(file)}: {seconds + 1} s, T candidates {perT.Count}, usable {usable}");
            results.Add(new
            {
                file, name = Path.GetFileNameWithoutExtension(file), duration_s = metadata.Duration.TotalSeconds,
                display = new[] { metadata.DisplayWidth ?? metadata.Width, metadata.DisplayHeight ?? metadata.Height }, transfer = metadata.ColorTransfer,
                rule = "T usable: transfer ok, every motion sample from T-5 to T reliable, max shift <= 3 px at width 1920",
                per_t = perT
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "windows.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
