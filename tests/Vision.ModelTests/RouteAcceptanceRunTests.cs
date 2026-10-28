using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage 1, the owner's manual acceptance (local, outside the solution): the unchanged route search
/// (<see cref="RouteHoldFinder"/> with its defaults — τ 0.08, score ≥ 0.5, colour map 0.10, the D047 grid 15×15 — and the
/// same without the grid) on the owner's clean frames, the frame at the D047 size 1920×1080 as the app decodes it. The
/// control hold — the app's user clicks one clearly visible hold of the route — is, among the owner's arrow targets whose
/// D034 mask is hold-sized (≤ 2 % of the frame) and does not touch the frame's edge, the one with the median area. For every arrow target the model's mask at that point is written too (no size rule: the object the arrow
/// points at), so the evaluation can group several arrows on one hold. Inputs: <c>AIVE_ACCEPT_FRAMES</c> (the clean
/// frames), <c>AIVE_ACCEPT_OUT</c> (<c>arrows.json</c> in, <c>run.json</c> out). Nothing of the algorithm is changed here.
/// </summary>
public sealed class RouteAcceptanceRunTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [Fact]
    public async Task Run_the_route_search_on_the_owners_clean_frames()
    {
        var frames = Environment.GetEnvironmentVariable("AIVE_ACCEPT_FRAMES");
        var outDir = Environment.GetEnvironmentVariable("AIVE_ACCEPT_OUT");
        if (frames is null || outDir is null)
        {
            output.WriteLine("SKIPPED: AIVE_ACCEPT_FRAMES / AIVE_ACCEPT_OUT not set");
            return;
        }
        using var arrows = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "arrows.json")));
        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var status = await service.EnsureReadyAsync();
        Assert.True(status.IsReady, status.Message);
        output.WriteLine($"device {service.Device}");

        var results = new Dictionary<string, object>();
        foreach (var entry in arrows.RootElement.EnumerateObject())
        {
            var clean = entry.Value.GetProperty("clean").GetString()!;
            var frame = Decode(Path.Combine(frames, clean));
            var sw = Stopwatch.StartNew();
            using var encoded = await service.PrepareFrameAsync(frame);
            var encodeMs = sw.ElapsedMilliseconds;

            // The owner's arrow targets at 1920 × 1080 and the model's mask at each.
            var targets = new List<object>();
            var controlCandidates = new List<(int Id, NormalizedPoint Point, long Area)>();
            foreach (var a in entry.Value.GetProperty("arrows").EnumerateArray())
            {
                var t = a.GetProperty("target");
                var p = new PointD(t[0].GetDouble() / 2, t[1].GetDouble() / 2);
                var set = await encoded.PredictAsync(new[] { p });
                var best = set.Masks.OrderByDescending(m => m.Score).First();
                var (component, area) = MaskContour.LargestComponent(best.Pixels, W, H);
                var offered = HoldCandidateRules.Choose(set) is { } k ? set.Masks[k] : null;
                var id = a.GetProperty("id").GetInt32();
                long offeredArea = 0;
                var inside = false;
                if (offered is not null)
                {
                    var (c, oa) = MaskContour.LargestComponent(offered.Pixels, W, H);
                    offeredArea = oa;
                    inside = MaskCrop.From(c, W, H) is { } crop && crop.X > 0 && crop.Y > 0 && crop.X + crop.Width < W && crop.Y + crop.Height < H;
                }
                targets.Add(new { id, x = p.X, y = p.Y, mask_score = best.Score, mask_area = area, offered = offered is not null,
                    offered_area = offeredArea, offered_inside = inside, contour = Contour(MaskContour.Outer(component, W, H)) });
                if (offered is not null && inside && offeredArea <= 0.02 * W * H)
                    controlCandidates.Add((id, new NormalizedPoint(p.X / W, p.Y / H), offeredArea));
            }
            var byArea = controlCandidates.OrderBy(c => c.Area).ThenBy(c => c.Id).ToList();
            var control = byArea[(byArea.Count - 1) / 2];

            var runs = new Dictionary<string, object>();
            foreach (var (name, options) in new[] { ("grid", new RouteHoldSearchOptions()), ("colour_only", new RouteHoldSearchOptions { UseGrid = false }) })
            {
                sw.Restart();
                var found = await RouteHoldFinder.FindAsync(encoded, frame, control.Point, Array.Empty<ImmutableArray<NormalizedPoint>>(), options);
                var ms = sw.ElapsedMilliseconds;
                Assert.True(found.Search is not null, found.Error);
                var search = found.Search!;
                runs[name] = new
                {
                    search_ms = ms, prompts = search.Prompts, candidates = search.Candidates.Count,
                    reference = new[] { search.Reference.L, search.Reference.A, search.Reference.B },
                    counts = new[] { 0.06, 0.08, 0.10 }.ToDictionary(t => t.ToString("0.00"), t => search.Select(t).Count),
                    accepted = search.Select(RouteHoldColor.DefaultTolerance).Select(c => new
                    {
                        is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                        cx = c.Centroid.X * W, cy = c.Centroid.Y * H, contour = Contour(c.Contour)
                    }).ToList()
                };
                output.WriteLine($"{entry.Name} {name}: {search.Prompts} prompts, {search.Candidates.Count} candidates, " +
                                 $"{search.Select(RouteHoldColor.DefaultTolerance).Count} at τ 0.08, {ms} ms");
            }
            // Sensitivity to the user's choice of the control hold: colour prompts only, with the smallest and the largest
            // eligible hold as the control (the median one is the run above).
            var sensitivity = new Dictionary<string, object>();
            foreach (var (label, pick) in new[] { ("smallest", byArea[0]), ("largest", byArea[^1]) })
            {
                var found = await RouteHoldFinder.FindAsync(encoded, frame, pick.Point, Array.Empty<ImmutableArray<NormalizedPoint>>(),
                    new RouteHoldSearchOptions { UseGrid = false });
                sensitivity[label] = new
                {
                    control_arrow = pick.Id,
                    accepted = found.Search!.Select(RouteHoldColor.DefaultTolerance).Select(c => new
                    {
                        is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                        cx = c.Centroid.X * W, cy = c.Centroid.Y * H, contour = Contour(c.Contour)
                    }).ToList()
                };
            }
            results[entry.Name] = new { clean, encode_ms = encodeMs, control_arrow = control.Id, targets, runs, sensitivity };
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "run.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = false }));
    }

    private static double[][] Contour(ImmutableArray<NormalizedPoint> contour) =>
        contour.Select(p => new[] { Math.Round(p.X * W, 2), Math.Round(p.Y * H, 2) }).ToArray();

    /// <summary>The clean PNG at 1920 × 1080 BGRA through FFmpeg (the app's decoder scales the same way).</summary>
    private static DecodedFrame Decode(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -vf scale={W}:{H} -f rawvideo -pix_fmt bgra -")
            { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(psi)!;
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();
        var pixels = ms.ToArray();
        Assert.Equal(W * H * 4, pixels.Length);
        return new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }
}
