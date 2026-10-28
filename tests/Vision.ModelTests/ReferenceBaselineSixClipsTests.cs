using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage 1, the six-clip baseline (local, outside the solution; research only). Like <see cref="ReferenceBaselineTests"/>
/// — the same reference snapshot (<c>AIVE_BASELINE_SNAPSHOT</c>, checked by its SHA-256 before and after, never written), the
/// same manifest clicks and <see cref="RouteHoldFinder"/> defaults — but every case runs on the source frame its reference
/// holds were marked on. The recorded click is replayed through <see cref="RouteSelectionService"/> first (clean frame and
/// control point must equal the manifest's). When the reference frame differs from the clean frame, it is decoded the same
/// way as the clean frame (the last frame at or before its time, inside 1920 × 1080; its time must equal the holds' time) and
/// the click is transferred from <c>T</c> to it by the same <see cref="RouteSelectionTransfer"/> the app uses for the clean
/// frame; the camera motion clean → reference is measured too. Besides the τ 0.08 proposals, every candidate is written
/// (surface, distance, score, contour) and the selector's output at several τ, for analysis without the model. Writes
/// <c>run.json</c> into <c>AIVE_BASELINE6_DIR</c>.
/// </summary>
public sealed class ReferenceBaselineSixClipsTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;
    private static readonly double[] Tolerances = [0.06, 0.08, 0.10, 0.12, 0.14];

    [Fact]
    public async Task Run_the_baseline_on_the_reference_frames_of_all_six_clips()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE6_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(snapshot, "project.json")))
        {
            output.WriteLine("SKIPPED: AIVE_BASELINE_SNAPSHOT / AIVE_BASELINE6_DIR not set");
            return;
        }
        Directory.CreateDirectory(dir);
        Assert.False(File.Exists(Path.Combine(dir, "run.json")), "run.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var project = await projects.OpenAsync(snapshot);
        var rate = project.Settings.FrameRate;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var frames = new RouteSelectionFrameSource(decoder);
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var results = new List<object>();
        foreach (var m in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = m.GetProperty("case").GetString()!;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty;
            var tFrame = m.GetProperty("t_marker").GetProperty("timeline_frame").GetInt64();
            var n = m.GetProperty("click").GetProperty("normalized");
            var click = new NormalizedPoint(n[0].GetDouble(), n[1].GetDouble());

            // 1. The recorded click replayed, exactly as in the app (timed: the product's decode + transfer).
            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            Assert.Equal(m.GetProperty("clean_source").GetProperty("requested_ticks").GetInt64(), prepared.Frames.CleanSourceTime.Ticks);
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);

            // 2. The reference frame: the one time all of the case's holds carry.
            var referenceTimes = holds.Select(h => h.SourceTime).Distinct().ToList();
            Assert.Single(referenceTimes);
            var referenceTime = referenceTimes[0];
            var cleanTime = prepared.Clean.SourceTime;
            var frame = prepared.Clean;
            var control = prepared.ControlPoint;
            object? substitution = null;
            long referenceDecodeMs = 0;
            if (referenceTime != cleanTime)
            {
                sw.Restart();
                frame = await frames.DecodeAsync(asset, referenceTime, prepared.Clean.Frame.Width, prepared.Clean.Frame.Height, default);
                Assert.Equal(referenceTime, frame.SourceTime);                    // exactly the reference's frame, no neighbour
                Assert.Equal(prepared.Clean.Frame.Width, frame.Frame.Width);
                Assert.Equal(prepared.Clean.Frame.Height, frame.Frame.Height);

                // The click transferred from T to the reference frame by the app's own transfer (same analysis width).
                var atT = await frames.DecodeAsync(asset, prepared.Frames.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var refSmall = await frames.DecodeAsync(asset, referenceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var transfer = RouteSelectionTransfer.Transfer(GrayImage.FromBgra(refSmall.Frame), refSmall.SourceTime, GrayImage.FromBgra(atT.Frame), atT.SourceTime, click);
                Assert.True(transfer.Succeeded, $"{caseId}: transfer to the reference frame failed: {transfer.Error}");
                control = transfer.Point!.Value;
                referenceDecodeMs = sw.ElapsedMilliseconds;

                // The camera motion between the algorithm's clean frame and the reference frame (as measured before).
                var cleanSmall = await frames.DecodeAsync(asset, cleanTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var estimator = new CameraMotionEstimator(GrayImage.FromBgra(cleanSmall.Frame), cleanSmall.SourceTime);
                estimator.Add(GrayImage.FromBgra(refSmall.Frame), refSmall.SourceTime);
                var sample = estimator.Build().Samples.Single(s => s.SourceTime == refSmall.SourceTime);
                double maxShift = 0;
                foreach (var gx in new[] { 0.05, 0.5, 0.95 })
                foreach (var gy in new[] { 0.05, 0.5, 0.95 })
                {
                    var (x, y) = sample.ReferenceToFrame.Apply(gx, gy);
                    maxShift = Math.Max(maxShift, Math.Sqrt(Math.Pow((x - gx) * W, 2) + Math.Pow((y - gy) * H, 2)));
                }
                var (cx, cy) = sample.ReferenceToFrame.Apply(prepared.ControlPoint.X, prepared.ControlPoint.Y);
                substitution = new
                {
                    clean_frame_s = cleanTime.Ticks / 1e7, reference_frame_s = referenceTime.Ticks / 1e7,
                    motion_reliable = sample.IsReliable, motion_max_shift_px_1920 = maxShift,
                    control_on_clean = new[] { prepared.ControlPoint.X, prepared.ControlPoint.Y },
                    control_on_reference = new[] { control.X, control.Y },
                    control_clean_moved_by_motion = new[] { cx, cy },
                    control_shift_px_1920 = Math.Sqrt(Math.Pow((control.X - prepared.ControlPoint.X) * W, 2) + Math.Pow((control.Y - prepared.ControlPoint.Y) * H, 2)),
                    research_decode_and_transfer_ms = referenceDecodeMs
                };
                output.WriteLine($"{caseId}: reference frame {referenceTime.Ticks / 1e7:0.000000} s (clean {cleanTime.Ticks / 1e7:0.000000} s), " +
                                 $"motion ≤ {maxShift:0.000} px (reliable {sample.IsReliable}), control moved {JsonSerializer.Serialize(substitution)}");
            }

            // 3. Encoder and search on that frame, defaults.
            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var found = await RouteHoldFinder.FindAsync(encoded, frame.Frame, control, ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty, search);
            var searchMs = sw.ElapsedMilliseconds;
            Assert.True(found.Search is not null, $"{caseId}: {found.Error}");
            var s = found.Search!;
            var selections = Tolerances.ToDictionary(t => t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                t => s.Select(t).Select(c => c.Id).ToArray());
            var accepted = s.Select(RouteHoldColor.DefaultTolerance);
            output.WriteLine($"{caseId}: {accepted.Count} proposals at τ 0.08 of {s.Candidates.Count} candidates, {s.Prompts} prompts; " +
                             $"prepare {prepareMs} ms, encode {encodeMs} ms, search {searchMs} ms");

            results.Add(new
            {
                @case = caseId, comparable = true, frame_s = frame.SourceTime.Ticks / 1e7, clean_frame_s = cleanTime.Ticks / 1e7,
                on_reference_frame = referenceTime == frame.SourceTime, substitution,
                frame = new[] { frame.Frame.Width, frame.Frame.Height },
                timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs, search = searchMs },
                parameters = new { tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, search.UseGrid, search.GridDivisions, search.NmsIouThreshold },
                reference = holds.Select(h => new { id = h.Id, surface = new[] { h.SurfaceLab.L, h.SurfaceLab.A, h.SurfaceLab.B },
                    contour = h.Points.Select(p => new[] { p.X, p.Y }) }),
                runs = new[]
                {
                    new
                    {
                        label = "baseline (manifest click)", control = new[] { control.X, control.Y }, search_ms = searchMs,
                        reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = s.Prompts, candidates = s.Candidates.Count,
                        predictions = accepted.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                            contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                        all_candidates = s.Candidates.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                            surface = new[] { c.Surface.L, c.Surface.A, c.Surface.B }, contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                        selected_by_tau = selections
                    }
                }
            });
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(results));
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));       // the reference never written
        output.WriteLine("reference snapshot unchanged: " + expectedHash);
    }

    /// <summary>
    /// Where the prompts fall (no model): on each case's reference frame, the colour-map prompts of the run's reference
    /// colour (read from <c>run.json</c>) as the finder plans them (at most <see cref="ColorPromptPlanner.MaxPrompts"/>) and
    /// without that cap, and the D047 grid; for each reference hold, whether any prompt of each kind is inside its contour.
    /// Writes <c>prompts.json</c>.
    /// </summary>
    [Fact]
    public async Task Locate_the_prompts_on_the_reference_holds()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE6_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(dir, "run.json")))
        {
            output.WriteLine("SKIPPED: no six-clip run.json");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "prompts.json")), "prompts.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        var search = new RouteHoldSearchOptions();

        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var size = c.GetProperty("frame");
            var frame = await frames.DecodeAsync(asset, holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default);
            Assert.Equal(holds[0].SourceTime, frame.SourceTime);
            var r = c.GetProperty("runs")[0].GetProperty("reference_oklab");
            var reference = new OkLabColor(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble());
            int w = frame.Frame.Width, h = frame.Frame.Height;

            var capped = ColorPromptPlanner.Plan(frame.Frame, reference, search.MapThreshold, maxPrompts: search.MaxColorPrompts);
            var all = ColorPromptPlanner.Plan(frame.Frame, reference, search.MapThreshold, maxPrompts: int.MaxValue);
            var grid = HoldProposalService.GridPoints(w, h, search.GridDivisions);
            bool AnyInside(IEnumerable<PointD> points, Hold hold) => points.Any(p => Inside(hold.Points, p.X / w, p.Y / h));
            int? Rank(Hold hold)
            {
                for (var k = 0; k < all.Count; k++) if (Inside(hold.Points, all[k].X / w, all[k].Y / h)) return k;
                return null;
            }
            results.Add(new
            {
                @case = caseId, regions = all.Count, colour_prompts = capped.Count, grid_prompts = grid.Count,
                holds = holds.Select(hold => new
                {
                    id = hold.Id, colour_prompt_capped = AnyInside(capped, hold), colour_prompt_uncapped = AnyInside(all, hold),
                    first_region_rank = Rank(hold), grid_prompt = AnyInside(grid, hold)
                })
            });
            output.WriteLine($"{caseId}: {all.Count} colour regions ({capped.Count} prompted), " +
                             $"holds with a colour prompt {holds.Count(x => AnyInside(capped, x))} / uncapped {holds.Count(x => AnyInside(all, x))} / grid {holds.Count(x => AnyInside(grid, x))} of {holds.Length}");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "prompts.json"), JsonSerializer.Serialize(results));
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
    }

    /// <summary>
    /// Why a hold gets no colour prompt (no model): the colour map rebuilt here exactly as <see cref="ColorPromptPlanner"/>
    /// builds it (blocks of frame width / 480, the block's mean sRGB to OKLab, near = distance &lt; 0.10, 4-connected
    /// regions). For each reference hold: its blocks (centre inside the contour), how many are near, and the area of the
    /// largest region those near blocks belong to — a region much larger than the hold means the hold merged with its
    /// surroundings and shares their single prompt. Writes <c>prompt-regions.json</c>.
    /// </summary>
    [Fact]
    public async Task Explain_the_colour_map_on_the_reference_holds()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE6_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(dir, "run.json")))
        {
            output.WriteLine("SKIPPED: no six-clip run.json");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "prompt-regions.json")), "prompt-regions.json exists — results are never overwritten");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));

        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var size = c.GetProperty("frame");
            var decoded = await frames.DecodeAsync(asset, holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default);
            Assert.Equal(holds[0].SourceTime, decoded.SourceTime);
            var frame = decoded.Frame;
            var r = c.GetProperty("runs")[0].GetProperty("reference_oklab");
            var reference = new OkLabColor(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble());

            var (near, mw, mh, block) = NearMap(frame, reference);
            var label = new int[mw * mh];
            var areas = new List<int> { 0 };
            var queue = new Queue<int>();
            for (var start = 0; start < near.Length; start++)
            {
                if (!near[start] || label[start] != 0) continue;
                var id = areas.Count; var area = 0;
                label[start] = id; queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var i = queue.Dequeue(); area++;
                    int x = i % mw, y = i / mw;
                    foreach (var j in new[] { x > 0 ? i - 1 : -1, x < mw - 1 ? i + 1 : -1, y > 0 ? i - mw : -1, y < mh - 1 ? i + mw : -1 })
                        if (j >= 0 && near[j] && label[j] == 0) { label[j] = id; queue.Enqueue(j); }
                }
                areas.Add(area);
            }

            results.Add(new
            {
                @case = caseId, block, map = new[] { mw, mh }, regions = areas.Count - 1,
                holds = holds.Select(hold =>
                {
                    var inside = new List<int>();
                    for (var my = 0; my < mh; my++)
                    for (var mx = 0; mx < mw; mx++)
                        if (Inside(hold.Points, (mx + 0.5) * block / frame.Width, (my + 0.5) * block / frame.Height)) inside.Add(my * mw + mx);
                    var nearInside = inside.Where(i => near[i]).ToList();
                    var regionAreas = nearInside.Select(i => label[i]).Distinct().Select(id => areas[id]).OrderByDescending(a => a).ToList();
                    return new
                    {
                        id = hold.Id, blocks = inside.Count, near_blocks = nearInside.Count,
                        regions = regionAreas.Count, largest_region_blocks = regionAreas.FirstOrDefault()
                    };
                })
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "prompt-regions.json"), JsonSerializer.Serialize(results));
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
    }

    /// <summary>
    /// Where the search time goes (research profile): on each case's reference frame, the encoder once and, for the D047
    /// grid points (the same 225 prompts on every frame), each step <see cref="RouteHoldFinder"/> takes per prompt — the
    /// model's mask decoder, the D034 candidate rule with the largest component, outline and crop, and the surface colour
    /// (frame-size mask + <see cref="HoldColorSampler"/>). The finder's own comparisons with the candidates found so far are
    /// the rest of its time. Writes <c>profile.json</c>.
    /// </summary>
    [Fact]
    public async Task Profile_the_search_per_prompt()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE6_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(dir, "run.json")))
        {
            output.WriteLine("SKIPPED: no six-clip run.json");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "profile.json")), "profile.json exists — results are never overwritten");
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var size = c.GetProperty("frame");
            var decoded = await frames.DecodeAsync(asset, clip.HoldMarkup!.Holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default);
            var frame = decoded.Frame;
            int w = frame.Width, h = frame.Height;

            var sw = Stopwatch.StartNew();
            using var encoded = await model.PrepareFrameAsync(frame);
            var encodeMs = sw.Elapsed.TotalMilliseconds;
            var grid = HoldProposalService.GridPoints(w, h, search.GridDivisions);
            double decoderMs = 0, ruleMs = 0, surfaceMs = 0;
            var masks = 0;
            foreach (var p in grid)
            {
                sw.Restart();
                var set = await encoded.PredictAsync(new[] { p }, default);
                decoderMs += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                MaskCrop? crop = null;
                if (HoldCandidateRules.Choose(set) is { } k && set.Masks[k].Width == w && set.Masks[k].Height == h)
                {
                    var (component, _) = MaskContour.LargestComponent(set.Masks[k].Pixels, w, h);
                    var contour = MaskContour.Outer(component, w, h);
                    if (contour.Length >= HoldMarkupRules.MinPoints) crop = MaskCrop.From(component, w, h);
                }
                ruleMs += sw.Elapsed.TotalMilliseconds;
                if (crop is null) continue;
                masks++;
                sw.Restart();
                HoldColorSampler.Sample(frame, crop.ToFrameMask(), Array.Empty<IReadOnlyList<NormalizedPoint>>());
                surfaceMs += sw.Elapsed.TotalMilliseconds;
            }
            var timing = c.GetProperty("timing_ms");
            var prompts = c.GetProperty("runs")[0].GetProperty("prompts").GetInt32();
            var perPrompt = (decoderMs + ruleMs + surfaceMs) / grid.Count;
            results.Add(new
            {
                @case = caseId, encode_ms = encodeMs, grid_prompts = grid.Count, masks,
                per_prompt_ms = new { decoder = decoderMs / grid.Count, candidate_rule = ruleMs / grid.Count, surface = surfaceMs / Math.Max(1, masks), total = perPrompt },
                run_search_ms = timing.GetProperty("search").GetInt64(), run_prompts = prompts,
                run_search_per_prompt_ms = timing.GetProperty("search").GetInt64() / (double)prompts
            });
            output.WriteLine($"{caseId}: encode {encodeMs:0} ms; per grid prompt decoder {decoderMs / grid.Count:0.0} ms, rule {ruleMs / grid.Count:0.0} ms, " +
                             $"surface {surfaceMs / Math.Max(1, masks):0.0} ms per mask ({masks} masks); the run: {timing.GetProperty("search").GetInt64() / (double)prompts:0.0} ms per prompt");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "profile.json"), JsonSerializer.Serialize(results));
    }

    /// <summary>
    /// Offline estimate of one hypothesis (no model, nothing in the app changed): a colour-map region much larger than the
    /// control hold is split again at a stricter distance, each part gets its own prompt (its innermost block), the rest is
    /// the planner's rule (largest first, at most 300), with or without the grid. For each variant: the prompts and how many
    /// of the evaluated reference holds (all but the control's) have a prompt inside. Writes <c>split-coverage.json</c>.
    /// </summary>
    [Fact]
    public async Task Estimate_prompt_coverage_with_split_regions()
    {
        var snapshot = Environment.GetEnvironmentVariable("AIVE_BASELINE_SNAPSHOT");
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE6_DIR");
        if (snapshot is null || dir is null || !File.Exists(Path.Combine(dir, "run.json")))
        {
            output.WriteLine("SKIPPED: no six-clip run.json");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "split-coverage.json")), "split-coverage.json exists — results are never overwritten");
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(snapshot);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        var search = new RouteHoldSearchOptions();

        var results = new List<object>();
        foreach (var c in run.RootElement.EnumerateArray())
        {
            var caseId = c.GetProperty("case").GetString()!;
            var m = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("case").GetString() == caseId);
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var size = c.GetProperty("frame");
            var frame = (await frames.DecodeAsync(asset, holds[0].SourceTime, size[0].GetInt32(), size[1].GetInt32(), default)).Frame;
            var run0 = c.GetProperty("runs")[0];
            var r = run0.GetProperty("reference_oklab");
            var reference = new OkLabColor(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble());
            var controlArea = run0.GetProperty("all_candidates").EnumerateArray().Single(x => x.GetProperty("is_control").GetBoolean()).GetProperty("area").GetDouble();
            var cp = run0.GetProperty("control");
            var control = new NormalizedPoint(cp[0].GetDouble(), cp[1].GetDouble());
            var evaluated = holds.Where(hold => !Inside(hold.Points, control.X, control.Y)).ToList();
            var (distance, mw, mh, block) = DistanceMap(frame, reference);
            var controlBlocks = controlArea * mw * mh;
            var grid = HoldProposalService.GridPoints(frame.Width, frame.Height, search.GridDivisions);

            var variants = new List<object>();
            foreach (var split in new double?[] { null, 0.06, 0.05, 0.04, 0.03 })
            foreach (var factor in split is null ? new[] { 0.0 } : new[] { 4.0, 10.0 })
            foreach (var useGrid in new[] { true, false })
            {
                var prompts = SplitPrompts(distance, mw, mh, block, search.MapThreshold, split, factor * controlBlocks, search.MaxColorPrompts);
                var all = useGrid ? prompts.Concat(grid).ToList() : prompts;
                var covered = evaluated.Count(hold => all.Any(p => Inside(hold.Points, p.X / frame.Width, p.Y / frame.Height)));
                variants.Add(new { split, large_region_factor = factor, grid = useGrid, prompts = all.Count, covered, evaluated = evaluated.Count });
                output.WriteLine($"{caseId}: split {split?.ToString("0.00") ?? "none"} ×{factor} grid {useGrid}: {all.Count} prompts, {covered}/{evaluated.Count} holds prompted");
            }
            results.Add(new { @case = caseId, control_blocks = controlBlocks, variants });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "split-coverage.json"), JsonSerializer.Serialize(results));
    }

    /// <summary>The planner's regions and prompts (innermost block, largest regions first, capped), with a region larger
    /// than <paramref name="largeBlocks"/> split again at <paramref name="split"/> when given.</summary>
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
        return regions.OrderByDescending(x => x.Area).Take(max).Select(x => new PointD((x.Best % mw + 0.5) * block, (x.Best / mw + 0.5) * block)).ToList();
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

    /// <summary>The block of the region farthest (chessboard) from its outside, the first such on ties — as the planner.</summary>
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

    private static (double[] Distance, int Mw, int Mh, int Block) DistanceMap(AiVideoEditor.Core.Playback.DecodedFrame frame, OkLabColor reference)
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
                var i = y * frame.Stride + x * AiVideoEditor.Core.Playback.DecodedFrame.BytesPerPixel;
                b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
            }
            var n = (long)block * block;
            distance[my * mw + mx] = RouteHoldColor.Distance(RouteColor.SrgbToOkLab((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n)), reference);
        }
        return (distance, mw, mh, block);
    }

    /// <summary>The planner's map, as in <see cref="ColorPromptPlanner.Plan"/>.</summary>
    private static (bool[] Near, int Mw, int Mh, int Block) NearMap(AiVideoEditor.Core.Playback.DecodedFrame frame, OkLabColor reference)
    {
        var block = Math.Max(1, (int)Math.Round(frame.Width / (double)ColorPromptPlanner.MapWidth));
        int mw = Math.Max(1, frame.Width / block), mh = Math.Max(1, frame.Height / block);
        var near = new bool[mw * mh];
        var pixels = frame.Pixels.Span;
        for (var my = 0; my < mh; my++)
        for (var mx = 0; mx < mw; mx++)
        {
            long b = 0, g = 0, r = 0;
            for (var y = my * block; y < (my + 1) * block; y++)
            for (var x = mx * block; x < (mx + 1) * block; x++)
            {
                var i = y * frame.Stride + x * AiVideoEditor.Core.Playback.DecodedFrame.BytesPerPixel;
                b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
            }
            var n = (long)block * block;
            var lab = RouteColor.SrgbToOkLab((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
            near[my * mw + mx] = RouteHoldColor.Distance(lab, reference) < ColorPromptPlanner.MapThreshold;
        }
        return (near, mw, mh, block);
    }

    private static bool Inside(ImmutableArray<NormalizedPoint> poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
