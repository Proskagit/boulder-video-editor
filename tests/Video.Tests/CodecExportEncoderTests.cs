using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.Video.Tests.EncoderHarness;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D076: the export's codecs (H.264, HEVC, AV1) and containers (MP4, MOV, MKV). The command lines: the D023 input, conversion
/// and tags for every codec; libx265 / SVT-AV1 / hevc_nvenc / av1_nvenc with the plan's preset and value; HEVC tagged hvc1 in
/// MP4 / MOV; the index at the front of every container. Real encodings (each where this machine's ffmpeg has the encoder):
/// every frame in order, the exact rate, the audio sample for sample, the BT.709 limited tags, the codec and the container.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class CodecExportEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-codec-tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _log;

    public CodecExportEncoderTests(ITestOutputHelper log)
    {
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly ExportEncoderAvailability All = new(true) { NvencHevc = true, NvencAv1 = true };

    private static string Line(ExportEncoding e, string path = "v.mp4") => string.Join(' ', FfmpegExportEncoder.VideoArguments(
        Output(1920, 1080, FrameRate.Ntsc30, 300), ExportVideoPlan.For(e, new(1920, 1080), All), "a.m4a", path, e.Container));

    [Fact]
    public void Every_codec_keeps_the_D023_input_conversion_and_tags()
    {
        var golden = FfmpegExportEncoderTests.GoldenVideoArguments;
        var input = golden[..golden.IndexOf("-c:v", StringComparison.Ordinal)];
        foreach (var codec in Enum.GetValues<ExportVideoCodec>())
        foreach (var encoder in Enum.GetValues<ExportVideoEncoder>())
        {
            var line = Line(ExportEncoding.Default with { Codec = codec, Encoder = encoder });
            Assert.StartsWith(input, line);
            Assert.Contains("-pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -c:a copy", line);
        }
    }

    [Fact]
    public void The_hevc_and_av1_encoders_options()
    {
        var hevc = ExportEncoding.Default with { Codec = ExportVideoCodec.Hevc };
        Assert.Contains($"-c:v libx265 -preset medium -profile:v main -crf {ExportVideoPlan.X265CrfOf(ExportQuality.High)} -x265-params log-level=error -tag:v hvc1", Line(hevc));
        Assert.Contains($"-c:v hevc_nvenc -preset p7 -tune hq -profile:v main -tier high -rc vbr -cq {ExportVideoPlan.HevcNvencCqOf(ExportQuality.Maximum)} -b:v 0 -maxrate 160M -bufsize 160M -spatial-aq 1 -rc-lookahead 20 -bf 3 -g 250 -multipass qres -tag:v hvc1",
            Line(ExportPresets.Maximum with { Codec = ExportVideoCodec.Hevc }));
        var av1 = ExportEncoding.Default with { Codec = ExportVideoCodec.Av1 };
        Assert.Contains($"-c:v libsvtav1 -preset 7 -g 250 -crf {ExportVideoPlan.SvtAv1CrfOf(ExportQuality.High)} -pix_fmt", Line(av1));
        Assert.Contains($"-c:v av1_nvenc -preset p5 -tune hq -rc vbr -cq {ExportVideoPlan.Av1NvencCqOf(ExportQuality.High)} -b:v 0", Line(av1 with { Encoder = ExportVideoEncoder.Nvenc }));
        Assert.DoesNotContain("hvc1", Line(av1));
        // Only HEVC NVENC has a peak at constant quality (its High tier); H.264 and AV1 NVENC exactly D059's -cq N -b:v 0.
        Assert.Contains($"-cq {ExportVideoPlan.Av1NvencCqOf(ExportQuality.High)} -b:v 0 -spatial-aq 1 -rc-lookahead 20 -bf 3 -g 250 -pix_fmt", Line(av1 with { Encoder = ExportVideoEncoder.Nvenc }));
        Assert.DoesNotContain("-maxrate", Line(av1 with { Encoder = ExportVideoEncoder.Nvenc }));
        Assert.DoesNotContain("-maxrate", Line(ExportPresets.Maximum));
        Assert.DoesNotContain("hvc1", Line(ExportEncoding.Default));
        Assert.Contains("-c:v libsvtav1 -preset 7 -g 250 -b:v 20000k -pix_fmt", Line(av1 with { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 20 }));
        Assert.DoesNotContain("-crf", Line(hevc with { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 20 }));
        Assert.Contains("-b:v 20000k -maxrate 30000k -bufsize 40000k", Line(hevc with { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 20 }));
    }

    [Fact]
    public void Every_container_has_its_muxer_and_its_index_first_and_mp4_is_the_D023_tail()
    {
        Assert.EndsWith("-c:a copy -movflags +faststart -f mp4 v.mp4", Line(ExportEncoding.Default));
        Assert.EndsWith("-c:a copy -movflags +faststart -f mov v.mov", Line(ExportEncoding.Default with { Container = ExportContainer.Mov }, "v.mov"));
        Assert.EndsWith("-c:a copy -cues_to_front 1 -f matroska v.mkv", Line(ExportEncoding.Default with { Container = ExportContainer.Mkv }, "v.mkv"));
        // Matroska has no hvc1 tag.
        Assert.DoesNotContain("hvc1", Line(ExportEncoding.Default with { Codec = ExportVideoCodec.Hevc, Container = ExportContainer.Mkv }, "v.mkv"));
        Assert.Equal((".mp4", ".mov", ".mkv"), (ExportFormat.ExtensionOf(ExportContainer.Mp4), ExportFormat.ExtensionOf(ExportContainer.Mov), ExportFormat.ExtensionOf(ExportContainer.Mkv)));
    }

    // One theory per codec and encoder, each skipped (with the reason) where this machine's ffmpeg has no such encoder.

    [FfmpegTheory, InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mov), InlineData(ExportContainer.Mkv)]
    public Task H264_on_the_processor(ExportContainer container) => Encodes(ExportVideoCodec.H264, ExportVideoEncoder.Software, container);

    [EncoderTheory(ExportVideoCodec.H264, ExportVideoEncoder.Nvenc), InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mov), InlineData(ExportContainer.Mkv)]
    public Task H264_on_nvenc(ExportContainer container) => Encodes(ExportVideoCodec.H264, ExportVideoEncoder.Nvenc, container);

    [EncoderTheory(ExportVideoCodec.Hevc, ExportVideoEncoder.Software), InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mov), InlineData(ExportContainer.Mkv)]
    public Task Hevc_on_the_processor(ExportContainer container) => Encodes(ExportVideoCodec.Hevc, ExportVideoEncoder.Software, container);

    [EncoderTheory(ExportVideoCodec.Hevc, ExportVideoEncoder.Nvenc), InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mov), InlineData(ExportContainer.Mkv)]
    public Task Hevc_on_nvenc(ExportContainer container) => Encodes(ExportVideoCodec.Hevc, ExportVideoEncoder.Nvenc, container);

    [EncoderTheory(ExportVideoCodec.Av1, ExportVideoEncoder.Software), InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mkv)]
    public Task Av1_on_the_processor(ExportContainer container) => Encodes(ExportVideoCodec.Av1, ExportVideoEncoder.Software, container);

    [EncoderTheory(ExportVideoCodec.Av1, ExportVideoEncoder.Nvenc), InlineData(ExportContainer.Mp4), InlineData(ExportContainer.Mkv)]
    public Task Av1_on_nvenc(ExportContainer container) => Encodes(ExportVideoCodec.Av1, ExportVideoEncoder.Nvenc, container);

    /// <summary>Every frame in order, the exact rate, the audio sample-exact, the tags, the codec and the container.</summary>
    private async Task Encodes(ExportVideoCodec codec, ExportVideoEncoder encoder, ExportContainer container)
    {
        var available = NvencProbe.Value;
        var settings = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { Codec = codec, Encoder = encoder, Container = container };
        var path = Path.Combine(_dir, $"out{ExportFormat.ExtensionOf(container)}");
        var output = Output(320, 180, FrameRate.Ntsc30, 60);
        var encoderImpl = new FfmpegExportEncoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegExportEncoder>.Instance, new Fixed(available));
        await using (var encoding = await encoderImpl.StartAsync(output, settings, path))
        {
            Assert.Equal(encoder == ExportVideoEncoder.Nvenc, encoding.IsHardware);
            Assert.Null(encoding.FallbackReason);
            var chunk = new float[2 * 4_800];
            for (long k = 0; k < output.AudioSampleCount; k += 4_800)
            {
                var n = (int)Math.Min(4_800, output.AudioSampleCount - k);
                for (var i = 0; i < n; i++) chunk[2 * i] = chunk[2 * i + 1] = k + i == 4_800 ? 0.9f : 0f;   // one click at 0.1 s
                await encoding.WriteAudioAsync(chunk.AsMemory(0, 2 * n));
            }
            var stride = output.Size.Width * 4;
            var pixels = new byte[stride * output.Size.Height];
            for (long n = 0; n < output.FrameCount; n++)
            {
                NumberFrame(n, pixels, stride, output.Size.Width, output.Size.Height);
                await encoding.WriteFrameAsync(pixels, stride);
            }
            await encoding.CompleteAsync();
        }

        var video = Stream(path, "video");
        _log.WriteLine($"{codec} {encoder} {container}: {video}");
        var name = codec switch { ExportVideoCodec.Hevc => "hevc", ExportVideoCodec.Av1 => "av1", _ => "h264" };
        Assert.Equal((name, "320", "180", "30000/1001", "yuv420p", "tv", "bt709", "bt709", "bt709"),
            (video.Str("codec_name"), video.Str("width"), video.Str("height"), video.Str("r_frame_rate"), video.Str("pix_fmt"),
             video.Str("color_range"), video.Str("color_space"), video.Str("color_transfer"), video.Str("color_primaries")));
        if (codec == ExportVideoCodec.Hevc && container != ExportContainer.Mkv) Assert.Equal("hvc1", video.Str("codec_tag_string"));
        Assert.Equal(Enumerable.Range(0, 60), DecodeFrames(path, 320, 180).Select(f => ReadNumber(f, 320, 180)));
        var left = DecodeLeft(path);
        Assert.Equal(output.AudioSampleCount, left.Length);
        Assert.Equal(4_800, Array.IndexOf(left, left.MaxBy(Math.Abs)));   // the click on its sample: no shift by the container
        Assert.Empty(Directory.GetFiles(_dir, ".*"));                      // no temporary file left
    }

    [FfmpegFact]
    public async Task A_codec_this_ffmpeg_cannot_encode_fails_before_anything_is_written()
    {
        var none = ExportEncoderAvailability.SoftwareOnly with { SoftwareAv1 = false };
        var encoder = new FfmpegExportEncoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegExportEncoder>.Instance, new Fixed(none));
        var path = Path.Combine(_dir, "av1.mp4");

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            encoder.StartAsync(Output(160, 90, FrameRate.Fps25, 25), ExportEncoding.Default with { Codec = ExportVideoCodec.Av1 }, path));

        Assert.Equal(ExportFailure.CodecUnavailable, error.Failure);
        Assert.Contains("libsvtav1", error.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private sealed class Fixed(ExportEncoderAvailability value) : IExportEncoderCapabilities
    {
        public Task<ExportEncoderAvailability> GetAsync(CancellationToken ct = default) => Task.FromResult(value);
    }
}

/// <summary>D076: skips (with the reason) unless this machine's ffmpeg encodes <c>codec</c> with <c>encoder</c> — the app's own
/// check (<see cref="FfmpegEncoderCapabilities"/>).</summary>
public sealed class EncoderTheoryAttribute : TheoryAttribute
{
    public EncoderTheoryAttribute(ExportVideoCodec codec, ExportVideoEncoder encoder)
    {
        if (FfmpegTools.SkipReason is { } none) { Skip = none; return; }
        var available = NvencProbe.Value;
        var can = encoder == ExportVideoEncoder.Nvenc ? available.NvencFor(codec) : available.SoftwareFor(codec);
        if (!can) Skip = encoder == ExportVideoEncoder.Nvenc ? $"NVENC {codec} unavailable here" : $"this ffmpeg has no software {codec} encoder";
    }
}
