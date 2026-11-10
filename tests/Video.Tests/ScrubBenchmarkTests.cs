using System.Diagnostics;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// A local measurement, not a check (Phase 38 acceptance, 2026-11-10): scrubbing a real file with the real
/// <see cref="PlaybackService"/> and ffmpeg, as the timeline does — a seek every 16 ms while the mouse moves, the Preview's
/// <see cref="PlaybackService.Update"/> at the same rate — reporting how many distinct pictures appeared during the drag,
/// and the time from the release to the exact frame. Runs only with <c>AIVE_SCRUB_BENCH_FILE</c> set to a video file
/// (e.g. a 4K HEVC phone recording); <c>AIVE_SCRUB_BENCH_HW=0</c> turns hardware decoding off. The results are written to
/// the test output and to <c>%TEMP%\AiVideoEditorScrubBench.txt</c>.
/// </summary>
public sealed class ScrubBenchmarkTests(ITestOutputHelper output)
{
    private const string Variable = "AIVE_SCRUB_BENCH_FILE";

    public sealed class ScrubBenchFactAttribute : FactAttribute
    {
        public ScrubBenchFactAttribute() =>
            Skip = FfmpegTools.SkipReason ?? (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } ? null : $"Set {Variable} to a video file to measure.");
    }

    [ScrubBenchFact]
    public async Task Scrub_a_real_file()
    {
        var path = Environment.GetEnvironmentVariable(Variable)!;
        var hardware = Environment.GetEnvironmentVariable("AIVE_SCRUB_BENCH_HW") == "0" ? HardwareDecoding.Disabled : HardwareDecoding.Auto;
        var analysis = await FfmpegTools.Analysis().AnalyzeAsync(path);
        var metadata = analysis.Metadata ?? throw new InvalidOperationException(analysis.ErrorMessage);
        var rate = FrameRate.Fps30;
        var project = new Project { Settings = { FrameRate = rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        project.MediaAssets.Add(asset);
        var length = MediaTime.FromFrame(metadata.Duration.ToFrameFloor(rate) - 1, rate);
        v1.Clips.Add(new VideoClip { MediaAssetId = asset.Id, Duration = length, SourceOut = length });

        var video = new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);
        var audio = new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance);
        await using var service = new PlaybackService(video, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { Hardware = hardware }, audio, new FakeAudioOutput());
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, 1));
        var lines = new List<string> { $"{Path.GetFileName(path)} {metadata.Width}×{metadata.Height} {metadata.FrameRate}, hardware {hardware}" };

        async Task<double> SettleAt(MediaTime target)
        {
            var watch = Stopwatch.StartNew();
            _ = service.SeekAsync(target);
            while (true)
            {
                var frame = service.Update();
                if (!frame.IsBuffering && frame.IsTopCurrent() && frame.TopFrame() is not null && service.Position == target) break;
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
                await Task.Delay(4);
            }
            return watch.Elapsed.TotalMilliseconds;
        }

        await SettleAt(MediaTime.FromSeconds(1));   // warm up (the first open)

        // Single seeks to mid-GOP positions (a phone's GOP is ~1 s): the time to the exact frame.
        var single = new List<double>();
        foreach (var s in new[] { 5.1, 9.5, 14.9, 20.7, 26.3, 31.85 })
            if (MediaTime.FromSeconds(s) < length) single.Add(await SettleAt(MediaTime.FromFrame(MediaTime.FromSeconds(s).ToNearestFrame(rate), rate)));
        lines.Add($"single seek → exact frame: {string.Join(" ", single.Select(Ms))} ms (median {Ms(Median(single))})");

        // Frame stepping: ten steps forward (→), then ten back (←), each to its exact frame.
        var stepFrom = MediaTime.FromFrame(MediaTime.FromSeconds(12.4).ToNearestFrame(rate), rate);
        await SettleAt(stepFrom);
        var forward = new List<double>();
        for (var i = 1; i <= 10; i++) forward.Add(await SettleAt(stepFrom + MediaTime.FromFrame(i, rate)));
        var back = new List<double>();
        for (var i = 9; i >= 0; i--) back.Add(await SettleAt(stepFrom + MediaTime.FromFrame(i, rate)));
        lines.Add($"step → : {string.Join(" ", forward.Select(Ms))} ms (median {Ms(Median(forward))}); " +
                  $"step ← : median {Ms(Median(back))} ms");

        // A drag: 3 s of mouse movement over 6 s of video, a seek per 16 ms, the Preview's update at the same rate.
        foreach (var speed in new[] { 0.5, 2.0, -2.0 })
        {
            var from = MediaTime.FromSeconds(speed > 0 ? 4 : 30);
            await SettleAt(from);
            var shown = new List<double>();
            DecodedFrame? last = null;
            var watch = Stopwatch.StartNew();
            MediaTime target = from;
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                var t = MediaTime.FromSeconds(from.TotalSeconds + watch.Elapsed.TotalSeconds * speed);
                target = MediaTime.FromFrame(Math.Min(t.ToFrameFloor(rate), length.ToFrameFloor(rate) - 1), rate);
                _ = service.SeekAsync(target);
                var frame = service.Update();
                // As the Preview takes it: a settled frame, or the previous seek's finished picture while the newest waits.
                if ((!frame.IsBuffering || frame.IsInterimPicture) && frame.TopFrame() is { } picture && !ReferenceEquals(picture, last))
                {
                    last = picture;
                    shown.Add(watch.Elapsed.TotalMilliseconds);
                }
                await Task.Delay(16);
            }
            var release = await SettleAt(target);
            var gaps = shown.Zip(shown.Skip(1), (a, b) => b - a).ToList();
            lines.Add($"drag ×{speed}: {shown.Count} pictures in 3 s ({shown.Count / 3.0:0.0}/s), longest gap {Ms(gaps.DefaultIfEmpty(0).Max())} ms, " +
                      $"median gap {Ms(Median(gaps))} ms; release → exact frame {Ms(release)} ms");
        }

        foreach (var line in lines) output.WriteLine(line);
        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "AiVideoEditorScrubBench.txt"),
            lines.Prepend($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ToArray());
    }

    private static string Ms(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
