using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
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
/// D048 «YOLO as a prompt source» (<c>artifacts/d048-alternatives/yolo-prompts/PROTOCOL.md</c>), local research only.
/// <list type="number">
/// <item><see cref="Save_the_frames"/> — the reference frame of each case (RouteSelectionFrameSource, the 1920 × 1080 box,
/// time = the holds' time in ticks), written raw for the detector (run in Python on these very pixels).</item>
/// <item><see cref="Build_the_pools"/> — one full-frame encoding; G0 = the frozen <c>ResearchFindAsync</c> (split);
/// the prompt loop copied (frozen <c>MaskAt</c> / <c>Surface</c> / <c>Candidate</c> by reflection) and checked to
/// reproduce G0; G1 = the loop on the YOLO box centres; G2 = the loop on G0's prompts with a 2 × 2 tiled
/// <see cref="IHoldFrame"/> (masks pasted back at the frame's size, so the D034 rule sees frame fractions).</item>
/// </list>
/// The project is opened, never saved.
/// </summary>
public sealed class YoloPromptExperimentTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;
    private static readonly Type Frozen = typeof(ReferenceSplitExperimentTests);
    private static MethodInfo M(string name) => Frozen.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public async Task Save_the_frames()
    {
        if (Env() is not { } e) return;
        Assert.False(File.Exists(Path.Combine(e.Dir, "frames.json")), "frames.json exists — never overwritten");
        var (project, frames, controls) = await OpenAsync(e);
        Directory.CreateDirectory(Path.Combine(e.Dir, "frames"));
        var list = new List<object>();
        foreach (var c in controls.RootElement.EnumerateObject())
        {
            var (clip, asset, time) = Case(project, c.Value);
            var sw = Stopwatch.StartNew();
            var frame = await Decode(frames, asset, time);
            var ms = sw.ElapsedMilliseconds;
            await File.WriteAllBytesAsync(Path.Combine(e.Dir, "frames", $"{c.Name}.bgra"), frame.Frame.Pixels.ToArray());
            list.Add(new { @case = c.Name, w = frame.Frame.Width, h = frame.Frame.Height, stride = frame.Frame.Stride, ticks = frame.SourceTime.Ticks, decode_ms = ms });
            output.WriteLine($"{c.Name}: {frame.Frame.Width}×{frame.Frame.Height} at {frame.SourceTime.Ticks}");
        }
        await File.WriteAllTextAsync(Path.Combine(e.Dir, "frames.json"), JsonSerializer.Serialize(list));
        Assert.Equal(e.Sha, Sha256(Path.Combine(e.Project, "project.json")));
    }

    [Fact]
    public async Task Build_the_pools()
    {
        if (Env() is not { } e) return;
        var yoloPath = Path.Combine(e.Dir, "yolo-prompts.json");
        if (!File.Exists(yoloPath)) { output.WriteLine("SKIPPED: yolo-prompts.json missing"); return; }
        Assert.False(File.Exists(Path.Combine(e.Dir, "pools.json")), "pools.json exists — never overwritten");
        using var yolo = JsonDocument.Parse(await File.ReadAllTextAsync(yoloPath));
        var (project, frames, controls) = await OpenAsync(e);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var options = new RouteHoldSearchOptions();
        var research = M("ResearchFindAsync");

        var results = new List<object>();
        foreach (var c in controls.RootElement.EnumerateObject())
        {
            var caseId = c.Name;
            var (clip, asset, time) = Case(project, c.Value);
            var frame = await Decode(frames, asset, time);
            // The detector saw these very pixels.
            var saved = await File.ReadAllBytesAsync(Path.Combine(e.Dir, "frames", $"{caseId}.bgra"));
            Assert.True(SameBytes(frame.Frame.Pixels, saved), $"{caseId}: frame differs");
            int w = frame.Frame.Width, h = frame.Frame.Height;
            var cp = c.Value.GetProperty("control_point");
            var control = new NormalizedPoint(cp[0].GetDouble(), cp[1].GetDouble());

            var sw = Stopwatch.StartNew();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;

            // G0 — the frozen search.
            sw.Restart();
            var g0Task = (Task<(RouteHoldSearch?, IReadOnlyList<(PointD, string)>)>)research.Invoke(null, [encoded, frame.Frame, control, options, true, null, null])!;
            var (g0, g0Prompts) = await g0Task;
            var g0Ms = sw.ElapsedMilliseconds;
            Assert.NotNull(g0);
            var prompts0 = g0Prompts.Select(p => p.Item1).ToList();

            // The loop copy reproduces G0 on G0's prompts and the full-frame encoder.
            var (copy, _) = await Loop(encoded, frame.Frame, control, prompts0, options);
            Assert.Equal(g0!.Candidates.Count, copy.Candidates.Count);
            Assert.Equal(g0.Candidates.Select(x => string.Join(";", x.Contour)).Order(), copy.Candidates.Select(x => string.Join(";", x.Contour)).Order());

            // G1 — YOLO box centres (frame pixels), confidence order.
            var y = yolo.RootElement.GetProperty(caseId);
            var prompts1 = y.GetProperty("prompts").EnumerateArray().Select(p => new PointD(p[0].GetDouble(), p[1].GetDouble())).ToList();
            sw.Restart();
            var (g1, _) = await Loop(encoded, frame.Frame, control, prompts1, options);
            var g1Ms = sw.ElapsedMilliseconds;

            // G2 — G0's prompts, 2 × 2 tiles.
            sw.Restart();
            using var tiled = await TiledFrame.CreateAsync(model, frame.Frame);
            var tileEncodeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var (g2, controlFromTiles) = await Loop(tiled, frame.Frame, control, prompts0, options, controlFrom: encoded);
            var g2Ms = sw.ElapsedMilliseconds;

            object Pool(RouteHoldSearch s, int prompts, long loopMs) => new
            {
                prompts, loop_ms = loopMs, reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B },
                all_candidates = s.Candidates.Select(x => new { id = x.Id, is_control = x.IsControl, score = x.Score, distance = x.Distance, area = x.AreaFraction,
                    surface = new[] { x.Surface.L, x.Surface.A, x.Surface.B }, contour = x.Contour.Select(p => new[] { p.X, p.Y }) })
            };
            output.WriteLine($"{caseId}: encode {encodeMs} ms; G0 {prompts0.Count} prompts {g0.Candidates.Count} cand {g0Ms} ms; " +
                             $"G1 {prompts1.Count} prompts {g1.Candidates.Count} cand {g1Ms} ms; G2 {g2.Candidates.Count} cand, tiles {tileEncodeMs} + {g2Ms} ms");
            results.Add(new
            {
                @case = caseId, frame = new[] { w, h }, frame_ticks = frame.SourceTime.Ticks, control = new[] { control.X, control.Y },
                control_hold_id = c.Value.GetProperty("control_hold_id").GetString(),
                timing_ms = new { encode = encodeMs, tile_encode = tileEncodeMs, g0_search = g0Ms, g1_loop = g1Ms, g2_loop = g2Ms },
                reference = clip.HoldMarkup!.Holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B }, contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                G0 = Pool(g0, prompts0.Count, g0Ms), G1 = Pool(g1, prompts1.Count, g1Ms), G2 = Pool(g2, prompts0.Count, g2Ms)
            });
        }
        await File.WriteAllTextAsync(Path.Combine(e.Dir, "pools.json"), JsonSerializer.Serialize(results));
        await File.WriteAllTextAsync(Path.Combine(e.Dir, "environment.json"), JsonSerializer.Serialize(new { run = DateTimeOffset.Now, reference_sha256 = e.Sha, device = model.Device.ToString(), model = HoldMarkup.CurrentModelId }));
        Assert.Equal(e.Sha, Sha256(Path.Combine(e.Project, "project.json")));
    }

    /// <summary><c>ResearchFindAsync</c>'s loop with given prompts (frozen helpers by reflection). The control mask and the
    /// reference colour come from <paramref name="controlFrom"/> (the full-frame encoder) when given — shared by all
    /// generators.</summary>
    private static async Task<(RouteHoldSearch Search, bool ControlFound)> Loop(IHoldFrame encoded, DecodedFrame frame, NormalizedPoint control,
        IReadOnlyList<PointD> prompts, RouteHoldSearchOptions options, IHoldFrame? controlFrom = null)
    {
        int w = frame.Width, h = frame.Height;
        var maskAt = M("MaskAt"); var surface = M("Surface"); var candidate = M("Candidate");
        async Task<(MaskCrop Crop, double Score, ImmutableArray<NormalizedPoint> Contour)?> MaskAt(IHoldFrame f, PointD p) =>
            await (Task<(MaskCrop, double, ImmutableArray<NormalizedPoint>)?>)maskAt.Invoke(null, [f, p, w, h])!;
        OkLabColor Surface(MaskCrop crop) => (OkLabColor)surface.Invoke(null, [frame, crop])!;
        RouteHoldCandidate Candidate(MaskCrop crop, double score, ImmutableArray<NormalizedPoint> contour, OkLabColor s, OkLabColor r, bool isControl) =>
            (RouteHoldCandidate)candidate.Invoke(null, [crop, score, contour, s, r, isControl])!;

        var controlPrompt = new PointD(Math.Clamp(control.X, 0, 1) * w, Math.Clamp(control.Y, 0, 1) * h);
        var controlMask = await MaskAt(controlFrom ?? encoded, controlPrompt);
        Assert.NotNull(controlMask);
        var (controlCrop, controlScore, controlContour) = controlMask!.Value;
        var reference = Surface(controlCrop);
        var candidates = new List<RouteHoldCandidate> { Candidate(controlCrop, controlScore, controlContour, reference, reference, true) };
        foreach (var p in prompts)
        {
            int px = Math.Clamp((int)p.X, 0, w - 1), py = Math.Clamp((int)p.Y, 0, h - 1);
            if (candidates.Any(c => c.Mask.Contains(px, py))) continue;
            if (await MaskAt(encoded, p) is not { } found) continue;
            var (crop, score, contour) = found;
            var c2 = Candidate(crop, score, contour, Surface(crop), reference, false);
            var duplicate = candidates.FindIndex(c => c.Mask.IoU(crop) >= options.NmsIouThreshold);
            if (duplicate < 0) candidates.Add(c2);
            else if (!candidates[duplicate].IsControl && candidates[duplicate].Score < score) candidates[duplicate] = c2;
        }
        return (new RouteHoldSearch(reference, candidates, prompts.Count), true);
    }

    /// <summary>A full-frame <see cref="IHoldFrame"/> over 2 × 2 encoded tiles (60 % of the frame each, origins 0 / 0.4):
    /// a point goes to the tile where it lies farthest from the tile's border; the tile's masks are pasted into frame-size
    /// masks (zeros elsewhere), so the D034 rule and the contours work at the frame's size.</summary>
    private sealed class TiledFrame : IHoldFrame
    {
        private readonly List<(int X, int Y, int W, int H, IHoldFrame Frame)> _tiles = new();
        public int Width { get; private init; }
        public int Height { get; private init; }

        public static async Task<TiledFrame> CreateAsync(IHoldSegmentationService model, DecodedFrame frame)
        {
            var t = new TiledFrame { Width = frame.Width, Height = frame.Height };
            int tw = (int)Math.Round(0.6 * frame.Width), th = (int)Math.Round(0.6 * frame.Height);
            foreach (var (ox, oy) in new[] { (0, 0), (frame.Width - tw, 0), (0, frame.Height - th), (frame.Width - tw, frame.Height - th) })
            {
                var crop = Crop(frame, ox, oy, tw, th);
                t._tiles.Add((ox, oy, tw, th, await model.PrepareFrameAsync(crop)));
            }
            return t;
        }

        public async Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
        {
            var p = Assert.Single(points);
            var tile = _tiles.Where(t => p.X >= t.X && p.X < t.X + t.W && p.Y >= t.Y && p.Y < t.Y + t.H)
                .OrderByDescending(t => Math.Min(Math.Min(p.X - t.X, t.X + t.W - p.X), Math.Min(p.Y - t.Y, t.Y + t.H - p.Y))).First();
            var set = await tile.Frame.PredictAsync(new[] { new PointD(p.X - tile.X, p.Y - tile.Y) }, cancellationToken);
            var masks = set.Masks.Select(m =>
            {
                var full = new byte[Width * Height];
                for (var y = 0; y < m.Height; y++) Array.Copy(m.Pixels, y * m.Width, full, (tile.Y + y) * Width + tile.X, m.Width);
                return new HoldMask(Width, Height, full, m.Score);
            }).ToImmutableArray();
            return new HoldMaskSet(masks);
        }

        public void Dispose() { foreach (var t in _tiles) t.Frame.Dispose(); }

        private static DecodedFrame Crop(DecodedFrame frame, int ox, int oy, int tw, int th)
        {
            var bytes = new byte[tw * th * DecodedFrame.BytesPerPixel];
            var src = frame.Pixels.Span;
            for (var y = 0; y < th; y++)
                src.Slice((oy + y) * frame.Stride + ox * DecodedFrame.BytesPerPixel, tw * DecodedFrame.BytesPerPixel).CopyTo(bytes.AsSpan(y * tw * DecodedFrame.BytesPerPixel));
            return new DecodedFrame(tw, th, tw * DecodedFrame.BytesPerPixel, bytes, frame.Timestamp);
        }
    }

    private sealed record Inputs(string Project, string Controls, string Sha, string Dir);

    private Inputs? Env()
    {
        var p = Environment.GetEnvironmentVariable("AIVE_YP_PROJECT"); var c = Environment.GetEnvironmentVariable("AIVE_YP_CONTROLS");
        var s = Environment.GetEnvironmentVariable("AIVE_YP_REFERENCE_SHA"); var d = Environment.GetEnvironmentVariable("AIVE_YP_RUN");
        if (p is null || c is null || s is null || d is null) { output.WriteLine("SKIPPED: AIVE_YP_* not set"); return null; }
        Assert.Equal(s, Sha256(Path.Combine(p, "project.json")));
        return new Inputs(p, c, s, d);
    }

    private static async Task<(AiVideoEditor.Core.Entities.Project, RouteSelectionFrameSource, JsonDocument)> OpenAsync(Inputs e)
    {
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(e.Project);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        return (project, frames, JsonDocument.Parse(await File.ReadAllTextAsync(e.Controls)));
    }

    private static (VideoClip Clip, MediaAsset Asset, MediaTime Time) Case(AiVideoEditor.Core.Entities.Project project, JsonElement c)
    {
        var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(x => x.Id == c.GetProperty("clip_id").GetGuid());
        var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
        return (clip, asset, Assert.Single(clip.HoldMarkup!.Holds.Select(h => h.SourceTime).Distinct()));
    }

    /// <summary>The reference frame: requested at the holds' time + 1 tick (the stored time is the pts rounded to a tick),
    /// its own time asserted equal to the holds' in ticks.</summary>
    private static async Task<RouteSelectionFrame> Decode(RouteSelectionFrameSource frames, MediaAsset asset, MediaTime time)
    {
        var m = asset.Metadata!;
        var f = await frames.DecodeAsync(asset, new MediaTime(time.Ticks + 1), Math.Min(m.DisplayWidth ?? m.Width ?? W, W), Math.Min(m.DisplayHeight ?? m.Height ?? H, H), default);
        Assert.Equal(time, f.SourceTime);
        return f;
    }

    private static bool SameBytes(ReadOnlyMemory<byte> a, byte[] b) => a.Span.SequenceEqual(b);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
