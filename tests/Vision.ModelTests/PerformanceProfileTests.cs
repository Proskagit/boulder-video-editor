using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.UI.Services;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// Phase 19 Step 19.8, local: the markup pipeline's stages measured one by one on the real model and the E1 frames, cold
/// and warm, each with its median, p95 and maximum — so the D034 §7 bounds can be read against a definite statistic.
/// Stages: the model check (SHA-256 of both files), the session creation, the 4K markup frame decode (the app's decoder),
/// the preprocessing, the encoder (the first run after the sessions are created separately), the decoder per click, the
/// upsampling of the three masks to 4K, the largest component + outer contour + colour samples of a confirmed hold.
/// Report: <c>&lt;E1&gt;/results/perf-&lt;stamp&gt;/perf.md</c>. Never fails on a number (a profile, not a gate).
/// </summary>
public sealed class PerformanceProfileTests(ITestOutputHelper output)
{
    private sealed class Series(string name)
    {
        public string Name { get; } = name;
        public List<double> Ms { get; } = new();

        public string Row()
        {
            var s = Ms.OrderBy(x => x).ToArray();
            double P(double q) => s[(int)Math.Ceiling(q * s.Length) - 1];
            return string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:F0} | {3:F0} | {4:F0} | {5:F0} |",
                Name, s.Length, s[0], P(0.5), P(0.95), s[^1]);
        }
    }

    private static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public async Task Stage_timings_cold_and_warm()
    {
        var model = ReferenceData.ModelDir;
        var report = new StringBuilder();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(ReferenceData.E1Dir, "results", "perf-" + stamp);
        Directory.CreateDirectory(folder);

        // Model check (SHA-256, cold file cache unknown) and session creation, three fresh times each.
        var verify = new Series("model check (size + SHA-256, both files)");
        var create = new Series("session creation, GPU (encoder + decoder)");
        var firstEncode = new Series("first encode after the sessions are created, GPU");
        var frames = ReferenceData.Clicks().Where(c => c.Clip.StartsWith('c')).Select(c => c.Frame).Distinct().ToList();
        var frame0 = ReferenceData.Frame(frames[0]);
        var image0 = SamImageProcessing.Preprocess(frame0);
        for (var i = 0; i < 3; i++)
        {
            verify.Ms.Add(await TimeAsync(() => new HoldModelVerifier(model).VerifyAsync(null, CancellationToken.None)));
            OnnxHoldModelBackend? b = null;
            create.Ms.Add(Time(() => b = new OnnxHoldModelBackend(Path.Combine(model, HoldModel.Encoder.Name), Path.Combine(model, HoldModel.Decoder.Name), InferenceDevice.Gpu)));
            using (b) firstEncode.Ms.Add(Time(() => b!.Encode(image0, CancellationToken.None)));
        }

        // The service path the app takes: a fresh service, EnsureReady (check + sessions), the first and the second
        // PrepareFrame (preprocess + encode) of the same frame.
        var svcReady = new Series("service: EnsureReady on a fresh service (check + sessions)");
        var svcFirst = new Series("service: first PrepareFrame after EnsureReady");
        var svcSecond = new Series("service: second PrepareFrame");
        for (var i = 0; i < 3; i++)
        {
            using var svc = new OnnxHoldSegmentationService(model);
            svcReady.Ms.Add(await TimeAsync(() => svc.EnsureReadyAsync()));
            IHoldFrame? f1 = null, f2 = null;
            svcFirst.Ms.Add(await TimeAsync(async () => f1 = await svc.PrepareFrameAsync(frame0)));
            svcSecond.Ms.Add(await TimeAsync(async () => f2 = await svc.PrepareFrameAsync(frame0)));
            f1!.Dispose(); f2!.Dispose();
        }

        // Warm: one backend, every E1 frame three times.
        using var backend = new OnnxHoldModelBackend(Path.Combine(model, HoldModel.Encoder.Name), Path.Combine(model, HoldModel.Decoder.Name), InferenceDevice.Gpu);
        backend.Encode(image0, CancellationToken.None);
        var preprocess = new Series("preprocess (4K BGRA → 1024² tensor)");
        var encode = new Series("encode, warm, GPU");
        var decode = new Series("decode per click, warm, GPU (three masks)");
        var upsample = new Series("upsample three masks to 4K (CPU)");
        var click = new Series("click total = decode + upsample");
        var confirm = new Series("confirm: component + contour + colour samples");
        var features = new Dictionary<string, HoldFeatures>();
        var decoded = new Dictionary<string, DecodedFrame>();
        for (var round = 0; round < 3; round++)
            foreach (var f in frames)
            {
                var frame = decoded.TryGetValue(f, out var d) ? d : decoded[f] = ReferenceData.Frame(f);
                float[]? image = null;
                preprocess.Ms.Add(Time(() => image = SamImageProcessing.Preprocess(frame)));
                HoldFeatures? feat = null;
                encode.Ms.Add(Time(() => feat = backend.Encode(image!, CancellationToken.None)));
                features[f] = feat!;
            }
        foreach (var c in ReferenceData.Clicks().Where(c => c.Clip.StartsWith('c')))
        {
            var frame = decoded[c.Frame];
            var coords = new[] { (float)(c.X * HoldModel.InputSize / frame.Width), (float)(c.Y * HoldModel.InputSize / frame.Height) };
            HoldLogits? logits = null;
            var td = Time(() => logits = backend.Decode(features[c.Frame], coords, CancellationToken.None));
            var plane = HoldModel.MaskSize * HoldModel.MaskSize;
            var masks = new byte[logits!.Count][];
            var tu = Time(() =>
            {
                for (var k = 0; k < logits.Count; k++)
                    masks[k] = SamImageProcessing.Upsample(logits.Logits.AsSpan(k * plane, plane), HoldModel.MaskSize, frame.Width, frame.Height);
            });
            decode.Ms.Add(td);
            upsample.Ms.Add(tu);
            click.Ms.Add(td + tu);
            var best = Enumerable.Range(0, logits.Count).OrderByDescending(k => logits.Scores[k]).First();
            confirm.Ms.Add(Time(() =>
            {
                var (component, _) = MaskContour.LargestComponent(masks[best], frame.Width, frame.Height);
                MaskContour.Outer(component, frame.Width, frame.Height);
                HoldColorSampler.Sample(frame, component, Array.Empty<IReadOnlyList<NormalizedPoint>>());
            }));
        }

        // The app's own 4K markup frame decode (software, as HoldMarkupFrameSource).
        var frameDecode = new Series("markup frame decode, 4K (app decoder)");
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var path = ReferenceData.Path_("clips/12x10/1014(6).mp4");
        var metadata = (await new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(path)).Metadata!;
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        var clip = new VideoClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(8), SourceOut = MediaTime.FromSeconds(8) };
        var source = new HoldMarkupFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        foreach (var t in new[] { 0.5, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0 })
            frameDecode.Ms.Add(await TimeAsync(() => source.GetAsync(clip, asset, MediaTime.FromSeconds(t), metadata.FrameRate ?? FrameRate.Fps25, CancellationToken.None)));

        report.AppendLine("# Phase 19 — stage timings (local, real model)").AppendLine();
        report.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}; machine: {Environment.MachineName}; frames: {frames.Count} E1 4K frames; clicks: {click.Ms.Count}");
        report.AppendLine("- Times in ms; p95 = the value 95 % of the runs do not exceed (nearest rank).").AppendLine();
        report.AppendLine("| Stage | n | min | median | p95 | max |").AppendLine("|---|---|---|---|---|---|");
        foreach (var s in new[] { verify, create, firstEncode, svcReady, svcFirst, svcSecond, frameDecode, preprocess, encode, decode, upsample, click, confirm })
            report.AppendLine(s.Row());
        File.WriteAllText(Path.Combine(folder, "perf.md"), report.ToString());
        output.WriteLine(report.ToString());
        output.WriteLine(folder);
    }

    private static async Task<double> TimeAsync<T>(Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        return sw.Elapsed.TotalMilliseconds;
    }
}
