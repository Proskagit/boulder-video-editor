using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 8.2 (A1): Preview ↔ Export parity at the canvas size for the source types added in Step 8.1 — PNG with
/// straight alpha, JPEG, a display-matrix 90° video, video at 0.25× / 2× / 4× and sources whose rate differs from the
/// project's. All sources are ≤ 1280 × 720, so the Preview (its own <c>VideoPipeline</c>, software decoding, and its own
/// control at the canvas size) decodes the same frames as the export and must draw the same bytes as the export's
/// rasterizer. Where a scene shows a source 1:1, the canvas is also checked against that source frame decoded directly by
/// ffmpeg, chosen by an independent computation of the D009/D022 rule — so both sides being wrong the same way fails too.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportParityCanvasTests
{
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportParityCanvasTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));
        AssertValidMp4(run);
        return run;
    }

    /// <summary>The Preview draws exactly the export's canvas at each of <paramref name="frames"/>.</summary>
    private async Task AssertPreviewEqualsExport(ExportRun run, string scene, params long[] frames)
    {
        foreach (var n in frames)
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = 0;
            var max = 0;
            for (var i = 0; i < canvas.Length; i++)
            {
                if (preview.Pixels[i] == canvas[i]) continue;
                differing++;
                max = Math.Max(max, Math.Abs(preview.Pixels[i] - canvas[i]));
            }
            Assert.True(differing == 0, $"{scene}, frame {n}: {differing} bytes differ between the Preview and the export canvas (max {max})");
        }
        AssertNoFfmpegLeft();
    }

    /// <summary>Frame <paramref name="index"/> of <paramref name="path"/> as BGRA, decoded by ffmpeg itself (autorotate on,
    /// as the app's decoder).</summary>
    private static byte[] SourceFrame(string path, long index) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index})", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-");

    private static (int Max, double Mean) Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0; long sum = 0, count = 0;
        for (var i = 0; i < a.Length; i += 4)
        for (var c = 0; c < 3; c++)
        {
            var d = Math.Abs(a[i + c] - b[i + c]);
            max = Math.Max(max, d); sum += d; count++;
        }
        return (max, (double)sum / count);
    }

    /// <summary>The canvas shows source frame <paramref name="expected"/> 1:1 — and clearly not its neighbours.</summary>
    private void AssertShowsSourceFrame(string path, byte[] canvas, long expected, long sourceFrames, string what)
    {
        var (max, mean) = Diff(canvas, SourceFrame(path, expected));
        Assert.True(max <= 3 && mean < 0.5, $"{what}: canvas vs source frame {expected}: max {max}, mean {mean:0.00}");
        foreach (var neighbour in new[] { expected - 1, expected + 1 }.Where(k => k >= 0 && k < sourceFrames))
        {
            var (_, other) = Diff(canvas, SourceFrame(path, neighbour));
            Assert.True(other > 2, $"{what}: source frames {expected} and {neighbour} are indistinguishable (mean {other:0.00})");
        }
    }

    private readonly record struct Q(BigInteger N, BigInteger D)
    {
        public static Q Of(BigInteger n, BigInteger d) => d < 0 ? new(-n, -d) : new(n, d);
        public static Q Ticks(MediaTime t) => new(t.Ticks, TimeSpan.TicksPerSecond);
        public static Q operator +(Q a, Q b) => Of(a.N * b.D + b.N * a.D, a.D * b.D);
        public static Q operator -(Q a, Q b) => Of(a.N * b.D - b.N * a.D, a.D * b.D);
        public static Q operator *(Q a, Q b) => Of(a.N * b.N, a.D * b.D);
        public static Q Min(Q a, Q b) => a.N * b.D <= b.N * a.D ? a : b;
        public BigInteger Floor() => N >= 0 ? N / D : -((-N + D - 1) / D);
    }

    /// <summary>
    /// D009/D022, computed independently: timeline frame <paramref name="n"/> of a clip starting at timeline frame
    /// <paramref name="clipStart"/> with SourceIn = timeline frame <paramref name="sourceInFrame"/>'s time, at speed a/b, samples
    /// <c>t = SourceIn + (FromFrame(n) − S)·s + ½·min(P·s, 1/src)</c>; a constant-rate source starting at 0 shows its frame
    /// ⌊t·src⌋ (the last one at or before t), held at the ends.
    /// </summary>
    private static long ExpectedSourceFrame(long n, long clipStart, long sourceInFrame, ClipSpeed speed, FrameRate project, FrameRate source, long sourceFrames)
    {
        var s = speed.IsNormal ? new Q(1, 1) : new Q(speed.Numerator, speed.Denominator);
        var start = Q.Ticks(MediaTime.FromFrame(clipStart, project));
        var t = Q.Ticks(MediaTime.FromFrame(sourceInFrame, project)) + (Q.Ticks(MediaTime.FromFrame(n, project)) - start) * s;
        var p = Q.Ticks(MediaTime.FromFrame(n + 1, project)) - Q.Ticks(MediaTime.FromFrame(n, project));
        var half = new Q(1, 2) * Q.Min(p * s, new Q(source.Denominator, source.Numerator));
        var index = ((t + half) * new Q(source.Numerator, source.Denominator)).Floor();
        return (long)BigInteger.Clamp(index, 0, sourceFrames - 1);
    }

    private static long FrameCount(MediaAsset asset) =>
        long.Parse(EncoderHarness.Stream(asset.FilePath, "video").Str("nb_frames"));

    // --- PNG with alpha -----------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Png_alpha_over_video_blends_per_band_in_both()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 25);
        p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 25);
        var run = await Export(p, "png-alpha");

        await AssertPreviewEqualsExport(run, "png-alpha", 0, 12, 24);

        // The bands: opaque = the PNG's colour, transparent = the red below, half = the average (straight alpha).
        var png = EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", _media.PngAlpha().FilePath, "-f", "rawvideo", "-pix_fmt", "bgra", "-");
        var red = SourceFrame(_media.Solid(Red, FrameRate.Fps25).FilePath, 0);
        var canvas = run.Canvases[12];
        foreach (var y in new[] { 30, 90, 150 })
        {
            foreach (var x in new[] { 20, 90 })        // opaque band
                AssertPixel(canvas, png, x, y, 0, 3, "opaque");
            foreach (var x in new[] { 230, 300 })      // transparent band
                AssertPixel(canvas, red, x, y, 0, 3, "transparent");
            foreach (var x in new[] { 130, 190 })      // half band
            {
                var i = (y * 320 + x) * 4;
                for (var c = 0; c < 3; c++)
                    Assert.InRange(canvas[i + c], (png[i + c] + red[i + c]) / 2 - 3, (png[i + c] + red[i + c]) / 2 + 3);
            }
        }
    }

    private static void AssertPixel(byte[] actual, byte[] expected, int x, int y, int offset, int tolerance, string what)
    {
        var i = (y * 320 + x) * 4 + offset;
        for (var c = 0; c < 3; c++)
            Assert.True(Math.Abs(actual[i + c] - expected[i + c]) <= tolerance, $"{what} ({x},{y}) channel {c}: {actual[i + c]} vs {expected[i + c]}");
    }

    [FfmpegFact]
    public async Task Png_alpha_with_transform_crop_and_opacity()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
        var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 25);
        (png.Scale, png.RotationDegrees, png.Opacity, png.PositionX, png.PositionY) = (0.7, 20, 0.8, 30, -10);
        png.Crop = new CropRect(0.05, 0, 0, 0.1);
        var run = await Export(p, "png-alpha-transformed");

        await AssertPreviewEqualsExport(run, "png-alpha-transformed", 0, 12, 24);
    }

    // --- JPEG ---------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Jpeg_full_canvas_is_the_decoded_still()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Image(p.VideoTrack(), _media.Jpeg(), 0, 25);
        var run = await Export(p, "jpeg");

        await AssertPreviewEqualsExport(run, "jpeg", 0, 24);
        AssertShowsSourceFrame(_media.Jpeg().FilePath, run.Canvases[24], 0, 1, "jpeg");
    }

    [FfmpegFact]
    public async Task Jpeg_with_crop_and_scale_over_video()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
        var jpeg = p.Image(p.VideoTrack(), _media.Jpeg(), 0, 25);
        (jpeg.Scale, jpeg.PositionX) = (0.6, -60);
        jpeg.Crop = new CropRect(0.1, 0.1, 0.1, 0.1);
        var run = await Export(p, "jpeg-crop");

        await AssertPreviewEqualsExport(run, "jpeg-crop", 0, 12, 24);
    }

    // --- Display-matrix rotation ----------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Rotated_video_on_a_landscape_canvas_is_pillarboxed()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Rotated90(), 0, 25);
        var run = await Export(p, "rotated-landscape");

        await AssertPreviewEqualsExport(run, "rotated-landscape", 0, 12, 24);

        // 180 × 320 shown in 320 × 180: 101.25 × 180 in the middle, black on both sides.
        var canvas = run.Canvases[12];
        bool Black(int x, int y) => Pixel(canvas, 320, x, y) is (0, 0, 0);
        foreach (var y in new[] { 10, 90, 170 })
            Assert.True(Black(50, y) && Black(270, y), $"pillar at row {y} not black");
        Assert.Contains(Enumerable.Range(0, 180), y => !Black(160, y));
    }

    [FfmpegFact]
    public async Task Rotated_video_on_a_portrait_canvas_shows_the_turned_frames()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 180, 320);
        p.Video(p.VideoTrack(), _media.Rotated90(), 0, 25);
        var run = await Export(p, "rotated-portrait");

        await AssertPreviewEqualsExport(run, "rotated-portrait", 0, 12, 24);
        foreach (var n in new long[] { 0, 12, 24 })
            AssertShowsSourceFrame(_media.Rotated90().FilePath, run.Canvases[(int)n], n, 100, $"rotated-portrait frame {n}");
    }

    // --- Speed --------------------------------------------------------------------------------------------------------

    public static TheoryData<int> SpeedSteps => new() { 5, 40, 80 };     // 0.25×, 2×, 4×

    [FfmpegTheory]
    [MemberData(nameof(SpeedSteps))]
    public async Task Video_at_speed_selects_the_same_source_frames(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var rate = FrameRate.Fps25;
        var source = _media.Pattern(rate, seconds: 6);
        var p = new ProjectBuilder(rate);
        p.Video(p.VideoTrack(), source, 0, 25, sourceInFrame: 10, speed: speed);
        var run = await Export(p, $"speed-{steps}");

        var frames = new long[] { 0, 7, 13, 24 };
        await AssertPreviewEqualsExport(run, $"speed {speed}", frames);
        var total = FrameCount(source);
        foreach (var n in frames)
        {
            var k = ExpectedSourceFrame(n, 0, 10, speed, rate, rate, total);
            _output.WriteLine($"{speed}: timeline frame {n} → source frame {k}");
            AssertShowsSourceFrame(source.FilePath, run.Canvases[(int)n], k, total, $"{speed} frame {n}");
        }
    }

    // --- Source rate ≠ project rate ---------------------------------------------------------------------------------------

    public static TheoryData<int, int, int, int> Rates => new()
    {
        { 24000, 1001, 30000, 1001 },   // 23.976 source in a 29.97 project
        { 25, 1, 30000, 1001 },
        { 60000, 1001, 25, 1 },
        { 50, 1, 24000, 1001 },
        { 30000, 1001, 25, 1 },
    };

    [FfmpegTheory]
    [MemberData(nameof(Rates))]
    public async Task Source_rate_other_than_the_project_rate(int sourceNum, int sourceDen, int projectNum, int projectDen)
    {
        var sourceRate = new FrameRate(sourceNum, sourceDen);
        var projectRate = new FrameRate(projectNum, projectDen);
        var source = _media.Pattern(sourceRate);
        var p = new ProjectBuilder(projectRate);
        p.Video(p.VideoTrack(), source, 0, 30, sourceInFrame: 3);
        var run = await Export(p, $"rate-{sourceNum}-{projectNum}");

        var frames = new long[] { 0, 11, 17, 29 };
        await AssertPreviewEqualsExport(run, $"{sourceRate} in {projectRate}", frames);
        var total = FrameCount(source);
        foreach (var n in frames)
        {
            var k = ExpectedSourceFrame(n, 0, 3, ClipSpeed.Normal, projectRate, sourceRate, total);
            _output.WriteLine($"{sourceRate} in {projectRate}: timeline frame {n} → source frame {k}");
            AssertShowsSourceFrame(source.FilePath, run.Canvases[(int)n], k, total, $"{sourceRate} in {projectRate} frame {n}");
        }
    }

    // --- Everything at once ---------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Mixed_layers_of_every_new_type()
    {
        var p = new ProjectBuilder(FrameRate.Ntsc30);
        p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 30, speed: ClipSpeed.FromSteps(27));   // 1.35×, 25 fps source
        var rotated = p.Video(p.VideoTrack(), _media.Rotated90(), 0, 30);
        (rotated.Scale, rotated.PositionX, rotated.RotationDegrees) = (0.6, 90, 10);
        var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 5, 30);
        (png.Scale, png.RotationDegrees, png.PositionY) = (0.5, -15, 40);
        var jpeg = p.Image(p.VideoTrack(), _media.Jpeg(), 0, 20);
        (jpeg.Scale, jpeg.PositionX, jpeg.PositionY, jpeg.Opacity) = (0.3, -110, -60, 0.7);
        var text = p.Text(p.VideoTrack(), "Step 8.2", 10, 30);
        (text.FontSize, text.ColorHex) = (24, "#80FF80");
        var run = await Export(p, "mixed");

        await AssertPreviewEqualsExport(run, "mixed", 0, 4, 5, 15, 19, 20, 29);
    }
}
