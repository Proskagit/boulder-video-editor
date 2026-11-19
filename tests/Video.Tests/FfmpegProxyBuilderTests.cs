using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D071 §3: the proxy keeps every frame of the source with its exact timestamp (CFR, NTSC, HEVC, VFR, a sparse file, jittered
/// timestamps, MPEG-TS with an offset start, a scaled 1080p source), so the Preview shows from it the same frame as from the
/// source — decoded from the start and after a seek; an HDR source's proxy is the Preview's SDR picture; a cancelled or failed
/// making leaves nothing.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegProxyBuilderTests : IDisposable
{
    private readonly TestMedia _media;
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-proxy-tests", Guid.NewGuid().ToString("N"));

    public FfmpegProxyBuilderTests(TestMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static FfmpegProxyBuilder Builder(IHdrLutStore? luts = null) =>
        new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegProxyBuilder>.Instance, luts);

    private static FfmpegVideoDecoder Decoder(IHdrLutStore? luts = null) =>
        new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance,
            new FfmpegVideoDecoderSettings { FrameTimeout = FfmpegTools.ProcessTimeout }, luts);

    private async Task<string> BuildAsync(MediaFile file, FrameSize size)
    {
        var output = Path.Combine(_dir, Path.GetFileNameWithoutExtension(file.Name) + "-proxy.mp4");
        await Builder().BuildAsync(new ProxyBuildRequest
        {
            SourcePath = file.Path, OutputPath = output, Size = size, Duration = file.Metadata.Duration
        });
        return output;
    }

    private static async Task<List<DecodedFrame>> DecodeAllAsync(string path, MediaTime start, HdrConversion? hdr = null, IHdrLutStore? luts = null)
    {
        await using var stream = await Decoder(luts).OpenAsync(new VideoDecodeRequest
        {
            FilePath = path, StartTime = start, FirstSamplePoint = new SourceSamplePoint(0, 1), MaxWidth = 4096, MaxHeight = 4096,
            Hardware = HardwareDecoding.Disabled, StrictEnd = true, Hdr = hdr
        });
        var frames = new List<DecodedFrame>();
        while (await stream.ReadFrameAsync() is { } frame) frames.Add(frame);
        return frames;
    }

    private static bool SameTime(SourceTimestamp a, SourceTimestamp b) =>
        (Int128)a.Pts * a.TimeBase.Numerator * b.TimeBase.Denominator == (Int128)b.Pts * b.TimeBase.Numerator * a.TimeBase.Denominator;

    public static TheoryData<string> Sources() => new()
    {
        "cfr25.mp4", "cfr2997.mp4", "cfr5994.mp4", "hevc2997.mp4", "vfr.mkv", "sparse.mkv", "jitter.mp4", "offset.ts", "video.ts"
    };

    [FfmpegTheory]
    [MemberData(nameof(Sources))]
    public async Task The_proxy_has_every_frame_of_the_source_with_its_exact_timestamp(string name)
    {
        var file = _media.Get(name);
        var proxy = await BuildAsync(file, new FrameSize(256, 144));
        var start = file.Metadata.StartTime ?? MediaTime.Zero;

        var source = await DecodeAllAsync(file.Path, start);
        var copy = await DecodeAllAsync(proxy, start);

        _output.WriteLine($"{name}: {source.Count} frames, proxy {copy.Count}, {new FileInfo(proxy).Length} bytes");
        Assert.Equal(source.Count, copy.Count);
        for (var i = 0; i < source.Count; i++)
        {
            Assert.True(SameTime(source[i].Timestamp, copy[i].Timestamp), $"frame {i}: {source[i].Timestamp} ≠ {copy[i].Timestamp}");
            Assert.Equal(FfmpegVideoDecoderIntegrationTests.ReadNumber(source[i]), FfmpegVideoDecoderIntegrationTests.ReadNumber(copy[i]));
        }
    }

    [FfmpegFact]
    public async Task A_scaled_proxy_of_a_1080p_source_keeps_its_frames()
    {
        var file = _media.Get("hd2997.mp4");
        var proxy = await BuildAsync(file, PreviewDecoding.ProxySize(PreviewQuality.Low, 1920, 1080));
        var start = file.Metadata.StartTime ?? MediaTime.Zero;

        var copy = await DecodeAllAsync(proxy, start);
        Assert.Equal(file.Frames.Count, copy.Count);
        Assert.All(copy, f => Assert.Equal((1280, 720), (f.Width, f.Height)));
        Assert.Equal(file.Frames.Select(f => f.Number), copy.Select(FfmpegVideoDecoderIntegrationTests.ReadNumber));
    }

    [FfmpegTheory]
    [InlineData("hevc2997.mp4")]
    [InlineData("jitter.mp4")]
    [InlineData("offset.ts")]
    public async Task After_a_seek_the_proxy_shows_the_frame_the_source_shows(string name)
    {
        var file = _media.Get(name);
        var proxy = await BuildAsync(file, new FrameSize(256, 144));
        var start = file.Metadata.StartTime ?? MediaTime.Zero;
        var frames = file.Frames;
        foreach (var k in new[] { 3, frames.Count / 3, frames.Count / 2, frames.Count - 5 })
        {
            // Halfway between two frames: the frame k is the one shown.
            var mid = (frames[k].Time.ToDouble() + frames[k + 1].Time.ToDouble()) / 2;
            var point = new SourceSamplePoint((Int128)Math.Round(mid * TimeSpan.TicksPerSecond), 1);
            Assert.Equal(frames[k].Number, await ShownAsync(file.Path, start, point));
            Assert.Equal(frames[k].Number, await ShownAsync(proxy, start, point));
        }
    }

    /// <summary>The frame shown at <paramref name="point"/>: the last one at or before it (D009).</summary>
    private static async Task<long> ShownAsync(string path, MediaTime start, SourceSamplePoint point)
    {
        await using var stream = await Decoder().OpenAsync(new VideoDecodeRequest
        {
            FilePath = path, StartTime = start, FirstSamplePoint = point, MaxWidth = 4096, MaxHeight = 4096, Hardware = HardwareDecoding.Disabled
        });
        DecodedFrame? shown = null;
        while (await stream.ReadFrameAsync() is { } frame && SourceFrameSelector.IsAtOrBefore(frame.Timestamp, start, point)) shown = frame;
        return FfmpegVideoDecoderIntegrationTests.ReadNumber(shown ?? throw new InvalidOperationException("no frame at the point"));
    }

    [FfmpegFact]
    public async Task An_HDR_source_gives_an_SDR_proxy_with_the_Preview_picture()
    {
        var luts = new HdrLutStore(Path.Combine(_dir, "luts"), NullLogger<HdrLutStore>.Instance);
        var hdr = HdrConversion.Hlg();
        var output = Path.Combine(_dir, "hlg-proxy.mp4");
        await Builder(luts).BuildAsync(new ProxyBuildRequest
        {
            SourcePath = HdrMedia.Hlg, OutputPath = output, Size = new FrameSize(HdrMedia.Width, HdrMedia.Height), Hdr = hdr,
            Duration = MediaTime.FromSeconds(0.4)
        });

        var source = (await DecodeAllAsync(HdrMedia.Hlg, MediaTime.Zero, hdr, luts))[0];
        var proxy = (await DecodeAllAsync(output, MediaTime.Zero))[0];   // the SDR path, as the Preview decodes a proxy
        for (var row = 0; row < HdrMedia.Rows; row++)
        for (var column = 0; column < HdrMedia.Columns; column++)
        {
            var a = Mean(source, column, row);
            var b = Mean(proxy, column, row);
            _output.WriteLine($"patch {column},{row}: source {string.Join('/', a.Select(v => v.ToString("0.0")))} proxy {string.Join('/', b.Select(v => v.ToString("0.0")))}");
            for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(a[c] - b[c]), 0, 4);
        }
    }

    private static double[] Mean(DecodedFrame frame, int column, int row)
    {
        var (x0, y0, w, h) = HdrMedia.Centre(column, row, frame.Width, frame.Height);
        var sum = new double[3];
        var pixels = frame.Pixels.Span;
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = y * frame.Stride + x * DecodedFrame.BytesPerPixel;
            sum[0] += pixels[i + 2];
            sum[1] += pixels[i + 1];
            sum[2] += pixels[i];
        }
        return sum.Select(s => s / (w * h)).ToArray();
    }

    [FfmpegFact]
    public async Task A_rotated_phone_video_gives_an_upright_proxy_that_is_not_turned_again()
    {
        // A portrait phone video: a landscape stream with a display matrix of 90°. The proxy is made display-oriented and
        // carries no matrix, so decoding it (with -autorotate, as the Preview does) gives the source's own upright picture.
        var plain = Path.Combine(_dir, "plain.mp4");
        var rotated = Path.Combine(_dir, "rotated.mp4");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=1",
            "-c:v", "libx264", "-preset", "ultrafast", plain });
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated });
        var output = Path.Combine(_dir, "rotated-proxy.mp4");
        await Builder().BuildAsync(new ProxyBuildRequest { SourcePath = rotated, OutputPath = output, Size = new FrameSize(180, 320) });

        var source = await DecodeAllAsync(rotated, MediaTime.Zero);
        var proxy = await DecodeAllAsync(output, MediaTime.Zero);
        Assert.Equal((180, 320), (source[0].Width, source[0].Height));
        Assert.All(proxy, f => Assert.Equal((180, 320), (f.Width, f.Height)));
        Assert.Equal(source.Count, proxy.Count);
    }

    [FfmpegFact]
    public async Task A_cancelled_making_leaves_no_file_and_no_ffmpeg()
    {
        var file = _media.Get("cfr25.mp4");
        var output = Path.Combine(_dir, "cancelled.mp4");
        var before = FfmpegProcess.LiveProcesses;
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cancel.Cancel());   // cancelled at its first progress
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Builder().BuildAsync(new ProxyBuildRequest
        {
            SourcePath = file.Path, OutputPath = output, Size = new FrameSize(256, 144), Duration = file.Metadata.Duration
        }, progress, cancel.Token));
        Assert.False(File.Exists(output));
        Assert.Equal(before, FfmpegProcess.LiveProcesses);
    }

    [FfmpegFact]
    public async Task A_source_that_cannot_be_read_fails_without_a_file()
    {
        var output = Path.Combine(_dir, "none.mp4");
        await Assert.ThrowsAsync<ProxyBuildException>(() => Builder().BuildAsync(new ProxyBuildRequest
        {
            SourcePath = Path.Combine(_dir, "missing.mp4"), OutputPath = output, Size = new FrameSize(256, 144)
        }));
        var broken = Path.Combine(_dir, "broken.mp4");
        await File.WriteAllBytesAsync(broken, new byte[4096]);
        await Assert.ThrowsAsync<ProxyBuildException>(() => Builder().BuildAsync(new ProxyBuildRequest
        {
            SourcePath = broken, OutputPath = output, Size = new FrameSize(256, 144)
        }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Frames_are_compared_exactly_across_time_bases()
    {
        var a = new[] { new SourceTimestamp(0, new TimeBase(1, 600)), new SourceTimestamp(20, new TimeBase(1, 600)) };
        Assert.Null(FfmpegProxyBuilder.Mismatch(a, new[] { new SourceTimestamp(0, new TimeBase(1, 19200)), new SourceTimestamp(640, new TimeBase(1, 19200)) }));
        Assert.Contains("frame 1", FfmpegProxyBuilder.Mismatch(a, new[] { new SourceTimestamp(0, new TimeBase(1, 600)), new SourceTimestamp(21, new TimeBase(1, 600)) }));
        Assert.Contains("2 frames", FfmpegProxyBuilder.Mismatch(a, a.Take(1).ToList()));
        Assert.NotNull(FfmpegProxyBuilder.Mismatch(Array.Empty<SourceTimestamp>(), Array.Empty<SourceTimestamp>()));
    }

    [Fact]
    public void The_encoding_keeps_the_timestamps_and_puts_a_key_frame_every_half_second()
    {
        var args = FfmpegProxyBuilder.EncodeArguments(new ProxyBuildRequest
        {
            SourcePath = "in.mp4", OutputPath = "out.mp4", Size = new FrameSize(1280, 720)
        }, lutPath: null, hardwareAccelerator: "auto");
        var line = string.Join(' ', args);
        Assert.Contains("-copyts", args);
        Assert.Contains("-fps_mode passthrough -enc_time_base demux", line);
        Assert.Contains("-bf 0", line);
        Assert.Contains("expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+0.5))", line);
        Assert.Contains("showinfo=checksum=0,scale=w=1280:h=720,format=yuv420p", line);
        Assert.DoesNotContain("-colorspace", args);   // an SDR source keeps its own colour tags
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
