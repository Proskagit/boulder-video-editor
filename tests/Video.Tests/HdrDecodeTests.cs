using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 18 Step 18.5 (D033 §3 "Graph", T4, T5): the decoder's SDR arguments are exactly those of Phase 17; an HDR
/// request takes the HDR graph (even sizes, the pre-LUT zscale, lut3d tetrahedral) and its frames equal the rule
/// (<see cref="HdrToSdr.Convert"/>) on the synthetic patches within ΔE76 ≤ 1 (neutral patches neutral); a LUT that can't be
/// had fails the open — never a silent SDR decode; any LUT folder name works in the graph.
/// </summary>
public sealed class HdrDecodeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-hdr-decode", Guid.NewGuid().ToString("N"));

    public HdrDecodeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static VideoDecodeRequest Request(string path, HdrConversion? hdr, int maxWidth = 4096, int maxHeight = 4096) => new()
    {
        FilePath = path,
        FirstSamplePoint = new SourceSamplePoint(0, 1),
        MaxWidth = maxWidth,
        MaxHeight = maxHeight,
        Hardware = HardwareDecoding.Disabled,
        StrictEnd = true,
        Hdr = hdr
    };

    private FfmpegVideoDecoder Decoder(IHdrLutStore? luts) =>
        new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance, new FfmpegVideoDecoderSettings { FrameTimeout = FfmpegTools.ProcessTimeout },
            luts);

    private HdrLutStore Store(string? folder = null) => new(folder ?? Path.Combine(_dir, "luts"), NullLogger<HdrLutStore>.Instance);

    private static async Task<DecodedFrame> First(IVideoDecoder decoder, VideoDecodeRequest request)
    {
        await using var stream = await decoder.OpenAsync(request);
        return await stream.ReadFrameAsync() ?? throw new InvalidOperationException("no frame");
    }

    /// <summary>The mean R, G, B (0–255) of a patch's centre.</summary>
    private static double[] Mean(DecodedFrame frame, int patch)
    {
        var (x0, y0, w, h) = HdrMedia.Centre(patch % HdrMedia.Columns, patch / HdrMedia.Columns, frame.Width, frame.Height);
        var sum = new double[3];
        var pixels = frame.Pixels.Span;
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = y * frame.Stride + x * DecodedFrame.BytesPerPixel;   // B, G, R, A
            sum[0] += pixels[i + 2];
            sum[1] += pixels[i + 1];
            sum[2] += pixels[i];
        }
        return sum.Select(s => s / (w * h)).ToArray();
    }

    private static double[] Expected(HdrConversion hdr, int patch)
    {
        var (r, g, b) = HdrMedia.Patches[patch];
        var output = new double[3];
        HdrToSdr.Convert(hdr.Transfer, hdr.PeakNits, new[] { r / 255.0, g / 255.0, b / 255.0 }, output);
        return output.Select(v => v * 255).ToArray();
    }

    // --- arguments -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_SDR_arguments_are_exactly_those_of_Phase_17()
    {
        var request = Request(@"C:\media\a.mp4", null, 1280, 720);
        Assert.Equal(new[]
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-hwaccel", "auto", "-copyts", "-ss", "2.5", "-autorotate",
            "-i", @"C:\media\a.mp4", "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", "scale=w='min(iw,1280)':h='min(ih,720)':force_original_aspect_ratio=decrease,format=bgra,showinfo=checksum=0",
            "-fps_mode", "passthrough", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        }, FfmpegVideoDecoder.BuildArguments(request, 25_000_000, "auto"));
        Assert.Equal(FfmpegVideoDecoder.BuildArguments(request, null, null),
            FfmpegVideoDecoder.BuildArguments(request, null, null, lutPath: @"C:\ignored.cube"));   // no Hdr: no LUT used
    }

    [Theory]
    [InlineData(HdrTransfer.Hlg, false, "arib-std-b67", "limited")]
    [InlineData(HdrTransfer.Pq, true, "smpte2084", "full")]
    public void The_HDR_graph_scales_to_even_sizes_converts_only_the_matrix_before_the_LUT_and_uses_it_tetrahedrally(
        HdrTransfer transfer, bool fullRange, string trc, string range)
    {
        var request = Request(@"C:\media\a.mp4", new HdrConversion(transfer, 1000, fullRange), 1280, 720);
        var args = FfmpegVideoDecoder.BuildArguments(request, null, null, @"C:\Users\Me\AppData\Local\AiVideoEditor\cache\color-luts\hdr-r1-hlg-1000.cube");
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.Equal(
            "scale=w='min(iw,1280)':h='min(ih,720)':force_original_aspect_ratio=decrease:force_divisible_by=2," +
            $"zscale=min=bt2020nc:pin=bt2020:tin={trc}:rin={range}:p=bt2020:t={trc}:r=full,format=gbrpf32le," +
            @"lut3d=file=C\\:/Users/Me/AppData/Local/AiVideoEditor/cache/color-luts/hdr-r1-hlg-1000.cube:interp=tetrahedral," +
            "format=bgra,showinfo=checksum=0", vf);
        Assert.Equal(new[] { "-fps_mode", "passthrough", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }, args.Skip(args.IndexOf("-vf") + 2));
        Assert.Throws<ArgumentNullException>(() => FfmpegVideoDecoder.BuildArguments(request, null, null));
    }

    [Theory]
    [InlineData(@"C:\a\b.cube", @"C\\:/a/b.cube")]
    [InlineData(@"C:\O'Brien\x.cube", @"C\\:/O\\\'Brien/x.cube")]
    [InlineData(@"C:\a,b;[c]\x.cube", @"C\\:/a\,b\;\[c\]/x.cube")]
    public void A_path_is_escaped_for_both_levels_of_the_filter_graph(string path, string expected) =>
        Assert.Equal(expected, FfmpegVideoDecoder.FilterValue(path));

    // --- frames (real ffmpeg) ------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Files() => new[]
    {
        new object[] { "hlg", new HdrConversion(HdrTransfer.Hlg, 1000, false) },
        new object[] { "pq-meta", new HdrConversion(HdrTransfer.Pq, 1500, false) },
        new object[] { "pq", new HdrConversion(HdrTransfer.Pq, 1000, false) }
    };

    private static string FileOf(string name) => name switch
    {
        "hlg" => HdrMedia.Hlg,
        "pq-meta" => HdrMedia.PqWithMetadata,
        _ => HdrMedia.PqWithoutMetadata
    };

    [FfmpegTheory]
    [MemberData(nameof(Files))]
    public async Task HDR_frames_equal_the_rule_on_every_patch(string name, HdrConversion hdr)
    {
        var frame = await First(Decoder(Store()), Request(FileOf(name), hdr));
        Assert.Equal((HdrMedia.Width, HdrMedia.Height), (frame.Width, frame.Height));

        for (var patch = 0; patch < HdrMedia.Patches.Length; patch++)
        {
            var actual = Mean(frame, patch);
            var expected = Expected(hdr, patch);
            var de = HdrMedia.DeltaE(actual, expected);
            _output.WriteLine($"{name} {HdrMedia.Describe(patch)}: actual {string.Join(",", actual.Select(v => v.ToString("0.0")))} " +
                              $"expected {string.Join(",", expected.Select(v => v.ToString("0.0")))} dE {de:0.00}");
            Assert.True(de <= 1.0, $"{name} {HdrMedia.Describe(patch)}: ΔE76 {de:0.00}");
            var (r, g, b) = HdrMedia.Patches[patch];
            if (r == g && g == b)
                Assert.True(Math.Max(Math.Abs(actual[0] - actual[1]), Math.Abs(actual[1] - actual[2])) <= 1.0, $"{name} patch {patch} not neutral");
        }
    }

    [FfmpegFact]
    public async Task The_SDR_path_of_the_same_file_differs_from_the_HDR_path()
    {
        // The washed-out picture of Phase 17 (D033 "Context"): the HDR graph really changes the output.
        var hdr = await First(Decoder(Store()), Request(HdrMedia.Hlg, HdrConversion.Hlg()));
        var sdr = await First(Decoder(Store()), Request(HdrMedia.Hlg, null));
        var largest = Enumerable.Range(0, HdrMedia.Patches.Length).Max(p => HdrMedia.DeltaE(Mean(hdr, p), Mean(sdr, p)));
        _output.WriteLine($"largest ΔE76 between the HDR and the SDR path: {largest:0.0}");
        Assert.True(largest > 10, $"largest ΔE76 {largest:0.0}");
    }

    [FfmpegFact]
    public async Task An_odd_Preview_bound_gives_even_frame_sizes()
    {
        // 211 × 119 would be odd on both sides (D033 "Context": zscale refuses odd 4:2:0 sizes).
        var frame = await First(Decoder(Store()), Request(HdrMedia.Hlg, HdrConversion.Hlg(), 211, 1000));
        Assert.Equal(0, frame.Width % 2);
        Assert.Equal(0, frame.Height % 2);
        Assert.InRange(frame.Width, 208, 211);
    }

    [FfmpegFact]
    public async Task Any_LUT_folder_name_works_in_the_graph()
    {
        var folder = Path.Combine(_dir, "Pröbe O'Brien, [x]; y");
        var frame = await First(Decoder(Store(folder)), Request(HdrMedia.Hlg, HdrConversion.Hlg()));
        Assert.True(HdrMedia.DeltaE(Mean(frame, 0), Expected(HdrConversion.Hlg(), 0)) <= 1.0);
        Assert.Single(Directory.GetFiles(folder, "*.cube"));
    }

    [FfmpegFact]
    public async Task Without_a_LUT_store_an_HDR_open_fails_instead_of_decoding_as_SDR()
    {
        var ex = await Assert.ThrowsAsync<VideoDecodeException>(() => First(Decoder(null), Request(HdrMedia.Hlg, HdrConversion.Hlg())));
        Assert.Equal(VideoDecodeError.DecoderFailed, ex.Error);
        Assert.Contains("HDR", ex.Message);
    }

    [FfmpegFact]
    public async Task A_LUT_that_can_t_be_written_fails_the_open_instead_of_decoding_as_SDR()
    {
        var blocker = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocker, "not a folder");
        var ex = await Assert.ThrowsAsync<VideoDecodeException>(() =>
            First(Decoder(Store(Path.Combine(blocker, "luts"))), Request(HdrMedia.Hlg, HdrConversion.Hlg())));
        Assert.Equal(VideoDecodeError.DecoderFailed, ex.Error);
        Assert.Contains("could not be prepared", ex.Message);
    }

    [FfmpegFact]
    public async Task SDR_frames_do_not_depend_on_the_LUT_store()
    {
        var with = await First(Decoder(Store()), Request(HdrMedia.Sdr10, null));
        var without = await First(Decoder(null), Request(HdrMedia.Sdr10, null));
        Assert.True(with.Pixels.Span.SequenceEqual(without.Pixels.Span));
    }
}
