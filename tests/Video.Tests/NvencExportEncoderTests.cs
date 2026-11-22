using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.Video.Tests.EncoderHarness;

namespace AiVideoEditor.Video.Tests;

/// <summary>Skips unless this machine's ffmpeg can encode with NVENC (the app's own check, D059 §3).</summary>
public sealed class NvencFactAttribute : FactAttribute
{
    public NvencFactAttribute() => Skip = FfmpegTools.SkipReason ?? (NvencProbe.Available ? null : "NVENC unavailable: " + NvencProbe.Reason);
}

internal static class NvencProbe
{
    private static readonly Lazy<ExportEncoderAvailability> Result = new(() =>
        FfmpegTools.SkipReason is not null ? ExportEncoderAvailability.SoftwareOnly
            : new FfmpegEncoderCapabilities(FfmpegTools.FfmpegLocator, NullLogger<FfmpegEncoderCapabilities>.Instance).GetAsync().GetAwaiter().GetResult());

    public static bool Available => Result.Value.Nvenc;
    public static ExportEncoderAvailability Value => Result.Value;
    public static string? Reason => Result.Value.NvencUnavailableReason;
}

/// <summary>
/// D059 §3: the NVENC encoder. The command line: the D023 input, colour conversion on the CPU (yuv420p reaches the GPU,
/// never BGRA), the same tags and container as libx264, h264_nvenc with the plan's preset, VBR with the calibrated CQ;
/// libx264's command lines are unchanged. On a machine with NVENC: a real encoding is a valid H.264 MP4 with every frame in
/// order, the exact rate and duration, BT.709 limited tags; without NVENC, the same request encodes with libx264 and says
/// why (the fallback before the start).
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class NvencExportEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-nvenc-tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public NvencExportEncoderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Out(string name = "out.mp4") => Path.Combine(_dir, name);

    private static string Line(ExportVideoPlan plan) =>
        string.Join(' ', FfmpegExportEncoder.VideoArguments(Output(1920, 1080, FrameRate.Ntsc30, 300), plan, "a.m4a", "v.mp4"));

    [Fact]
    public void The_nvenc_command_line_keeps_the_D023_input_conversion_tags_and_container()
    {
        var plan = ExportVideoPlan.For(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, new(1920, 1080), new ExportEncoderAvailability(true));
        var line = Line(plan);

        Assert.StartsWith(FfmpegExportEncoderTests.GoldenVideoArguments[..FfmpegExportEncoderTests.GoldenVideoArguments.IndexOf("-c:v", StringComparison.Ordinal)], line);
        Assert.EndsWith("-pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -c:a copy -movflags +faststart -f mp4 v.mp4", line);
        Assert.Contains($"-c:v h264_nvenc -preset p5 -tune hq -profile:v high -rc vbr -cq {ExportVideoPlan.NvencCqOf(ExportQuality.High)} -b:v 0", line);
        Assert.Contains("-spatial-aq 1 -rc-lookahead 20 -bf 3 -g 250", line);
        // D059's exact encoder part (D076 Q7 A): nothing between the CQ and the AQ — no peak, no buffer.
        Assert.Contains($"-rc vbr -cq {ExportVideoPlan.NvencCqOf(ExportQuality.High)} -b:v 0 -spatial-aq 1 -rc-lookahead 20 -bf 3 -g 250 -pix_fmt", line);
        Assert.DoesNotContain("-maxrate", line);
        Assert.DoesNotContain("-bufsize", line);
        Assert.DoesNotContain("libx264", line);
        Assert.DoesNotContain("-crf", line);
        Assert.DoesNotContain("-multipass", line);
        Assert.Contains("-multipass qres", Line(ExportVideoPlan.For(ExportEncoding.Default with { Preset = ExportSpeedPreset.Slow, Encoder = ExportVideoEncoder.Nvenc },
            new(1920, 1080), new ExportEncoderAvailability(true))));
    }

    [Fact]
    public void An_average_bitrate_has_a_peak_and_a_buffer_on_both_encoders()
    {
        var bitrate = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 20 };
        var x264 = Line(ExportVideoPlan.For(bitrate, new(1920, 1080), new ExportEncoderAvailability(true)));
        var nvenc = Line(ExportVideoPlan.For(bitrate with { Encoder = ExportVideoEncoder.Nvenc }, new(1920, 1080), new ExportEncoderAvailability(true)));

        Assert.Contains("-c:v libx264 -preset medium -b:v 20000k -maxrate 30000k -bufsize 40000k -pix_fmt", x264);
        Assert.Contains("-rc vbr -b:v 20000k -maxrate 30000k -bufsize 40000k", nvenc);
        Assert.DoesNotContain("-cq", nvenc);
    }

    [Fact]
    public void The_old_libx264_profiles_change_only_the_preset_and_the_crf()
    {
        var golden = FfmpegExportEncoderTests.GoldenVideoArguments;
        string Of(ExportQuality q, ExportSpeedPreset s) => string.Join(' ', FfmpegExportEncoder.VideoArguments(Output(1920, 1080, FrameRate.Ntsc30, 300),
            new ExportEncoding(q, s, 192), "a.m4a", "v.mp4"));

        Assert.Equal(golden, Of(ExportQuality.High, ExportSpeedPreset.Medium));                                            // «Сбалансированный»
        Assert.Equal(golden.Replace("-preset medium -crf 18", "-preset veryfast -crf 18"), Of(ExportQuality.High, ExportSpeedPreset.VeryFast)); // «Быстрый»
        Assert.Equal(golden.Replace("-preset medium -crf 18", "-preset slow -crf 14"), Of(ExportQuality.Maximum, ExportSpeedPreset.Slow));     // «Высокое качество»
    }

    [FfmpegFact]
    public async Task Without_nvenc_the_request_encodes_with_libx264_and_says_why()
    {
        var output = Output(160, 90, FrameRate.Fps25, 25);
        var encoder = new FfmpegExportEncoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegExportEncoder>.Instance,
            new FixedCapabilities(new ExportEncoderAvailability(false, "no NVIDIA graphics card")));
        await using (var encoding = await encoder.StartAsync(output, ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, Out()))
        {
            Assert.False(encoding.IsHardware);
            Assert.Equal("libx264 medium CRF 18", encoding.EncoderDescription);
            Assert.Equal("no NVIDIA graphics card", encoding.FallbackReason);
            await WriteAll(encoding, output);
            await encoding.CompleteAsync();
        }
        Assert.Equal("h264", Stream(Out(), "video").Str("codec_name"));
    }

    [NvencFact]
    public async Task Nvenc_encodes_a_valid_mp4_with_every_frame_in_order_and_the_D023_tags()
    {
        foreach (var (profile, speed) in new[] { ("Fast", ExportSpeedPreset.VeryFast), ("Balanced", ExportSpeedPreset.Medium), ("HighQuality", ExportSpeedPreset.Slow) })
        {
            var path = Out($"nvenc-{profile}.mp4");
            var output = Output(320, 180, FrameRate.Ntsc30, 60);   // 2 s at 29.97
            var encoder = new FfmpegExportEncoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegExportEncoder>.Instance,
                new FfmpegEncoderCapabilities(FfmpegTools.FfmpegLocator, NullLogger<FfmpegEncoderCapabilities>.Instance));
            await using (var encoding = await encoder.StartAsync(output, ExportEncoding.Default with { Preset = speed, Encoder = ExportVideoEncoder.Nvenc }, path))
            {
                Assert.True(encoding.IsHardware);
                Assert.StartsWith("NVENC p", encoding.EncoderDescription);
                await WriteAll(encoding, output);
                await encoding.CompleteAsync();
            }

            var video = Stream(path, "video");
            _output.WriteLine($"{profile}: {video}");
            Assert.Equal(("h264", "320", "180", "30000/1001", "yuv420p", "tv", "bt709", "bt709", "bt709"),
                (video.Str("codec_name"), video.Str("width"), video.Str("height"), video.Str("r_frame_rate"), video.Str("pix_fmt"),
                 video.Str("color_range"), video.Str("color_space"), video.Str("color_transfer"), video.Str("color_primaries")));
            var frames = DecodeFrames(path, 320, 180);
            Assert.Equal(Enumerable.Range(0, 60), frames.Select(f => ReadNumber(f, 320, 180)));
            Assert.Equal(output.AudioSampleCount, DecodeLeft(path).Length);
            var (pts, num, den) = VideoPts(path);
            Assert.Equal(60, pts.Count);
            Assert.Equal(pts.OrderBy(p => p), pts);
        }
        Assert.Single(Directory.GetFiles(_dir, "*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith("Fast.mp4")));
        Assert.Empty(Directory.GetFiles(_dir, ".*"));   // no temporary file left
    }

    private static async Task WriteAll(IExportEncoding encoding, ExportOutput output)
    {
        var chunk = new float[2 * 4_800];
        for (long k = 0; k < output.AudioSampleCount; k += 4_800)
            await encoding.WriteAudioAsync(chunk.AsMemory(0, 2 * (int)Math.Min(4_800, output.AudioSampleCount - k)));
        var stride = output.Size.Width * 4;
        var pixels = new byte[stride * output.Size.Height];
        for (long n = 0; n < output.FrameCount; n++)
        {
            NumberFrame(n, pixels, stride, output.Size.Width, output.Size.Height);
            await encoding.WriteFrameAsync(pixels, stride);
        }
    }

    private sealed class FixedCapabilities(ExportEncoderAvailability value) : IExportEncoderCapabilities
    {
        public Task<ExportEncoderAvailability> GetAsync(CancellationToken ct = default) => Task.FromResult(value);
    }
}
