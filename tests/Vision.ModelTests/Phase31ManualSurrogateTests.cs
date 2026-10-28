using System.Collections.Immutable;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// Step 31.6 surrogate: the real proposal pipeline on actual footage, run headlessly because Computer use is not available
/// in this conversation. Times and proposal counts are written to the test output; images and a report are written to the
/// temp folder for the product owner's review. This is NOT a replacement for the interactive checks in
/// docs/PHASE31_MANUAL_TEST_PLAN.md — those are listed as NOT RUN below.
/// </summary>
public sealed class Phase31ManualSurrogateTests(ITestOutputHelper output)
{
    private static readonly string ReportDir = Path.Combine(Path.GetTempPath(), "AiVideoEditor_Phase31_Reports", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    private static (UndoRedoService Undo, ProjectService Projects, TimelineEditService Edit, HoldMarkupViewModel Markup, VideoClip Clip, MediaAsset Asset, OnnxHoldSegmentationService Segmentation, HoldProposalGenerator Generator, FfmpegVideoDecoder Decoder, string AssetPath) Setup(
        string clipPath, FrameRate? rateOverride = null)
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var analysis = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(clipPath).GetAwaiter().GetResult();
        var metadata = analysis.Metadata ?? throw new InvalidOperationException(analysis.ErrorMessage);
        var asset = new MediaAsset { FilePath = clipPath, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        projects.AddMediaAssets(new[] { asset });
        Assert.True(edit.AddClip(asset.Id, projects.Current.Timeline.VideoTracks[0].Id, MediaTime.Zero).Success);
        var clip = projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();

        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var segmentation = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var proposalService = new HoldProposalService();
        var generator = new HoldProposalGenerator(decoder, segmentation, proposalService);
        var refiner = new HoldProposalRefiner(decoder, segmentation);
        var status = new StatusService();
        var markup = new HoldMarkupViewModel(edit, projects, status, segmentation, new HoldMarkupFrameSource(decoder), new EditingLock(), generator, refiner)
        {
            Playhead = () => MediaTime.FromSeconds(0.5),
            IsPausedOnTimeline = () => true
        };
        markup.SetClip(clip);
        return (undo, projects, edit, markup, clip, asset, segmentation, generator, decoder, clipPath);
    }

    private static void WriteDiagnostics(ITestOutputHelper output, ProposalGenerationResult result, ProfilingBackend.Timings? backend = null)
    {
        var d = result.Diagnostics;
        if (d is null) return;
        output.WriteLine($"  Decode frame:     {d.DecodeMs:F1} ms");
        output.WriteLine($"  Prepare frame:    {d.PrepareFrameMs:F1} ms");
        if (backend is not null)
        {
            output.WriteLine($"  ONNX encode:      {backend.EncodeMs:F1} ms ({backend.EncodeCount} run)");
            output.WriteLine($"  ONNX decode:      {backend.DecodeMs:F1} ms ({backend.DecodeCount} runs, avg {backend.DecodeMs / Math.Max(1, backend.DecodeCount):F1} ms/run)");
            output.WriteLine($"  Upsample/mask:    {d.PredictionLoopMs - backend.DecodeMs:F1} ms");
        }
        else
        {
            output.WriteLine($"  Prediction loop:  {d.PredictionLoopMs:F1} ms ({d.PredictionCount} pts, avg {d.PredictionLoopMs / Math.Max(1, d.PredictionCount):F1} ms/pt)");
        }
        output.WriteLine($"  Post-process:     {d.PostProcessMs:F1} ms");
        output.WriteLine($"  NMS:              {d.NmsMs:F1} ms");
        output.WriteLine($"  Pipeline total:   {d.TotalMs:F1} ms");
    }

    private sealed class ProfilingBackend : IHoldModelBackend
    {
        public sealed class Timings
        {
            public long EncodeMs;
            public int EncodeCount;
            public long DecodeMs;
            public int DecodeCount;
        }

        private readonly IHoldModelBackend _inner;
        private readonly Timings _timings;

        public ProfilingBackend(IHoldModelBackend inner, Timings timings)
        {
            _inner = inner;
            _timings = timings;
        }

        public InferenceDevice Device => _inner.Device;

        public HoldFeatures Encode(float[] image, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var r = _inner.Encode(image, cancellationToken);
            _timings.EncodeMs += sw.ElapsedMilliseconds;
            _timings.EncodeCount++;
            return r;
        }

        public HoldLogits Decode(HoldFeatures features, float[] pointCoords, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var r = _inner.Decode(features, pointCoords, cancellationToken);
            _timings.DecodeMs += sw.ElapsedMilliseconds;
            _timings.DecodeCount++;
            return r;
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task Proposal_generation_and_accept_all_on_4K_E1_clip()
    {
        Directory.CreateDirectory(ReportDir);
        var (undo, projects, edit, markup, clip, asset, segmentation, generator, decoder, path) = Setup(ReferenceData.Path_("clips/12x10/1014(6).mp4"));
        output.WriteLine($"Asset: {path}");
        output.WriteLine($"Display size: {asset.Metadata?.Width}x{asset.Metadata?.Height}");

        var cold = Stopwatch.StartNew();
        Assert.True((await segmentation.EnsureReadyAsync()).IsReady);
        cold.Stop();
        output.WriteLine($"Cold start (SHA + sessions): {cold.Elapsed.TotalSeconds:F2} s");

        var sw = Stopwatch.StartNew();
        await markup.StartCommand.ExecuteAsync(null);
        sw.Stop();
        output.WriteLine($"Start -> proposals ready (cold): {sw.Elapsed.TotalSeconds:F2} s");

        Assert.True(markup.IsActive);
        Assert.True(markup.HasProposals);
        Assert.True(markup.Proposals.Count <= 30, $"Default shown {markup.Proposals.Count} proposals; expected ≤30.");
        output.WriteLine($"Proposals shown: {markup.Proposals.Count}");
        for (var i = 0; i < markup.Proposals.Count; i++)
            output.WriteLine($"  {i + 1}. score {markup.Proposals[i].Score:F3}, area {markup.Proposals[i].Proposal.AreaFraction:F3}");

        File.WriteAllText(Path.Combine(ReportDir, "4k_proposals.txt"),
            $"4K E1 c6 proposal pass (cold){Environment.NewLine}Time: {sw.Elapsed.TotalSeconds:F2} s{Environment.NewLine}Count: {markup.Proposals.Count}{Environment.NewLine}" +
            string.Join(Environment.NewLine, markup.Proposals.Select((p, i) => $"{i + 1}. score={p.Score:F3} area={p.Proposal.AreaFraction:F3}")));

        // Warm run: model sessions are already loaded.
        markup.DoneCommand.Execute(null);
        sw.Restart();
        await markup.StartCommand.ExecuteAsync(null);
        sw.Stop();
        output.WriteLine($"Start -> proposals ready (warm): {sw.Elapsed.TotalSeconds:F2} s");
        File.AppendAllText(Path.Combine(ReportDir, "4k_proposals.txt"),
            $"{Environment.NewLine}Warm pass: {sw.Elapsed.TotalSeconds:F2} s{Environment.NewLine}");

        var proposalCount = markup.Proposals.Count;
        sw.Restart();
        await markup.AcceptAllProposalsCommand.ExecuteAsync(null);
        sw.Stop();
        output.WriteLine($"Accept-all refinement: {sw.Elapsed.TotalSeconds:F2} s");
        Assert.Equal(proposalCount, clip.HoldMarkup?.Holds.Length ?? 0);

        undo.Undo();
        Assert.Null(clip.HoldMarkup);

        // Round-trip: after accept-all, save, reload, check v9 and accepted holds.
        await markup.AcceptAllProposalsCommand.ExecuteAsync(null);
        var projectDir = Path.Combine(ReportDir, "project");
        Directory.CreateDirectory(projectDir);
        var json = ProjectSerializer.Serialize(projects.Current, projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.json"), json);
        Assert.Contains("\"formatVersion\": 10", json);
        Assert.DoesNotContain("proposal", json, StringComparison.OrdinalIgnoreCase);

        var reloaded = ProjectSerializer.Deserialize(json, projectDir);
        var reloadedClip = reloaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
        Assert.Equal(proposalCount, reloadedClip.HoldMarkup?.Holds.Length ?? 0);
    }

    [Fact]
    public async Task Proposal_generation_on_1080p_clip()
    {
        var path = @"C:\AiVideoEditor\VideosForTest\Phase28_Step28.1\s1_same_wall_horizontal.mp4";
        if (!File.Exists(path))
        {
            output.WriteLine($"1080p clip not found at {path}; skipping.");
            return;
        }

        var (_, _, _, markup, _, _, segmentation, _, _, _) = Setup(path);
        var cold = Stopwatch.StartNew();
        Assert.True((await segmentation.EnsureReadyAsync()).IsReady);
        cold.Stop();
        output.WriteLine($"1080p cold start (SHA + sessions): {cold.Elapsed.TotalSeconds:F2} s");

        // Warm the pipeline once so the second measurement excludes first-run overhead.
        await markup.StartCommand.ExecuteAsync(null);
        markup.DoneCommand.Execute(null);

        var sw = Stopwatch.StartNew();
        await markup.StartCommand.ExecuteAsync(null);
        sw.Stop();
        output.WriteLine($"1080p proposal pass (warm): {sw.Elapsed.TotalSeconds:F2} s, {markup.Proposals.Count} proposals");
        Assert.True(markup.HasProposals);
    }

    [Fact]
    public async Task Profile_pipeline_stages_on_4K_and_1080p()
    {
        var clips = new[]
        {
            ("4K", ReferenceData.Path_("clips/12x10/1014(6).mp4")),
            ("1080p", @"C:\AiVideoEditor\VideosForTest\Phase28_Step28.1\s1_same_wall_horizontal.mp4")
        };
        var gridDivisions = new[] { 16, 15, 14, 12 };

        foreach (var (label, path) in clips)
        {
            if (!File.Exists(path))
            {
                output.WriteLine($"{label} clip not found at {path}; skipping.");
                continue;
            }

            var (_, _, _, _, clip, asset, _, _, decoder, _) = Setup(path);
            var rate = asset.Metadata!.FrameRate ?? new FrameRate(60, 1);

            foreach (var divisions in gridDivisions)
            {
                var verifier = new HoldModelVerifier(ReferenceData.ModelDir);
                var backendTimings = new ProfilingBackend.Timings();
                var segmentation = new OnnxHoldSegmentationService(verifier,
                    device => new ProfilingBackend(new OnnxHoldModelBackend(verifier.PathOf(HoldModel.Encoder), verifier.PathOf(HoldModel.Decoder), device), backendTimings),
                    null);
                var proposalService = new HoldProposalService();
                var generator = new HoldProposalGenerator(decoder, segmentation, proposalService);

                Assert.True((await segmentation.EnsureReadyAsync()).IsReady);

                var options = new ProposalPipelineOptions { GridDivisions = divisions };

                // Warm-up pass for this grid size.
                await generator.GenerateAsync(clip, asset, MediaTime.FromSeconds(0.5), rate, options);

                var sw = Stopwatch.StartNew();
                var result = await generator.GenerateAsync(clip, asset, MediaTime.FromSeconds(0.5), rate, options);
                sw.Stop();

                output.WriteLine("");
                output.WriteLine($"=== {label} grid={divisions}×{divisions} (warm) ===");
                output.WriteLine($"Total wall: {sw.Elapsed.TotalSeconds:F2} s, proposals: {result.Proposals.Count}");
                WriteDiagnostics(output, result, backendTimings);
                output.WriteLine($"Top-30 scores: {string.Join(", ", result.Proposals.Take(30).Select(p => p.Score.ToString("F3")))}");
            }
        }
    }

}
