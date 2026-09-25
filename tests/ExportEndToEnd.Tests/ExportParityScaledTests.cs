using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 8.3 (A2): Preview ↔ Export parity for sources larger than the Preview's 1280 × 720 decode limit. The Preview
/// (its own <c>VideoPipeline</c>, software decoding, default <c>PlaybackSettings</c>) decodes them at ≤ 1280 × 720, the export at
/// full size, so pixels differ by design (sharpness). The D023 contract is checked as written:
/// <list type="bullet">
/// <item>geometry ±1 px, measured on luma (Y, BT.601 weights; product owner decision 2a, Step 8.3) — the non-black and the black
/// areas of each side lie within 1 px of the other's, and the colour-bar edges along several rows are at the same x ±1. Chroma
/// is 4:2:0 on both sides by design and, at the Preview's ≤ 1280 × 720 decode of a 4K source, one chroma sample covers 3 canvas
/// pixels, so a per-channel RGB edge can't be placed within 1 px; luma has the full decode resolution;</item>
/// <item>colour within YUV quantization — wherever both pictures are flat (5 × 5 range ≤ 4 per channel) they differ by at most one
/// code step of each of Y, Cb, Cr after the BT.601 limited-range conversion to 8-bit RGB: R ≤ 4, G ≤ 3, B ≤ 4 (decision 1a:
/// 1.164 + 1.596 + rounding, 1.164 + 0.391 + 0.813 + rounding, 1.164 + 2.018 + rounding), over a meaningful share of the frame;</item>
/// <item>the same source frame — after 16 × 16 block averaging the Preview's frame n is closest to the export's frame n, not a
/// neighbour.</item>
/// </list>
/// The checks are <see cref="ParityMetrics"/>. Whole-frame mean |Δ| and PSNR are reported, not asserted. 1080p sources run in the regular suite; 4K scenes are heavy
/// (<see cref="HeavyFfmpegFactAttribute"/>).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportParityScaledTests
{
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportParityScaledTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));
        AssertValidMp4(run);
        return run;
    }

    /// <summary>The whole contract at every frame of <paramref name="frames"/>.</summary>
    private async Task AssertScaledParity(ExportRun run, string scene, long[] frames, bool checkBars)
    {
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        foreach (var n in frames)
        {
            var preview = (await Preview(run.Job.Snapshot, n)).Pixels;
            var export = run.Canvases[(int)n];
            var what = $"{scene}, frame {n}";

            ParityMetrics.AssertGeometry(preview, export, w, h, what);
            if (checkBars) ParityMetrics.AssertBarEdges(preview, export, w, h, what);
            var (flat, flatMax) = ParityMetrics.AssertFlatColour(preview, export, w, h, what);
            ParityMetrics.AssertSameSourceFrame(preview, run.Canvases, n, w, h, what);

            var (mean, psnr) = ParityMetrics.WholeFrame(preview, export);
            _output.WriteLine($"{what}: flat {flat:P0} of the frame, flat max |Δ| R {flatMax[2]} G {flatMax[1]} B {flatMax[0]}; whole frame mean |Δ| {mean:0.00}, PSNR {psnr:0.0} dB");
        }
        AssertNoFfmpegLeft();
    }

    // --- 1080p source (regular suite) ------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Source_1080p_full_canvas()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        var run = await Export(p, "1080p-full");

        await AssertScaledParity(run, "1080p full canvas", new long[] { 0, 2, 4 }, checkBars: true);
    }

    [FfmpegFact]
    public async Task Source_1080p_scaled_moved_and_cropped_over_black()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        var clip = p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        (clip.Scale, clip.PositionX, clip.PositionY) = (0.5, 300, -150);
        clip.Crop = new CropRect(0.1, 0, 0.1, 0.05);
        var run = await Export(p, "1080p-scaled");

        await AssertScaledParity(run, "1080p scaled + cropped", new long[] { 0, 2, 4 }, checkBars: false);
    }

    [FfmpegFact]
    public async Task Source_1080p_rotated_30_degrees()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        var clip = p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        (clip.Scale, clip.RotationDegrees) = (0.6, 30);
        var run = await Export(p, "1080p-rotated");

        await AssertScaledParity(run, "1080p rotated 30°", new long[] { 0, 2, 4 }, checkBars: false);
    }

    [FfmpegFact]
    public async Task Source_1080p_on_a_720p_canvas()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1280, 720);
        p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        var run = await Export(p, "1080p-on-720p");

        await AssertScaledParity(run, "1080p on 720p canvas", new long[] { 0, 2, 4 }, checkBars: true);
    }

    // --- 4K source (heavy, opt-in) ---------------------------------------------------------------------------------------

    [HeavyFfmpegFact]
    public async Task Source_4K_full_canvas()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        p.Video(p.VideoTrack(), _media.Pattern4K(), 0, 13);
        var run = await Export(p, "4k-full");

        await AssertScaledParity(run, "4K full canvas", new long[] { 0, 6, 12 }, checkBars: true);
    }

    [HeavyFfmpegFact]
    public async Task Source_4K_scaled_rotated_and_cropped()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        var clip = p.Video(p.VideoTrack(), _media.Pattern4K(), 0, 13);
        (clip.Scale, clip.RotationDegrees, clip.PositionX) = (0.55, -20, -200);
        clip.Crop = new CropRect(0, 0.1, 0.15, 0);
        var run = await Export(p, "4k-transformed");

        await AssertScaledParity(run, "4K scaled + rotated + cropped", new long[] { 0, 6, 12 }, checkBars: false);
    }
}
