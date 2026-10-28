using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage 1, the split / chroma experiment (local, outside the solution; research only — nothing in the app changes).
/// On the six reference frames of <see cref="ReferenceBaselineSixClipsTests"/> (same snapshot, clicks, frame decode and
/// click transfer), each frame encoded once, two searches:
/// <list type="bullet">
/// <item>the production <see cref="RouteHoldFinder.FindAsync"/> with its defaults (the baseline, re-run for timing under the
/// same conditions);</item>
/// <item>a research copy of it (<see cref="ResearchFindAsync"/>, every step the same) whose colour-map prompts come from
/// <see cref="SplitPrompts"/>: a colour-map region (distance &lt; 0.10, 4-connected, as <see cref="ColorPromptPlanner"/>)
/// larger than <see cref="LargeRegionFactor"/> × the control mask's area (in map blocks) is split again at distance &lt;
/// <see cref="SplitDistance"/>, each part one prompt at its innermost block; the planner's rule otherwise (largest first, at
/// most 300); no D047 grid.</item>
/// </list>
/// Each search is selected twice by <see cref="RouteHoldSelector"/> at τ 0.08: as is, and with the chroma gate (a candidate
/// other than the control whose OKLab chroma is below <see cref="ChromaRatio"/> × the control hold's is left out before the
/// selection). Four variants, each written to its own directory under <c>AIVE_SPLIT_DIR</c>: <c>baseline-rerun</c>,
/// <c>chroma</c>, <c>split</c>, <c>split-chroma</c>. The order of the two searches alternates per case (warm-up fairness).
/// Before anything, the research copy is checked against production: its unsplit planner equals
/// <see cref="ColorPromptPlanner.Plan"/> on every frame, and with the production prompts it reproduces
/// <see cref="RouteHoldFinder.FindAsync"/> on case 01.
/// </summary>
public sealed class ReferenceSplitExperimentTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;
    private const double SplitDistance = 0.06;
    private const double LargeRegionFactor = 4.0;
    private const double ChromaRatio = 0.4;
    private static readonly string[] Variants = ["baseline-rerun", "chroma", "split", "split-chroma"];

    [Fact]
    public async Task Run_the_split_and_chroma_experiment_on_the_six_reference_frames()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_SPLIT_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(snapshot, "project.json")))
        {
            output.WriteLine("SKIPPED: AIVE_BASELINE_SNAPSHOT / AIVE_SPLIT_DIR not set");
            return;
        }
        foreach (var v in Variants)
            Assert.False(File.Exists(Path.Combine(dir, v, "run.json")), $"{v}/run.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var rate = project.Settings.FrameRate;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var results = Variants.ToDictionary(v => v, _ => new List<object>());
        var index = 0;
        foreach (var m in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = m.GetProperty("case").GetString()!;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty;
            var tFrame = m.GetProperty("t_marker").GetProperty("timeline_frame").GetInt64();
            var n = m.GetProperty("click").GetProperty("normalized");
            var click = new NormalizedPoint(n[0].GetDouble(), n[1].GetDouble());

            // The frame and the control point exactly as in the six-clip baseline.
            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            Assert.Equal(m.GetProperty("clean_source").GetProperty("requested_ticks").GetInt64(), prepared.Frames.CleanSourceTime.Ticks);
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);
            var referenceTimes = holds.Select(h => h.SourceTime).Distinct().ToList();
            Assert.Single(referenceTimes);
            var referenceTime = referenceTimes[0];
            var frame = prepared.Clean;
            var control = prepared.ControlPoint;
            if (referenceTime != prepared.Clean.SourceTime)
            {
                frame = await frames.DecodeAsync(asset, referenceTime, prepared.Clean.Frame.Width, prepared.Clean.Frame.Height, default);
                Assert.Equal(referenceTime, frame.SourceTime);
                var atT = await frames.DecodeAsync(asset, prepared.Frames.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var refSmall = await frames.DecodeAsync(asset, referenceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var transfer = RouteSelectionTransfer.Transfer(GrayImage.FromBgra(refSmall.Frame), refSmall.SourceTime, GrayImage.FromBgra(atT.Frame), atT.SourceTime, click);
                Assert.True(transfer.Succeeded, transfer.Error);
                control = transfer.Point!.Value;
            }
            int w = frame.Frame.Width, h = frame.Frame.Height;

            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;

            // The research copy against production: the unsplit planner, and (on 01) the whole search.
            async Task<RouteHoldSearch> Production() => (await RouteHoldFinder.FindAsync(encoded, frame.Frame, control, ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty, search)).Search!;
            if (caseId == "01")
            {
                var reference = await Production();
                var copy = (await ResearchFindAsync(encoded, frame.Frame, control, search, split: false)).Search!;
                Assert.Equal(reference.Prompts, copy.Prompts);
                Assert.Equal(reference.Candidates.Count, copy.Candidates.Count);
                var a = reference.Select(RouteHoldColor.DefaultTolerance).Select(c => string.Join(";", c.Contour)).Order().ToList();
                var b = copy.Select(RouteHoldColor.DefaultTolerance).Select(c => string.Join(";", c.Contour)).Order().ToList();
                Assert.Equal(a, b);
                output.WriteLine($"01: research copy reproduces production ({copy.Candidates.Count} candidates, {a.Count} proposals)");
            }

            var runs = new Dictionary<string, (RouteHoldSearch Search, long SearchMs, IReadOnlyList<(PointD Point, string Source)> Prompts)>();
            async Task RunProduction()
            {
                var watch = Stopwatch.StartNew();
                var s = await Production();
                var ms = watch.ElapsedMilliseconds;
                var prompts = ColorPromptPlanner.Plan(frame.Frame, s.Reference, search.MapThreshold, maxPrompts: search.MaxColorPrompts).Select(p => (p, "colour"))
                    .Concat(HoldProposalService.GridPoints(w, h, search.GridDivisions).Select(p => (p, "grid"))).ToList();
                Assert.Equal(s.Prompts, prompts.Count);
                runs["baseline"] = (s, ms, prompts);
            }
            async Task RunSplit()
            {
                var watch = Stopwatch.StartNew();
                var (s, prompts) = await ResearchFindAsync(encoded, frame.Frame, control, search, split: true);
                runs["split"] = (s!, watch.ElapsedMilliseconds, prompts);
            }
            if (index++ % 2 == 0) { await RunProduction(); await RunSplit(); }
            else { await RunSplit(); await RunProduction(); }

            foreach (var variant in Variants)
            {
                var (s, searchMs, prompts) = runs[variant.StartsWith("split") ? "split" : "baseline"];
                var gate = variant.EndsWith("chroma");
                var refChroma = Chroma(s.Reference);
                var watch = Stopwatch.StartNew();
                var pool = gate ? s.Candidates.Where(c => c.IsControl || Chroma(c.Surface) >= ChromaRatio * refChroma).ToList() : s.Candidates.ToList();
                var accepted = RouteHoldSelector.Select(pool, RouteHoldColor.DefaultTolerance);
                var selectMs = watch.Elapsed.TotalMilliseconds;
                var chromaRejected = gate ? s.Candidates.Where(c => !c.IsControl && Chroma(c.Surface) < ChromaRatio * refChroma).Select(c => c.Id).ToArray() : [];
                output.WriteLine($"{caseId} {variant}: {prompts.Count} prompts, {s.Candidates.Count} candidates, {accepted.Count} proposals; " +
                                 $"prepare {prepareMs} + encode {encodeMs} + search {searchMs} + select {selectMs:0} ms");
                results[variant].Add(new
                {
                    @case = caseId, frame_s = frame.SourceTime.Ticks / 1e7, clean_frame_s = prepared.Clean.SourceTime.Ticks / 1e7,
                    on_reference_frame = frame.SourceTime == referenceTime, frame = new[] { w, h },
                    timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs, search = searchMs, select = selectMs },
                    parameters = new
                    {
                        tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, grid = !variant.StartsWith("split"),
                        split_distance = variant.StartsWith("split") ? SplitDistance : (double?)null,
                        large_region_factor = variant.StartsWith("split") ? LargeRegionFactor : (double?)null,
                        chroma_ratio = gate ? ChromaRatio : (double?)null, reference_chroma = refChroma
                    },
                    reference = holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B },
                        contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                    runs = new[]
                    {
                        new
                        {
                            label = variant, control = new[] { control.X, control.Y }, search_ms = searchMs,
                            reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = prompts.Count,
                            prompt_points = prompts.Select(p => new { x = p.Point.X / w, y = p.Point.Y / h, source = p.Source }),
                            candidates = s.Candidates.Count,
                            predictions = accepted.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            all_candidates = s.Candidates.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                surface = new[] { c.Surface.L, c.Surface.A, c.Surface.B }, contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            chroma_rejected = chromaRejected
                        }
                    }
                });
            }
        }

        foreach (var v in Variants)
        {
            Directory.CreateDirectory(Path.Combine(dir, v));
            await File.WriteAllTextAsync(Path.Combine(dir, v, "run.json"), JsonSerializer.Serialize(results[v]));
        }
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));       // the reference never written
        output.WriteLine("reference snapshot unchanged: " + expectedHash);
    }

    private static double Chroma(OkLabColor c) => Math.Sqrt(c.A * c.A + c.B * c.B);

    /// <summary>
    /// <see cref="RouteHoldFinder.FindAsync"/>, step for step (no existing holds), with the colour-map prompts of
    /// <see cref="SplitPrompts"/>: split when <paramref name="split"/>, else exactly the planner's, plus the grid.
    /// </summary>
    private static async Task<(RouteHoldSearch? Search, IReadOnlyList<(PointD Point, string Source)> Prompts)> ResearchFindAsync(
        IHoldFrame encoded, DecodedFrame frame, NormalizedPoint control, RouteHoldSearchOptions options, bool split, double[]? levels = null,
        double? keepPromptsInMasksOver = null)
    {
        int w = frame.Width, h = frame.Height;
        var controlPrompt = new PointD(Math.Clamp(control.X, 0, 1) * w, Math.Clamp(control.Y, 0, 1) * h);
        var controlMask = await MaskAt(encoded, controlPrompt, w, h);
        if (controlMask is null) return (null, []);
        var (controlCrop, controlScore, controlContour) = controlMask.Value;
        var reference = Surface(frame, controlCrop);

        var (distance, mw, mh, block) = DistanceMap(frame, reference);
        var controlBlocks = (double)controlCrop.Area / ((long)w * h) * mw * mh;
        // The copy's unsplit planner must be the app's planner, prompt for prompt.
        var planned = ColorPromptPlanner.Plan(frame, reference, options.MapThreshold, maxPrompts: options.MaxColorPrompts);
        Assert.Equal(planned, SplitPrompts(distance, mw, mh, block, options.MapThreshold, null, 0, options.MaxColorPrompts));
        var colour = levels is null
            ? SplitPrompts(distance, mw, mh, block, options.MapThreshold, split ? SplitDistance : null, LargeRegionFactor * controlBlocks, options.MaxColorPrompts)
            : SplitPromptsLevels(distance, mw, mh, block, options.MapThreshold, levels, LargeRegionFactor * controlBlocks, options.MaxColorPrompts);
        var prompts = colour.Select(p => (p, split ? "split" : "colour")).ToList();
        if (!split && options.UseGrid) prompts.AddRange(HoldProposalService.GridPoints(w, h, options.GridDivisions).Select(p => (p, "grid")));

        var candidates = new List<RouteHoldCandidate> { Candidate(controlCrop, controlScore, controlContour, reference, reference, isControl: true) };
        foreach (var (p, _) in prompts)
        {
            int px = Math.Clamp((int)p.X, 0, w - 1), py = Math.Clamp((int)p.Y, 0, h - 1);
            // A prompt inside a mask already found is skipped — with keepPromptsInMasksOver, only inside a mask at most that
            // many times the control's area (a prompt inside a wall or panel mask still asks the model).
            if (candidates.Any(c => c.Mask.Contains(px, py) &&
                    (keepPromptsInMasksOver is null || c.Mask.Area <= keepPromptsInMasksOver.Value * controlCrop.Area))) continue;
            if (await MaskAt(encoded, p, w, h) is not { } found) continue;
            var (crop, score, contour) = found;
            var candidate = Candidate(crop, score, contour, Surface(frame, crop), reference, isControl: false);
            var duplicate = candidates.FindIndex(c => c.Mask.IoU(crop) >= options.NmsIouThreshold);
            if (duplicate < 0) candidates.Add(candidate);
            else if (!candidates[duplicate].IsControl && candidates[duplicate].Score < score) candidates[duplicate] = candidate;
        }
        return (new RouteHoldSearch(reference, candidates, prompts.Count), prompts);
    }

    private static async Task<(MaskCrop Crop, double Score, ImmutableArray<NormalizedPoint> Contour)?> MaskAt(IHoldFrame encoded, PointD prompt, int w, int h)
    {
        var set = await encoded.PredictAsync(new[] { prompt }, default);
        if (HoldCandidateRules.Choose(set) is not { } k) return null;
        var mask = set.Masks[k];
        if (mask.Width != w || mask.Height != h) return null;
        var (component, _) = MaskContour.LargestComponent(mask.Pixels, w, h);
        var contour = MaskContour.Outer(component, w, h);
        if (contour.Length < HoldMarkupRules.MinPoints || MaskCrop.From(component, w, h) is not { } crop) return null;
        return (crop, mask.Score, contour);
    }

    private static OkLabColor Surface(DecodedFrame frame, MaskCrop crop) =>
        HoldColorSampler.Sample(frame, crop.ToFrameMask(), Array.Empty<IReadOnlyList<NormalizedPoint>>()).Surface;

    private static RouteHoldCandidate Candidate(MaskCrop crop, double score, ImmutableArray<NormalizedPoint> contour,
        OkLabColor surface, OkLabColor reference, bool isControl) =>
        new(Guid.NewGuid(), contour, HoldProposalService.ComputeCentroid(contour), Math.Clamp(score, 0, 1),
            (double)crop.Area / ((long)crop.FrameWidth * crop.FrameHeight), surface, RouteHoldColor.Distance(surface, reference), crop,
            isControl);

    /// <summary>The planner's regions and prompts (innermost block, largest regions first — ties in scan order — capped),
    /// a region larger than <paramref name="largeBlocks"/> split again at <paramref name="split"/> when given (its parts in
    /// its place in the scan order).</summary>
    private static List<PointD> SplitPrompts(double[] distance, int mw, int mh, int block, double threshold, double? split, double largeBlocks, int max)
    {
        var regions = new List<(int Area, int Best)>();
        var near = distance.Select(d => d < threshold).ToArray();
        foreach (var region in Regions(near, mw, mh))
        {
            if (split is null || region.Count <= largeBlocks)
            {
                regions.Add((region.Count, Innermost(region, mw, mh)));
                continue;
            }
            var inner = new bool[mw * mh];
            foreach (var i in region) inner[i] = distance[i] < split.Value;
            foreach (var part in Regions(inner, mw, mh)) regions.Add((part.Count, Innermost(part, mw, mh)));
        }
        return regions.Select((x, order) => (x.Area, x.Best, order)).OrderByDescending(x => x.Area).ThenBy(x => x.order).Take(max)
            .Select(x => new PointD((x.Best % mw + 0.5) * block, (x.Best / mw + 0.5) * block)).ToList();
    }

    /// <summary>
    /// <see cref="SplitPrompts"/> with several split levels: a region (or a part) still larger than
    /// <paramref name="largeBlocks"/> is split at the next level's distance, its parts in its place in the scan order. With
    /// one level it is <see cref="SplitPrompts"/> exactly (checked by the tests).
    /// </summary>
    private static List<PointD> SplitPromptsLevels(double[] distance, int mw, int mh, int block, double threshold, double[] levels, double largeBlocks, int max)
    {
        var regions = new List<(int Area, int Best)>();
        void Expand(List<int> region, int level)
        {
            if (level >= levels.Length || region.Count <= largeBlocks)
            {
                regions.Add((region.Count, Innermost(region, mw, mh)));
                return;
            }
            var inner = new bool[mw * mh];
            foreach (var i in region) inner[i] = distance[i] < levels[level];
            foreach (var part in Regions(inner, mw, mh)) Expand(part, level + 1);
        }
        foreach (var region in Regions(distance.Select(d => d < threshold).ToArray(), mw, mh)) Expand(region, 0);
        return regions.Select((x, order) => (x.Area, x.Best, order)).OrderByDescending(x => x.Area).ThenBy(x => x.order).Take(max)
            .Select(x => new PointD((x.Best % mw + 0.5) * block, (x.Best / mw + 0.5) * block)).ToList();
    }

    private static readonly double[] OneLevel = [SplitDistance];
    private static readonly double[] TwoLevels = [SplitDistance, 0.04];

    /// <summary>
    /// The second split level, offline (no model): on each reference frame, with the reference colour and the control mask
    /// area of the saved split run (<c>AIVE_SPLIT_DIR/split/run.json</c>), the colour-map prompts of one level (must equal the
    /// saved run's prompts) and of two levels (0.06, then 0.04 for a part still larger than 4× the control). Writes
    /// <c>AIVE_SPLIT2_DIR/split2-coverage.json</c> (the prompt points of both); coverage is computed by the evaluation script.
    /// </summary>
    [Fact]
    public async Task Estimate_the_second_split_level_offline()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var splitDir = Environment.GetEnvironmentVariable("AIVE_SPLIT_DIR");
        var dir = Environment.GetEnvironmentVariable("AIVE_SPLIT2_DIR");
        if (snapshot is null || splitDir is null || dir is null || !File.Exists(Path.Combine(splitDir, "split", "run.json")))
        {
            output.WriteLine("SKIPPED: AIVE_BASELINE_SNAPSHOT / AIVE_SPLIT_DIR / AIVE_SPLIT2_DIR not set");
            return;
        }
        Directory.CreateDirectory(dir);
        Assert.False(File.Exists(Path.Combine(dir, "split2-coverage.json")), "split2-coverage.json exists — results are never overwritten");
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(splitDir, "split", "run.json")));
        var (project, frames) = await OpenAsync(snapshot);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var search = new RouteHoldSearchOptions();

        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var size = c.GetProperty("frame");
            var frame = (await frames.DecodeAsync(asset, clip.HoldMarkup!.Holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default)).Frame;
            var run0 = c.GetProperty("runs")[0];
            var r = run0.GetProperty("reference_oklab");
            var reference = new OkLabColor(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble());
            var controlArea = run0.GetProperty("all_candidates").EnumerateArray().Single(x => x.GetProperty("is_control").GetBoolean()).GetProperty("area").GetDouble();
            var (distance, mw, mh, block) = DistanceMap(frame, reference);
            var large = LargeRegionFactor * controlArea * mw * mh;
            var one = SplitPromptsLevels(distance, mw, mh, block, search.MapThreshold, OneLevel, large, search.MaxColorPrompts);
            Assert.Equal(SplitPrompts(distance, mw, mh, block, search.MapThreshold, SplitDistance, large, search.MaxColorPrompts), one);
            var saved = run0.GetProperty("prompt_points").EnumerateArray().Select(p => (X: p.GetProperty("x").GetDouble(), Y: p.GetProperty("y").GetDouble())).ToList();
            Assert.Equal(saved.Count, one.Count);
            for (var k = 0; k < one.Count; k++)
            {
                Assert.Equal(saved[k].X, one[k].X / frame.Width, 9);
                Assert.Equal(saved[k].Y, one[k].Y / frame.Height, 9);
            }
            var uncapped = SplitPromptsLevels(distance, mw, mh, block, search.MapThreshold, TwoLevels, large, int.MaxValue).Count;
            var two = SplitPromptsLevels(distance, mw, mh, block, search.MapThreshold, TwoLevels, large, search.MaxColorPrompts);
            output.WriteLine($"{caseId}: one level {one.Count} prompts, two levels {two.Count} (uncapped {uncapped})");
            results.Add(new
            {
                @case = caseId, uncapped_two_levels = uncapped,
                one_level = one.Select(p => new { x = p.X / frame.Width, y = p.Y / frame.Height }),
                two_levels = two.Select(p => new { x = p.X / frame.Width, y = p.Y / frame.Height })
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "split2-coverage.json"), JsonSerializer.Serialize(results));
    }

    /// <summary>
    /// Diagnostic for the offline estimate (no model): the two-level prompts without the 300 cap, and the one-level ones too,
    /// to tell a hold that never separates from one whose part is cut by the cap. Writes
    /// <c>AIVE_SPLIT2_DIR/split2-uncapped.json</c>.
    /// </summary>
    [Fact]
    public async Task Estimate_the_second_split_level_without_the_cap()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var splitDir = Environment.GetEnvironmentVariable("AIVE_SPLIT_DIR");
        var dir = Environment.GetEnvironmentVariable("AIVE_SPLIT2_DIR");
        if (snapshot is null || splitDir is null || dir is null || !File.Exists(Path.Combine(splitDir, "split", "run.json")))
        {
            output.WriteLine("SKIPPED");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "split2-uncapped.json")), "split2-uncapped.json exists — results are never overwritten");
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(splitDir, "split", "run.json")));
        var (project, frames) = await OpenAsync(snapshot);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var search = new RouteHoldSearchOptions();
        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var size = c.GetProperty("frame");
            var frame = (await frames.DecodeAsync(asset, clip.HoldMarkup!.Holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default)).Frame;
            var run0 = c.GetProperty("runs")[0];
            var r = run0.GetProperty("reference_oklab");
            var (distance, mw, mh, block) = DistanceMap(frame, new OkLabColor(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble()));
            var controlArea = run0.GetProperty("all_candidates").EnumerateArray().Single(x => x.GetProperty("is_control").GetBoolean()).GetProperty("area").GetDouble();
            var large = LargeRegionFactor * controlArea * mw * mh;
            object Points(double[] levels) => SplitPromptsLevels(distance, mw, mh, block, search.MapThreshold, levels, large, int.MaxValue)
                .Select(p => new { x = p.X / frame.Width, y = p.Y / frame.Height });
            results.Add(new { @case = caseId, one_level = Points(OneLevel), two_levels = Points(TwoLevels) });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "split2-uncapped.json"), JsonSerializer.Serialize(results));
    }

    /// <summary>
    /// The second split level, the full run: on the six reference frames (each encoded once), the research search with one
    /// level (the earlier split, re-run for timing under the same conditions) and with two levels, alternating in order per
    /// case; each selected as is and with the chroma gate. Writes <c>split-rerun</c>, <c>split-chroma-rerun</c>,
    /// <c>split2</c>, <c>split2-chroma</c> under <c>AIVE_SPLIT2_DIR</c>, in the format of the first experiment.
    /// </summary>
    [Fact]
    public async Task Run_the_second_split_level_on_the_six_reference_frames()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_SPLIT2_DIR");
        if (snapshot is null || dir is null || Environment.GetEnvironmentVariable("AIVE_SPLIT2_RUN") != "1")
        {
            output.WriteLine("SKIPPED: AIVE_SPLIT2_RUN=1 / AIVE_BASELINE_SNAPSHOT / AIVE_SPLIT2_DIR not set");
            return;
        }
        string[] variants = ["split-rerun", "split-chroma-rerun", "split2", "split2-chroma"];
        foreach (var v in variants)
            Assert.False(File.Exists(Path.Combine(dir, v, "run.json")), $"{v}/run.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        var (project, frames) = await OpenAsync(snapshot);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var results = variants.ToDictionary(v => v, _ => new List<object>());
        var index = 0;
        foreach (var m in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = m.GetProperty("case").GetString()!;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var n = m.GetProperty("click").GetProperty("normalized");
            var click = new NormalizedPoint(n[0].GetDouble(), n[1].GetDouble());

            // The frame and the control point exactly as in the six-clip baseline and the first experiment.
            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, m.GetProperty("t_marker").GetProperty("timeline_frame").GetInt64(), project.Settings.FrameRate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);
            var referenceTime = holds.Select(h => h.SourceTime).Distinct().Single();
            var frame = prepared.Clean;
            var control = prepared.ControlPoint;
            if (referenceTime != prepared.Clean.SourceTime)
            {
                frame = await frames.DecodeAsync(asset, referenceTime, prepared.Clean.Frame.Width, prepared.Clean.Frame.Height, default);
                Assert.Equal(referenceTime, frame.SourceTime);
                var atT = await frames.DecodeAsync(asset, prepared.Frames.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var refSmall = await frames.DecodeAsync(asset, referenceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var transfer = RouteSelectionTransfer.Transfer(GrayImage.FromBgra(refSmall.Frame), refSmall.SourceTime, GrayImage.FromBgra(atT.Frame), atT.SourceTime, click);
                Assert.True(transfer.Succeeded, transfer.Error);
                control = transfer.Point!.Value;
            }
            int w = frame.Frame.Width, h = frame.Frame.Height;
            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;

            var runs = new Dictionary<string, (RouteHoldSearch Search, long SearchMs, IReadOnlyList<(PointD Point, string Source)> Prompts)>();
            async Task Run(string key, double[] levels)
            {
                var watch = Stopwatch.StartNew();
                var (s, prompts) = await ResearchFindAsync(encoded, frame.Frame, control, search, split: true, levels);
                runs[key] = (s!, watch.ElapsedMilliseconds, prompts);
            }
            if (index++ % 2 == 0) { await Run("split", OneLevel); await Run("split2", TwoLevels); }
            else { await Run("split2", TwoLevels); await Run("split", OneLevel); }

            foreach (var variant in variants)
            {
                var key = variant.StartsWith("split2") ? "split2" : "split";
                var (s, searchMs, prompts) = runs[key];
                var gate = variant.Contains("chroma");
                var refChroma = Chroma(s.Reference);
                var watch = Stopwatch.StartNew();
                var pool = gate ? s.Candidates.Where(c => c.IsControl || Chroma(c.Surface) >= ChromaRatio * refChroma).ToList() : s.Candidates.ToList();
                var accepted = RouteHoldSelector.Select(pool, RouteHoldColor.DefaultTolerance);
                var selectMs = watch.Elapsed.TotalMilliseconds;
                var chromaRejected = gate ? s.Candidates.Where(c => !c.IsControl && Chroma(c.Surface) < ChromaRatio * refChroma).Select(c => c.Id).ToArray() : [];
                output.WriteLine($"{caseId} {variant}: {prompts.Count} prompts, {s.Candidates.Count} candidates, {accepted.Count} proposals; " +
                                 $"prepare {prepareMs} + encode {encodeMs} + search {searchMs} + select {selectMs:0} ms");
                results[variant].Add(new
                {
                    @case = caseId, frame_s = frame.SourceTime.Ticks / 1e7, clean_frame_s = prepared.Clean.SourceTime.Ticks / 1e7,
                    on_reference_frame = frame.SourceTime == referenceTime, frame = new[] { w, h },
                    timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs, search = searchMs, select = selectMs },
                    parameters = new
                    {
                        tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, grid = false,
                        split_levels = key == "split2" ? TwoLevels : OneLevel, large_region_factor = LargeRegionFactor,
                        chroma_ratio = gate ? ChromaRatio : (double?)null, reference_chroma = refChroma
                    },
                    reference = holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B },
                        contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                    runs = new[]
                    {
                        new
                        {
                            label = variant, control = new[] { control.X, control.Y }, search_ms = searchMs,
                            reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = prompts.Count,
                            prompt_points = prompts.Select(p => new { x = p.Point.X / w, y = p.Point.Y / h, source = p.Source }),
                            candidates = s.Candidates.Count,
                            predictions = accepted.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            all_candidates = s.Candidates.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                surface = new[] { c.Surface.L, c.Surface.A, c.Surface.B }, contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            chroma_rejected = chromaRejected
                        }
                    }
                });
            }
        }
        foreach (var v in variants)
        {
            Directory.CreateDirectory(Path.Combine(dir, v));
            await File.WriteAllTextAsync(Path.Combine(dir, v, "run.json"), JsonSerializer.Serialize(results[v]));
        }
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        output.WriteLine("reference snapshot unchanged: " + expectedHash);
    }

    /// <summary>
    /// Prompts inside large found masks, the full run: on the six reference frames (each encoded once), the one-level split
    /// search as before (re-run under the same conditions) and the same search where a prompt inside a found mask is
    /// skipped only when that mask is at most <see cref="LargeRegionFactor"/> × the control's area, alternating in order per
    /// case. Each selected as is, with the chroma gate 0.4 and with 0.3 (the gate is a selection on the same candidates — no
    /// model call). Writes six variants under <c>AIVE_NOSKIP_DIR</c>, in the format of the earlier experiments.
    /// </summary>
    [Fact]
    public async Task Run_prompts_inside_large_masks_on_the_six_reference_frames()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_NOSKIP_DIR");
        if (snapshot is null || dir is null)
        {
            output.WriteLine("SKIPPED: AIVE_BASELINE_SNAPSHOT / AIVE_NOSKIP_DIR not set");
            return;
        }
        (string Name, string Search, double? Gate)[] variants =
        [
            ("split-rerun", "split", null), ("split-chroma-rerun", "split", ChromaRatio), ("split-chroma03", "split", 0.3),
            ("noskip", "noskip", null), ("noskip-chroma", "noskip", ChromaRatio), ("noskip-chroma03", "noskip", 0.3)
        ];
        foreach (var v in variants)
            Assert.False(File.Exists(Path.Combine(dir, v.Name, "run.json")), $"{v.Name}/run.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        var (project, frames) = await OpenAsync(snapshot);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var results = variants.ToDictionary(v => v.Name, _ => new List<object>());
        var index = 0;
        foreach (var m in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = m.GetProperty("case").GetString()!;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var n = m.GetProperty("click").GetProperty("normalized");
            var click = new NormalizedPoint(n[0].GetDouble(), n[1].GetDouble());

            // The frame and the control point exactly as in the six-clip baseline and the earlier experiments.
            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, m.GetProperty("t_marker").GetProperty("timeline_frame").GetInt64(), project.Settings.FrameRate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);
            var referenceTime = holds.Select(h => h.SourceTime).Distinct().Single();
            var frame = prepared.Clean;
            var control = prepared.ControlPoint;
            if (referenceTime != prepared.Clean.SourceTime)
            {
                frame = await frames.DecodeAsync(asset, referenceTime, prepared.Clean.Frame.Width, prepared.Clean.Frame.Height, default);
                Assert.Equal(referenceTime, frame.SourceTime);
                var atT = await frames.DecodeAsync(asset, prepared.Frames.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var refSmall = await frames.DecodeAsync(asset, referenceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var transfer = RouteSelectionTransfer.Transfer(GrayImage.FromBgra(refSmall.Frame), refSmall.SourceTime, GrayImage.FromBgra(atT.Frame), atT.SourceTime, click);
                Assert.True(transfer.Succeeded, transfer.Error);
                control = transfer.Point!.Value;
            }
            int w = frame.Frame.Width, h = frame.Frame.Height;
            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;

            var runs = new Dictionary<string, (RouteHoldSearch Search, long SearchMs, IReadOnlyList<(PointD Point, string Source)> Prompts)>();
            async Task Run(string key, double? keep)
            {
                var watch = Stopwatch.StartNew();
                var (s, prompts) = await ResearchFindAsync(encoded, frame.Frame, control, search, split: true, OneLevel, keep);
                runs[key] = (s!, watch.ElapsedMilliseconds, prompts);
            }
            if (index++ % 2 == 0) { await Run("split", null); await Run("noskip", LargeRegionFactor); }
            else { await Run("noskip", LargeRegionFactor); await Run("split", null); }

            foreach (var (variant, key, gateRatio) in variants)
            {
                var (s, searchMs, prompts) = runs[key];
                var refChroma = Chroma(s.Reference);
                var watch = Stopwatch.StartNew();
                var pool = gateRatio is { } g ? s.Candidates.Where(c => c.IsControl || Chroma(c.Surface) >= g * refChroma).ToList() : s.Candidates.ToList();
                var accepted = RouteHoldSelector.Select(pool, RouteHoldColor.DefaultTolerance);
                var selectMs = watch.Elapsed.TotalMilliseconds;
                var chromaRejected = gateRatio is { } r ? s.Candidates.Where(c => !c.IsControl && Chroma(c.Surface) < r * refChroma).Select(c => c.Id).ToArray() : [];
                output.WriteLine($"{caseId} {variant}: {prompts.Count} prompts, {s.Candidates.Count} candidates, {accepted.Count} proposals; " +
                                 $"prepare {prepareMs} + encode {encodeMs} + search {searchMs} + select {selectMs:0} ms");
                results[variant].Add(new
                {
                    @case = caseId, frame_s = frame.SourceTime.Ticks / 1e7, clean_frame_s = prepared.Clean.SourceTime.Ticks / 1e7,
                    on_reference_frame = frame.SourceTime == referenceTime, frame = new[] { w, h },
                    timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs, search = searchMs, select = selectMs },
                    parameters = new
                    {
                        tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, grid = false,
                        split_levels = OneLevel, large_region_factor = LargeRegionFactor,
                        keep_prompts_in_masks_over = key == "noskip" ? LargeRegionFactor : (double?)null,
                        chroma_ratio = gateRatio, reference_chroma = refChroma
                    },
                    reference = holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B },
                        contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                    runs = new[]
                    {
                        new
                        {
                            label = variant, control = new[] { control.X, control.Y }, search_ms = searchMs,
                            reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = prompts.Count,
                            prompt_points = prompts.Select(p => new { x = p.Point.X / w, y = p.Point.Y / h, source = p.Source }),
                            candidates = s.Candidates.Count,
                            predictions = accepted.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            all_candidates = s.Candidates.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                surface = new[] { c.Surface.L, c.Surface.A, c.Surface.B }, contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            chroma_rejected = chromaRejected
                        }
                    }
                });
            }
        }
        foreach (var v in variants)
        {
            Directory.CreateDirectory(Path.Combine(dir, v.Name));
            await File.WriteAllTextAsync(Path.Combine(dir, v.Name, "run.json"), JsonSerializer.Serialize(results[v.Name]));
        }
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        output.WriteLine("reference snapshot unchanged: " + expectedHash);
    }

    private static async Task<(AiVideoEditor.Core.Entities.Project Project, RouteSelectionFrameSource Frames)> OpenAsync(string snapshot)
    {
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        return (project, frames);
    }

    private static List<List<int>> Regions(bool[] near, int mw, int mh)
    {
        var label = new bool[near.Length];
        var regions = new List<List<int>>();
        var queue = new Queue<int>();
        for (var start = 0; start < near.Length; start++)
        {
            if (!near[start] || label[start]) continue;
            var region = new List<int>();
            label[start] = true; queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue(); region.Add(i);
                int x = i % mw, y = i / mw;
                foreach (var j in new[] { x > 0 ? i - 1 : -1, x < mw - 1 ? i + 1 : -1, y > 0 ? i - mw : -1, y < mh - 1 ? i + mw : -1 })
                    if (j >= 0 && near[j] && !label[j]) { label[j] = true; queue.Enqueue(j); }
            }
            regions.Add(region);
        }
        return regions;
    }

    /// <summary>The block of the region farthest (chessboard) from its outside (the frame's edge outside too), the first in
    /// scan order on ties — as the planner.</summary>
    private static int Innermost(List<int> region, int mw, int mh)
    {
        var set = new HashSet<int>(region);
        var depth = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (var i in region)
        {
            int x = i % mw, y = i / mw;
            var edge = false;
            for (var dy = -1; dy <= 1 && !edge; dy++)
            for (var dx = -1; dx <= 1 && !edge; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= mw || ny >= mh || !set.Contains(ny * mw + nx)) edge = true;
            }
            if (edge) { depth[i] = 1; queue.Enqueue(i); }
        }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue(); int x = i % mw, y = i / mw;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= mw || ny >= mh) continue;
                var j = ny * mw + nx;
                if (set.Contains(j) && !depth.ContainsKey(j)) { depth[j] = depth[i] + 1; queue.Enqueue(j); }
            }
        }
        return region.OrderByDescending(i => depth[i]).ThenBy(i => i).First();
    }

    private static (double[] Distance, int Mw, int Mh, int Block) DistanceMap(DecodedFrame frame, OkLabColor reference)
    {
        var block = Math.Max(1, (int)Math.Round(frame.Width / (double)ColorPromptPlanner.MapWidth));
        int mw = Math.Max(1, frame.Width / block), mh = Math.Max(1, frame.Height / block);
        var distance = new double[mw * mh];
        var pixels = frame.Pixels.Span;
        for (var my = 0; my < mh; my++)
        for (var mx = 0; mx < mw; mx++)
        {
            long b = 0, g = 0, r = 0;
            for (var y = my * block; y < (my + 1) * block; y++)
            for (var x = mx * block; x < (mx + 1) * block; x++)
            {
                var i = y * frame.Stride + x * DecodedFrame.BytesPerPixel;
                b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
            }
            var n = (long)block * block;
            distance[my * mw + mx] = RouteHoldColor.Distance(RouteColor.SrgbToOkLab((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n)), reference);
        }
        return (distance, mw, mh, block);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
