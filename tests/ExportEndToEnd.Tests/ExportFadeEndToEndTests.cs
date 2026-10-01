using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 10 Step 10.5 (D025 §2): fades end to end with real decoders, the Avalonia rasterizer and the ffmpeg encoder. The
/// Preview (its own pipeline and control at the canvas size) draws exactly the export's canvas on the ramp frames — one
/// layer, a layer fading over another, 8 layers, speed ≠ 1×, images and text, a clip shorter than its fades, 23.976 and
/// 29.97 fps (the D023 Step 8 criteria, no new tolerance). Independently of the app, a faded frame over black is the
/// source frame decoded by ffmpeg times the ramp's factor. The sound follows the envelope sample by sample; cancelling
/// inside a ramp ends like any cancel.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportFadeEndToEndTests
{
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportFadeEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private static void Fade(ProjectBuilder p, Clip clip, long fadeIn, long fadeOut) =>
        (clip.FadeIn, clip.FadeOut) = (p.F(fadeIn), p.F(fadeOut));

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));
        AssertValidMp4(run);
        return run;
    }

    /// <summary>The Preview draws exactly the export's canvas at each of <paramref name="frames"/>.</summary>
    private static async Task AssertPreviewEqualsExport(ExportRun run, string scene, params long[] frames)
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

    private static byte[] SourceFrame(string path, long index) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index})", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-");

    // --- pictures ---------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task A_video_fading_in_and_out_over_black_is_the_source_times_the_ramp()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var source = _media.Pattern();
        var clip = p.Video(p.VideoTrack(), source, 0, 50);
        Fade(p, clip, 10, 12);
        var run = await Export(p, "fade-one-layer");

        var frames = new long[] { 0, 4, 9, 10, 25, 37, 38, 44, 49 };
        await AssertPreviewEqualsExport(run, "fade one layer", frames);

        // Independent of the app: over the black background the canvas is the source frame × (k+1)/(F+1), to rounding.
        foreach (var n in new long[] { 0, 4, 9, 25, 38, 49 })
        {
            var factor = FadeRule.PictureFactor(n, 0, 50, 10, 12);
            var expected = SourceFrame(source.FilePath, n);
            var canvas = run.Canvases[(int)n];
            var max = 0;
            for (var i = 0; i < canvas.Length; i += 4)
                for (var c = 0; c < 3; c++)
                    max = Math.Max(max, (int)Math.Abs(canvas[i + c] - expected[i + c] * factor));
            _output.WriteLine($"frame {n}: factor {factor:0.####}, max |canvas − source·factor| = {max}");
            Assert.True(max <= 3, $"frame {n} (factor {factor}): max {max}");
        }
    }

    [FfmpegFact]
    public async Task A_video_fading_over_another_uncovers_it_in_both()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 50);
        var top = p.Video(p.VideoTrack(), _media.Pattern(), 0, 50);
        Fade(p, top, 15, 15);
        var run = await Export(p, "fade-over-video");

        await AssertPreviewEqualsExport(run, "fade over a video", 0, 7, 14, 15, 30, 35, 42, 49);

        // At the first ramp frame (factor 1/16) the red below dominates.
        var (r0, g0, _) = Pixel(run.Canvases[0], E2EMedia.Width, 5, 5);
        Assert.True(r0 > 150 && g0 < 90, $"frame 0 not mostly red: {r0},{g0}");
    }

    [FfmpegFact]
    public async Task Eight_layers_with_fades_on_several_of_them()
    {
        var p = new ProjectBuilder(FrameRate.Ntsc30);
        p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Ntsc30), 0, 40);
        for (var i = 1; i < 8; i++)
        {
            var clip = p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Ntsc30), i * 2, 40);
            (clip.Scale, clip.PositionX, clip.PositionY) = (0.5, (i - 4) * 20, (i % 3 - 1) * 20);
            Fade(p, clip, 3 + i, i % 2 == 0 ? 6 : 0);
        }
        var run = await Export(p, "fade-eight-layers");

        await AssertPreviewEqualsExport(run, "8 layers", 2, 5, 9, 14, 20, 35, 39);
    }

    public static TheoryData<int> Speeds => new() { 5, 40 };     // 0.25×, 2×

    [FfmpegTheory]
    [MemberData(nameof(Speeds))]
    public async Task A_fade_keeps_its_timeline_length_at_other_speeds(int steps)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 30);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 30, sourceInFrame: 5, speed: ClipSpeed.FromSteps(steps));
        Fade(p, clip, 8, 8);
        var run = await Export(p, $"fade-speed-{steps}");

        await AssertPreviewEqualsExport(run, $"fade at {ClipSpeed.FromSteps(steps)}", 0, 3, 7, 8, 15, 22, 26, 29);
    }

    [FfmpegFact]
    public async Task Image_and_text_fade_like_a_video()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, 40);
        var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 40);
        (png.Scale, png.Opacity) = (0.8, 0.9);
        Fade(p, png, 10, 10);
        var text = p.Text(p.VideoTrack(), "Fade", 5, 35);
        text.FontSize = 40;
        Fade(p, text, 6, 9);
        var run = await Export(p, "fade-image-text");

        await AssertPreviewEqualsExport(run, "image and text", 0, 5, 9, 10, 20, 26, 30, 34, 39);
    }

    [FfmpegFact]
    public async Task A_clip_shorter_than_its_fades_clamps_and_multiplies_the_ramps()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(), 0, 12);
        Fade(p, clip, 20, 9);                                                // clamped to 12; overlapping ramps
        var run = await Export(p, "fade-short-clip");

        await AssertPreviewEqualsExport(run, "short clip", 0, 3, 6, 11);
        var factor = FadeRule.PictureFactor(6, 0, 12, 12, 9);
        Assert.Equal((7.0 / 13) * (6.0 / 10), factor, 12);
    }

    public static TheoryData<int, int> Rates => new() { { 24000, 1001 }, { 30000, 1001 } };

    [FfmpegTheory]
    [MemberData(nameof(Rates))]
    public async Task Fades_at_ntsc_rates(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var p = new ProjectBuilder(rate);
        p.Video(p.VideoTrack(), _media.Solid(Red, rate), 0, 45);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(rate), 3, 45);
        Fade(p, clip, 7, 11);
        var run = await Export(p, $"fade-{num}");

        await AssertPreviewEqualsExport(run, $"fades at {rate}", 3, 6, 9, 10, 20, 33, 34, 40, 44);
    }

    // --- sound ------------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task The_exported_sound_follows_the_envelope_sample_by_sample()
    {
        var p = new ProjectBuilder(FrameRate.Ntsc30);
        p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Ntsc30), 0, 90);
        var tone = p.Audio(p.AudioTrack(), _media.Tone(), 0, 90);
        Fade(p, tone, 20, 30);
        var run = await Export(p, "fade-tone");

        var snapshot = run.Job.Snapshot;
        var envelope = AudioFadeEnvelope.Of(snapshot.AudioSpans.Single(), snapshot.FrameRate);
        Assert.Equal(AudioTiming.CeilingSample(p.F(20)), envelope.FadeInEnd);
        Assert.Equal(AudioTiming.CeilingSample(p.F(60)), envelope.FadeOutStart);

        // The PCM handed to the encoder is the decoded tone × g(k), exactly the mix rule.
        var source = EncoderHarness.DecodeLeft(_media.Tone().FilePath);
        var pcm = Left(run.Pcm);
        for (long k = 0; k < envelope.EndSample; k++)
            Assert.Equal(source[k] * (float)(1f * envelope.Gain(k)), pcm[k]);

        // And the MP4 (AAC) rises through the fade in, is steady in the middle and falls through the fade out.
        var decoded = EncoderHarness.DecodeLeft(run.Path);
        double Level(long from, long to) => Rms(decoded.AsSpan((int)from, (int)(to - from)));
        var third = (envelope.FadeInEnd - envelope.FirstSample) / 3;
        var (a, b, c) = (Level(0, third), Level(third, 2 * third), Level(2 * third, envelope.FadeInEnd));
        var middle = Level(envelope.FadeInEnd + 2000, envelope.FadeOutStart - 2000);
        _output.WriteLine($"fade in thirds {a:0.0000} {b:0.0000} {c:0.0000}, middle {middle:0.0000}");
        Assert.True(a < b && b < c && c < middle, "the fade in doesn't rise");
        Assert.InRange(middle, 0.25 / Math.Sqrt(2) * 0.95, 0.25 / Math.Sqrt(2) * 1.05);
        Assert.True(Level(envelope.EndSample - 3000, envelope.EndSample) < middle / 4, "the fade out doesn't fall");
    }

    // --- cancel -----------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Cancelling_inside_a_ramp_leaves_nothing_behind()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(), 0, 75);
        Fade(p, clip, 30, 30);
        var tone = p.Audio(p.AudioTrack(), _media.Tone(), 0, 75);
        Fade(p, tone, 30, 30);
        var folder = _media.OutputFolder();
        var job = Preflight(p.Project, Path.Combine(folder, "cancel.mp4"));

        var cts = new CancellationTokenSource();
        var progress = new SyncProgress(r => { if (r is { Stage: ExportStage.Video, Done: 12 }) cts.Cancel(); });   // inside the fade in
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
        AssertNoFfmpegLeft();
    }
}
