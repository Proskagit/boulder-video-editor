using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
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
/// D048 stage 1, the baseline against the owner's reference marking (local, outside the solution). Reads a snapshot of the
/// reference project (<c>AIVE_BASELINE_DIR/reference-snapshot</c>, checked by its SHA-256 before and after — never written) and
/// the control clicks' manifest; for each case replays the recorded click through <see cref="RouteSelectionService"/> (the
/// clean frame and the control point must equal the manifest's) and runs <see cref="RouteHoldFinder"/> with its defaults,
/// timing the decode, the encoder and the search. A case whose reference holds were marked on another source frame than
/// the algorithm's clean frame is not compared: the camera motion between the two frames is measured instead. For the
/// cases in <c>AIVE_SENSITIVITY_CASES</c> the search is repeated on the same encoded clean frame with several of the owner's
/// own holds as the control (a point inside the hold), everything else the same. Writes <c>run.json</c>.
/// </summary>
public sealed class ReferenceBaselineTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [Fact]
    public async Task Run_the_baseline_against_the_reference_marking()
    {
        var dir = Environment.GetEnvironmentVariable("AIVE_BASELINE_DIR");
        if (dir is null || !File.Exists(Path.Combine(dir, "reference-snapshot", "project.json")))
        {
            output.WriteLine("SKIPPED: AIVE_BASELINE_DIR has no reference snapshot");
            return;
        }
        var snapshot = Path.Combine(dir, "reference-snapshot");
        var expectedHash = (await File.ReadAllTextAsync(Path.Combine(snapshot, "project.json.sha256"))).Trim();
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));
        var sensitivityCases = (Environment.GetEnvironmentVariable("AIVE_SENSITIVITY_CASES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

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

            // The recorded click replayed: the clean frame and the control point must be the manifest's.
            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            var expectedClean = m.GetProperty("clean_source").GetProperty("requested_ticks").GetInt64();
            Assert.Equal(expectedClean, prepared.Frames.CleanSourceTime.Ticks);
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);
            var cleanTime = prepared.Clean.SourceTime;
            var referenceTimes = holds.Select(h => h.SourceTime).Distinct().ToList();

            if (referenceTimes.Count != 1 || referenceTimes[0] != cleanTime)
            {
                // Not comparable as is: how far the camera moved between the algorithm's frame and the reference's frame.
                var motions = new List<object>();
                foreach (var time in referenceTimes)
                {
                    var a = await frames.DecodeAsync(asset, cleanTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                    var b = await frames.DecodeAsync(asset, time, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                    var estimator = new CameraMotionEstimator(GrayImage.FromBgra(a.Frame), a.SourceTime);
                    estimator.Add(GrayImage.FromBgra(b.Frame), b.SourceTime);
                    var sample = estimator.Build().Samples.Single(s => s.SourceTime == b.SourceTime);
                    double maxShift = 0;
                    foreach (var gx in new[] { 0.05, 0.5, 0.95 })
                    foreach (var gy in new[] { 0.05, 0.5, 0.95 })
                    {
                        var (x, y) = sample.ReferenceToFrame.Apply(gx, gy);
                        maxShift = Math.Max(maxShift, Math.Sqrt(Math.Pow((x - gx) * W, 2) + Math.Pow((y - gy) * H, 2)));
                    }
                    motions.Add(new { reference_frame_s = b.SourceTime.Ticks / 1e7, reliable = sample.IsReliable, max_shift_px_1920 = maxShift });
                }
                output.WriteLine($"{caseId}: NOT COMPARED — reference holds on {string.Join(", ", referenceTimes.Select(t => (t.Ticks / 1e7).ToString("0.000000")))} s, " +
                                 $"the algorithm's clean frame {cleanTime.Ticks / 1e7:0.000000} s; motion {JsonSerializer.Serialize(motions)}");
                results.Add(new { @case = caseId, comparable = false, clean_frame_s = cleanTime.Ticks / 1e7,
                    reference_frames_s = referenceTimes.Select(t => t.Ticks / 1e7), reference_holds = holds.Length, motions });
                continue;
            }

            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(prepared.Clean.Frame);
            var encodeMs = sw.ElapsedMilliseconds;
            async Task<object> Run(string label, NormalizedPoint control, Guid? fromHold)
            {
                var watch = Stopwatch.StartNew();
                var found = await RouteHoldFinder.FindAsync(encoded, prepared.Clean.Frame, control, ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty, search);
                var searchMs = watch.ElapsedMilliseconds;
                Assert.True(found.Search is not null, $"{caseId} {label}: {found.Error}");
                var accepted = found.Search!.Select(RouteHoldColor.DefaultTolerance);
                output.WriteLine($"{caseId} {label}: {accepted.Count} proposals at τ 0.08, search {searchMs} ms");
                return new
                {
                    label, control = new[] { control.X, control.Y }, control_from_reference_hold = fromHold, search_ms = searchMs,
                    reference_oklab = new[] { found.Search.Reference.L, found.Search.Reference.A, found.Search.Reference.B },
                    prompts = found.Search.Prompts, candidates = found.Search.Candidates.Count,
                    predictions = accepted.Select(c => new { is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                        contour = c.Contour.Select(p => new[] { p.X, p.Y }) })
                };
            }

            var runs = new List<object> { await Run("baseline (manifest click)", prepared.ControlPoint, null) };
            if (sensitivityCases.Contains(caseId))
            {
                // The owner's own holds as controls: varied in size and in colour (their stored surface sample against the
                // median of the reference set), each clicked at its most interior point.
                var median = new OkLabColor(Median(holds.Select(h => h.SurfaceLab.L)), Median(holds.Select(h => h.SurfaceLab.A)), Median(holds.Select(h => h.SurfaceLab.B)));
                var picks = new List<(string Why, Hold Hold)>
                {
                    ("smallest reference hold", holds.MinBy(Area)!),
                    ("largest reference hold", holds.MaxBy(Area)!),
                    ("colour nearest the set's median", holds.MinBy(h => RouteHoldColor.Distance(h.SurfaceLab, median))!),
                    ("colour farthest from the set's median", holds.MaxBy(h => RouteHoldColor.Distance(h.SurfaceLab, median))!)
                };
                foreach (var (why, hold) in picks.DistinctBy(p => p.Hold.Id))
                    runs.Add(await Run(why, Interior(hold.Points), hold.Id));
            }

            results.Add(new
            {
                @case = caseId, comparable = true, clean_frame_s = cleanTime.Ticks / 1e7, frame = new[] { prepared.Clean.Frame.Width, prepared.Clean.Frame.Height },
                timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs },
                reference = holds.Select(h => new { id = h.Id, surface = new[] { h.SurfaceLab.L, h.SurfaceLab.A, h.SurfaceLab.B },
                    contour = h.Points.Select(p => new[] { p.X, p.Y }) }),
                runs
            });
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(results));
        Assert.Equal(expectedHash, Sha256(Path.Combine(snapshot, "project.json")));       // the reference never written
        output.WriteLine("reference snapshot unchanged: " + expectedHash);
    }

    private static double Area(Hold hold)
    {
        double a = 0;
        for (int i = 0, j = hold.Points.Length - 1; i < hold.Points.Length; j = i++)
            a += (hold.Points[j].X * W + hold.Points[i].X * W) * (hold.Points[j].Y * H - hold.Points[i].Y * H);
        return Math.Abs(a / 2);
    }

    /// <summary>The point of the polygon farthest from its outline (on a 1 px grid at 1920 × 1080): a click well inside the hold.</summary>
    private static NormalizedPoint Interior(ImmutableArray<NormalizedPoint> polygon)
    {
        var pts = polygon.Select(p => (X: p.X * W, Y: p.Y * H)).ToArray();
        int x0 = (int)pts.Min(p => p.X), x1 = (int)Math.Ceiling(pts.Max(p => p.X)), y0 = (int)pts.Min(p => p.Y), y1 = (int)Math.Ceiling(pts.Max(p => p.Y));
        (double X, double Y) best = (pts.Average(p => p.X), pts.Average(p => p.Y));
        var bestDistance = -1.0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            if (!Inside(pts, x + 0.5, y + 0.5)) continue;
            var d = double.MaxValue;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++) d = Math.Min(d, SegmentDistance(pts[j], pts[i], (x + 0.5, y + 0.5)));
            if (d > bestDistance) (bestDistance, best) = (d, (x + 0.5, y + 0.5));
        }
        return new NormalizedPoint(best.X / W, best.Y / H);
    }

    private static bool Inside((double X, double Y)[] poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    private static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        return Math.Sqrt(Math.Pow(a.X + t * dx - p.X, 2) + Math.Pow(a.Y + t * dy - p.Y, 2));
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
