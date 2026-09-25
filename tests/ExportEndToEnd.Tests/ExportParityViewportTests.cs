using AiVideoEditor.Core.Common;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 8.4 (A3): Preview ↔ Export parity at the size the user actually sees — the Preview's control laid out as a
/// viewport smaller than the canvas (its own "contain" transform, sub-pixel scale and offset) against the export's full-size
/// canvas reduced to the same viewport by an independent exact area (box) filter through the same contain mapping (scale =
/// min(vw / cw, vh / ch), centred). Checked with the D023 metrics fixed in Step 8.3 (<see cref="ParityMetrics"/>), in viewport
/// pixels: geometry ±1 px on luma, bar edges ±1 px, flat colour R ≤ 4 / G ≤ 3 / B ≤ 4, the same source frame. Outside the canvas
/// the Preview draws nothing. Two cases (decision A3: 1–2): a 16:9 canvas at exactly ½ and a portrait canvas pillarboxed at a
/// fractional scale.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportParityViewportTests
{
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportParityViewportTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    /// <summary>The canvas <paramref name="cw"/> × <paramref name="ch"/> "contained" in <paramref name="vw"/> × <paramref name="vh"/>.</summary>
    private static (double Scale, double X, double Y) Contain(int cw, int ch, int vw, int vh)
    {
        var s = Math.Min((double)vw / cw, (double)vh / ch);
        return (s, (vw - cw * s) / 2, (vh - ch * s) / 2);
    }

    /// <summary>
    /// The export canvas reduced to the viewport by exact area averaging: each viewport pixel is the coverage-weighted mean of the
    /// canvas pixels under it; the part of a pixel outside the canvas counts as nothing (0), so the canvas edge fades like the
    /// Preview's anti-aliased edge and everything outside the canvas is 0.
    /// </summary>
    private static byte[] ToViewport(byte[] canvas, int cw, int ch, int vw, int vh)
    {
        var (s, ox, oy) = Contain(cw, ch, vw, vh);
        var result = new byte[vw * vh * 4];
        for (var ty = 0; ty < vh; ty++)
        {
            double y0 = (ty - oy) / s, y1 = (ty + 1 - oy) / s;
            for (var tx = 0; tx < vw; tx++)
            {
                double x0 = (tx - ox) / s, x1 = (tx + 1 - ox) / s;
                double b = 0, g = 0, r = 0, covered = 0;
                for (var sy = Math.Max(0, (int)Math.Floor(y0)); sy < Math.Min(ch, (int)Math.Ceiling(y1)); sy++)
                {
                    var wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (var sx = Math.Max(0, (int)Math.Floor(x0)); sx < Math.Min(cw, (int)Math.Ceiling(x1)); sx++)
                    {
                        var wgt = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        var i = (sy * cw + sx) * 4;
                        b += canvas[i] * wgt; g += canvas[i + 1] * wgt; r += canvas[i + 2] * wgt; covered += wgt;
                    }
                }
                var area = (x1 - x0) * (y1 - y0);
                var o = (ty * vw + tx) * 4;
                result[o] = (byte)Math.Round(b / area);
                result[o + 1] = (byte)Math.Round(g / area);
                result[o + 2] = (byte)Math.Round(r / area);
                result[o + 3] = (byte)Math.Round(255 * covered / area);
            }
        }
        return result;
    }

    private async Task AssertViewportParity(ExportRun run, string scene, int vw, int vh, long[] frames, bool pillars)
    {
        var (cw, ch) = (run.Output.Size.Width, run.Output.Size.Height);
        var reference = run.Canvases.Select(c => ToViewport(c, cw, ch, vw, vh)).ToList();
        var (s, ox, _) = Contain(cw, ch, vw, vh);
        foreach (var n in frames)
        {
            var preview = (await Preview(run.Job.Snapshot, n, vw, vh)).Pixels;
            var expected = reference[(int)n];
            var what = $"{scene}, frame {n}";
            Assert.Equal(expected.Length, preview.Length);

            if (pillars)   // nothing is drawn beside the canvas (whole pixels only; the edge pixels are anti-aliased)
            {
                var left = (int)Math.Floor(ox);
                var right = (int)Math.Ceiling(ox + cw * s);
                for (var y = 0; y < vh; y++)
                for (var x = 0; x < vw; x++)
                {
                    if (x >= left && x < right) continue;
                    var i = (y * vw + x) * 4;
                    Assert.True(preview[i] == 0 && preview[i + 1] == 0 && preview[i + 2] == 0 && preview[i + 3] == 0,
                        $"{what}: the Preview drew at ({x},{y}) outside the canvas [{ox:0.###}, {ox + cw * s:0.###})");
                }
            }

            ParityMetrics.AssertGeometry(preview, expected, vw, vh, what);
            ParityMetrics.AssertBarEdges(preview, expected, vw, vh, what);
            var (flat, flatMax) = ParityMetrics.AssertFlatColour(preview, expected, vw, vh, what);
            ParityMetrics.AssertSameSourceFrame(preview, reference, n, vw, vh, what);

            var (mean, psnr) = ParityMetrics.WholeFrame(preview, expected);
            _output.WriteLine($"{what}: viewport {vw}×{vh}, scale {s:0.#####}, offset x {ox:0.###}; flat {flat:P0}, flat max |Δ| R {flatMax[2]} G {flatMax[1]} B {flatMax[0]}; whole mean |Δ| {mean:0.00}, PSNR {psnr:0.0} dB");
        }
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task Landscape_canvas_in_a_half_size_viewport()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 5);
        (png.Scale, png.RotationDegrees, png.PositionX, png.PositionY) = (2.5, 15, -300, 120);
        var text = p.Text(p.VideoTrack(), "Viewport 8.4", 0, 5);
        (text.FontSize, text.PositionY) = (96, -300);
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "viewport-landscape.mp4"));
        AssertValidMp4(run);

        await AssertViewportParity(run, "1920×1080 in 960×540", 960, 540, new long[] { 0, 2, 4 }, pillars: false);
    }

    [FfmpegFact]
    public async Task Portrait_canvas_pillarboxed_at_a_fractional_scale()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1080, 1920);
        p.Video(p.VideoTrack(), _media.Rotated90(), 0, 25);
        var text = p.Text(p.VideoTrack(), "portrait", 0, 25);
        (text.FontSize, text.PositionY) = (140, 600);
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "viewport-portrait.mp4"));
        AssertValidMp4(run);

        await AssertViewportParity(run, "1080×1920 in 960×540", 960, 540, new long[] { 0, 12, 24 }, pillars: true);
    }
}
