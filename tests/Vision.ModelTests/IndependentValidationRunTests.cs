using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
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
/// D048 independent validation pilot (<c>artifacts/d048-colour-audit/independent-validation/run/FROZEN.md</c>): one
/// search per frame. The frame at the reference holds' source time (asserted equal in ticks), the pre-registered control
/// point (<c>controls.json</c>), the frozen research search <c>ReferenceSplitExperimentTests.ResearchFindAsync</c>
/// (<c>split: true</c>), called unchanged by reflection; the whole candidate pool is saved for the offline variants V0–V3.
/// Inputs: <c>AIVE_IV_PROJECT</c>, <c>AIVE_IV_CONTROLS</c>, <c>AIVE_IV_REFERENCE_SHA</c>; output <c>AIVE_IV_RUN</c>.
/// The project is opened, never saved.
/// </summary>
public sealed class IndependentValidationRunTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [Fact]
    public async Task Run_one_search_per_frame()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_IV_PROJECT");
        var controlsPath = Environment.GetEnvironmentVariable("AIVE_IV_CONTROLS");
        var expectedHash = Environment.GetEnvironmentVariable("AIVE_IV_REFERENCE_SHA");
        var dir = Environment.GetEnvironmentVariable("AIVE_IV_RUN");
        if (folder is null || controlsPath is null || expectedHash is null || dir is null)
        {
            output.WriteLine("SKIPPED: AIVE_IV_PROJECT / AIVE_IV_CONTROLS / AIVE_IV_REFERENCE_SHA / AIVE_IV_RUN not set");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "pool.json")), "pool.json exists — results are never overwritten");
        Assert.Equal(expectedHash, Sha256(Path.Combine(folder, "project.json")));
        var research = typeof(ReferenceSplitExperimentTests).GetMethod("ResearchFindAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        using var controls = JsonDocument.Parse(await File.ReadAllTextAsync(controlsPath));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(folder);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();
        Directory.CreateDirectory(Path.Combine(dir, "frames"));

        var results = new List<object>();
        foreach (var c in controls.RootElement.EnumerateObject())
        {
            var caseId = c.Name; var v = c.Value;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == v.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup!.Holds;
            var time = Assert.Single(holds.Select(h => h.SourceTime).Distinct());
            var m = asset.Metadata!;

            var sw = Stopwatch.StartNew();
            // + 1 tick: the stored time is the frame's pts rounded to the nearest tick and may lie just below the exact pts;
            // the frame source takes the last frame at or before the request. The frame's own time is asserted below.
            var frame = await frames.DecodeAsync(asset, new MediaTime(time.Ticks + 1), Math.Min(m.DisplayWidth ?? m.Width ?? W, W), Math.Min(m.DisplayHeight ?? m.Height ?? H, H), default);
            var prepareMs = sw.ElapsedMilliseconds;
            Assert.Equal(time, frame.SourceTime);                                                      // the reference's frame, in ticks
            int w = frame.Frame.Width, h = frame.Frame.Height;
            var size = v.GetProperty("frame_size");
            Assert.Equal((size[0].GetInt32(), size[1].GetInt32()), (w, h));
            await File.WriteAllBytesAsync(Path.Combine(dir, "frames", $"{caseId}.bgra"), frame.Frame.Pixels.ToArray());
            var cp = v.GetProperty("control_point");
            var control = new NormalizedPoint(cp[0].GetDouble(), cp[1].GetDouble());

            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var task = (Task<(RouteHoldSearch?, IReadOnlyList<(PointD, string)>)>)research.Invoke(null, [encoded, frame.Frame, control, search, true, null, null])!;
            var (s, prompts) = await task;
            var searchMs = sw.ElapsedMilliseconds;
            Assert.NotNull(s);

            // V0's selection, as a cross-check of the offline re-implementation (gate 0.3, then RouteHoldSelector).
            static double Chroma(OkLabColor x) => Math.Sqrt(x.A * x.A + x.B * x.B);
            var refChroma = Chroma(s!.Reference);
            var pool = s.Candidates.Where(x => x.IsControl || Chroma(x.Surface) >= 0.3 * refChroma).ToList();
            var accepted = RouteHoldSelector.Select(pool, RouteHoldColor.DefaultTolerance);
            output.WriteLine($"{caseId}: {prompts.Count} prompts, {s.Candidates.Count} candidates, V0 {accepted.Count} proposals; prepare {prepareMs} + encode {encodeMs} + search {searchMs} ms");
            results.Add(new
            {
                @case = caseId, frame_ticks = frame.SourceTime.Ticks, frame = new[] { w, h }, raw = $"frames/{caseId}.bgra", stride = frame.Frame.Stride,
                timing_ms = new { decode = prepareMs, encode = encodeMs, search = searchMs },
                parameters = new { tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, split_distance = 0.06, large_region_factor = 4.0, grid = false, chroma_ratio = 0.3, reference_chroma = refChroma },
                ambiguous_ids = v.GetProperty("ambiguous_ids").EnumerateArray().Select(x => x.GetString()).ToArray(),
                control_hold_id = v.GetProperty("control_hold_id").GetString(),
                reference = holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B }, contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                runs = new[]
                {
                    new
                    {
                        label = "split", control = new[] { control.X, control.Y }, search_ms = searchMs,
                        reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = prompts.Count,
                        prompt_points = prompts.Select(p => new { x = p.Item1.X / w, y = p.Item1.Y / h, source = p.Item2 }),
                        candidates = s.Candidates.Count,
                        predictions = accepted.Select(x => new { id = x.Id, is_control = x.IsControl, score = x.Score, distance = x.Distance, area = x.AreaFraction, contour = x.Contour.Select(p => new[] { p.X, p.Y }) }),
                        all_candidates = s.Candidates.Select(x => new { id = x.Id, is_control = x.IsControl, score = x.Score, distance = x.Distance, area = x.AreaFraction,
                            surface = new[] { x.Surface.L, x.Surface.A, x.Surface.B }, contour = x.Contour.Select(p => new[] { p.X, p.Y }) }),
                        chroma_rejected = s.Candidates.Where(x => !x.IsControl && Chroma(x.Surface) < 0.3 * refChroma).Select(x => x.Id).ToArray()
                    }
                }
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "pool.json"), JsonSerializer.Serialize(results));
        await File.WriteAllTextAsync(Path.Combine(dir, "environment.json"), JsonSerializer.Serialize(new { run = DateTimeOffset.Now, reference_sha256 = expectedHash, device = model.Device.ToString(), model = HoldMarkup.CurrentModelId }));
        Assert.Equal(expectedHash, Sha256(Path.Combine(folder, "project.json")));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
