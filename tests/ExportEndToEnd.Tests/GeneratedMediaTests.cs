using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 8.1: the generated sources of the parity tests are what they claim — checked on the files themselves with
/// ffprobe and ffmpeg (never trusting the generator's arguments) and as the app sees them (its ffprobe analysis, its
/// decoder, the snapshot builder). No export happens here.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class GeneratedMediaTests
{
    private readonly E2EMedia _media;

    public GeneratedMediaTests(E2EMedia media) => _media = media;

    private static JsonElement Video(string path) => EncoderHarness.Stream(path, "video");

    /// <summary>Decoded frames (ffmpeg with autorotate, as the app's decoder) as raw pixels of <paramref name="pixFmt"/>.</summary>
    private static byte[] Decode(string path, string pixFmt, int frames = 1) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-frames:v", frames.ToString(), "-f", "rawvideo", "-pix_fmt", pixFmt, "-");

    /// <summary>The size of the first frame the app's own decoder delivers (full resolution, software).</summary>
    private static async Task<(int Width, int Height)> AppDecodedSize(MediaAsset asset)
    {
        var decoder = new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = asset.FilePath,
            StartTime = asset.Metadata!.StartTime ?? default,
            FirstSamplePoint = new SourceSamplePoint(0, 1),
            MaxWidth = 16_384,
            MaxHeight = 16_384,
            Hardware = HardwareDecoding.Disabled
        });
        var frame = await stream.ReadFrameAsync() ?? throw new InvalidOperationException("no frame");
        return (frame.Width, frame.Height);
    }

    public static TheoryData<int, int> Rates => new() { { 24000, 1001 }, { 25, 1 }, { 30000, 1001 }, { 50, 1 }, { 60000, 1001 } };

    [FfmpegTheory]
    [MemberData(nameof(Rates))]
    public void Pattern_has_the_exact_constant_rate_and_every_frame_differs(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var asset = _media.Pattern(rate);
        var s = Video(asset.FilePath);

        Assert.Equal(("h264", "yuv420p", "320", "180"), (s.Str("codec_name"), s.Str("pix_fmt"), s.Str("width"), s.Str("height")));
        Assert.Equal($"{numerator}/{denominator}", s.Str("r_frame_rate"));
        Assert.Equal($"{numerator}/{denominator}", s.Str("avg_frame_rate"));                       // constant frame rate
        var expected = (long)Math.Ceiling(4.0 * numerator / denominator);                            // frames with t < 4 s
        Assert.Equal(expected.ToString(), s.Str("nb_frames"));
        Assert.Equal(rate, asset.Metadata!.FrameRate);                                               // the app's analysis
        Assert.Equal((320, 180, 0), (asset.Metadata.DisplayWidth, asset.Metadata.DisplayHeight, asset.Metadata.DisplayRotation));

        // Distinguishable frames: the frame counter and moving parts change every frame.
        var md5 = System.Text.Encoding.ASCII.GetString(EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", asset.FilePath, "-f", "framemd5", "-"));
        var hashes = md5.Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim()).ToList();
        Assert.Equal(expected, hashes.Count);
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [FfmpegFact]
    public async Task Pattern_4K_is_larger_than_the_preview_decode_limit()
    {
        var asset = _media.Pattern4K();
        var s = Video(asset.FilePath);

        Assert.Equal(("h264", "yuv420p", "3840", "2160", "25/1", "25"),
            (s.Str("codec_name"), s.Str("pix_fmt"), s.Str("width"), s.Str("height"), s.Str("r_frame_rate"), s.Str("nb_frames")));
        Assert.Equal((3840, 2160, 0), (asset.Metadata!.DisplayWidth, asset.Metadata.DisplayHeight, asset.Metadata.DisplayRotation));
        Assert.Equal((3840, 2160), await AppDecodedSize(asset));                                    // the export's full-resolution decode
    }

    [FfmpegFact]
    public async Task Pattern_1080p_is_larger_than_the_preview_decode_limit()
    {
        var asset = _media.Pattern1080();
        var s = Video(asset.FilePath);

        Assert.Equal(("h264", "yuv420p", "1920", "1080", "25/1", "25"),
            (s.Str("codec_name"), s.Str("pix_fmt"), s.Str("width"), s.Str("height"), s.Str("r_frame_rate"), s.Str("nb_frames")));
        Assert.Equal((1920, 1080, 0), (asset.Metadata!.DisplayWidth, asset.Metadata.DisplayHeight, asset.Metadata.DisplayRotation));
        Assert.Equal((1920, 1080), await AppDecodedSize(asset));                                    // the export's full-resolution decode
    }

    [FfmpegFact]
    public async Task Png_has_straight_alpha_in_three_bands()
    {
        var asset = _media.PngAlpha();
        var s = Video(asset.FilePath);

        Assert.Equal(("png", "rgba", "320", "180"), (s.Str("codec_name"), s.Str("pix_fmt"), s.Str("width"), s.Str("height")));
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Equal((320, 180, 0), (asset.Metadata!.DisplayWidth, asset.Metadata.DisplayHeight, asset.Metadata.DisplayRotation));

        var rgba = Decode(asset.FilePath, "rgba");
        Assert.Equal(320 * 180 * 4, rgba.Length);
        byte Alpha(int x, int y) => rgba[(y * 320 + x) * 4 + 3];
        foreach (var y in new[] { 20, 90, 170 })
        {
            Assert.Equal((255, 255), (Alpha(0, y), Alpha(106, y)));
            Assert.Equal((128, 128), (Alpha(107, y), Alpha(213, y)));
            Assert.Equal((0, 0), (Alpha(214, y), Alpha(319, y)));
        }
        Assert.Contains(rgba.Where((_, i) => i % 4 != 3), b => b > 0);                              // it has colour, not only alpha
        Assert.Equal((320, 180), await AppDecodedSize(asset));
    }

    [FfmpegFact]
    public async Task Jpeg_is_a_plain_baseline_still()
    {
        var asset = _media.Jpeg();
        var s = Video(asset.FilePath);

        Assert.Equal(("mjpeg", "320", "180"), (s.Str("codec_name"), s.Str("width"), s.Str("height")));
        Assert.StartsWith("yuvj", s.Str("pix_fmt"));                                                 // full-range JPEG YUV, no alpha
        Assert.False(s.TryGetProperty("side_data_list", out _));                                     // no orientation
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Equal((320, 180, 0), (asset.Metadata!.DisplayWidth, asset.Metadata.DisplayHeight, asset.Metadata.DisplayRotation));
        Assert.Equal((320, 180), await AppDecodedSize(asset));
    }

    [FfmpegFact]
    public async Task Rotated_pattern_carries_a_display_matrix_of_90_degrees_clockwise()
    {
        var asset = _media.Rotated90();
        var s = Video(asset.FilePath);

        Assert.Equal(("h264", "320", "180", "25/1", "100"),
            (s.Str("codec_name"), s.Str("width"), s.Str("height"), s.Str("r_frame_rate"), s.Str("nb_frames")));  // coded size unchanged
        var matrix = s.GetProperty("side_data_list").EnumerateArray().Single(d => d.Str("side_data_type") == "Display Matrix");
        Assert.Equal("-90", matrix.Str("rotation"));                                                 // ffmpeg: counter-clockwise angle
        Assert.Equal((90, 180, 320), (asset.Metadata!.DisplayRotation, asset.Metadata.DisplayWidth, asset.Metadata.DisplayHeight));
        Assert.Equal(180 * 320 * 4, Decode(asset.FilePath, "rgba").Length);                          // ffmpeg autorotates to 180 × 320
        Assert.Equal((180, 320), await AppDecodedSize(asset));

        // The picture itself is the pattern's first frame turned clockwise: its top-left corner (the frame counter's dark box)
        // lands at the top-right.
        var plain = Decode(_media.Pattern().FilePath, "gray");
        var turned = Decode(asset.FilePath, "gray");
        for (var y = 0; y < 180; y += 7)
        for (var x = 0; x < 320; x += 7)
            Assert.Equal(plain[y * 320 + x], turned[x * 180 + (179 - y)]);                              // stream copy: lossless turn
    }

    /// <summary>The new sources and the image clip helper through the app's own snapshot builder: what the parity scenes
    /// (Steps 8.2–8.5) will be built from.</summary>
    [FfmpegFact]
    public void The_snapshot_sees_stills_rotation_and_rates_as_the_app_does()
    {
        var p = new ProjectBuilder(FrameRate.Ntsc30);
        var v1 = p.VideoTrack();
        p.Image(v1, _media.PngAlpha(), 0, 30);
        p.Image(v1, _media.Jpeg(), 30, 60);
        p.Video(v1, _media.Rotated90(), 60, 90);
        p.Video(v1, _media.Pattern(FrameRate.Fps25), 90, 115, speed: ClipSpeed.FromSteps(80));    // 4×, other source rate
        p.Video(v1, _media.Pattern4K(), 115, 125);

        var snapshot = PlaybackSnapshotBuilder.Build(p.Project, 1);
        var spans = snapshot.VideoLayers.Single().Spans;

        Assert.Equal(new[] { SpanStatus.StillImage, SpanStatus.StillImage, SpanStatus.Video, SpanStatus.Video, SpanStatus.Video },
            spans.Select(sp => sp.Status));
        Assert.Equal(new FrameSize?[] { new(320, 180), new(320, 180), new(180, 320), new(320, 180), new(3840, 2160) },
            spans.Select(sp => sp.SourceSize));
        var fourX = p.Project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single(c => !c.Speed.IsNormal);
        Assert.True(fourX.SourceOut <= _media.Pattern(FrameRate.Fps25).Metadata!.Duration);         // 25 frames at 4× fit the 4 s source
    }
}
