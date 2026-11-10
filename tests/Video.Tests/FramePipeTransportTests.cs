using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D059 E1: the export's high-throughput transport (a named pipe with a 64 MB buffer instead of ffmpeg's stdout). The same
/// file decoded through both transports gives the same frames — sizes, timestamps, every byte — from the start, after a
/// seek, at full HD and for a whole long stream; a decoder that fails before it opened the pipe is an error at once (no
/// wait for the frame timeout); cancelling and disposing leave neither an ffmpeg process nor a pipe; the Preview's
/// requests keep stdout.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FramePipeTransportTests(TestMedia media)
{
    private static FfmpegVideoDecoder Decoder() => new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);

    private static VideoDecodeRequest Request(MediaFile file, DecodeTransport transport, long firstFrame = 0, bool strict = true)
    {
        var rate = file.Metadata.FrameRate ?? FrameRate.Fps25;
        return new VideoDecodeRequest
        {
            FilePath = file.Path, StartTime = file.Metadata.StartTime ?? MediaTime.Zero, NominalFrameRate = rate,
            MaxWidth = 16_384, MaxHeight = 16_384, Hardware = HardwareDecoding.Disabled, StrictEnd = strict, Transport = transport,
            FirstSamplePoint = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, firstFrame, rate, rate)
        };
    }

    private static async Task<List<DecodedFrame>> ReadAll(IVideoFrameStream stream, int max = int.MaxValue)
    {
        var frames = new List<DecodedFrame>();
        while (frames.Count < max && await stream.ReadFrameAsync() is { } frame) frames.Add(frame);
        return frames;
    }

    private static void AssertSame(IReadOnlyList<DecodedFrame> expected, IReadOnlyList<DecodedFrame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].Width, expected[i].Height, expected[i].Stride), (actual[i].Width, actual[i].Height, actual[i].Stride));
            Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
            Assert.True(expected[i].Pixels.Span.SequenceEqual(actual[i].Pixels.Span), $"frame {i}: the pixels differ between the transports");
        }
    }

    [FfmpegTheory]
    [InlineData("cfr25.mp4", 0)]
    [InlineData("cfr25.mp4", 120)]
    [InlineData("hd2997.mp4", 0)]
    [InlineData("vfr.mkv", 40)]
    public async Task Both_transports_deliver_the_same_frames(string name, long firstFrame)
    {
        var file = media.Get(name);
        var baseline = FfmpegProcess.LiveProcesses;

        List<DecodedFrame> viaStdout, viaPipe;
        await using (var stdout = await Decoder().OpenStreamAsync(Request(file, DecodeTransport.Standard, firstFrame), CancellationToken.None))
        {
            Assert.False(stdout.UsesPipe);
            viaStdout = await ReadAll(stdout);
        }
        await using (var pipe = await Decoder().OpenStreamAsync(Request(file, DecodeTransport.HighThroughput, firstFrame), CancellationToken.None))
        {
            Assert.Equal(OperatingSystem.IsWindows(), pipe.UsesPipe);
            Assert.Contains("-y", pipe.Arguments);
            Assert.StartsWith(@"\\.\pipe\aive-frames-", pipe.Arguments[^1]);
            viaPipe = await ReadAll(pipe);
        }

        Assert.NotEmpty(viaStdout);
        AssertSame(viaStdout, viaPipe);
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        Assert.Equal(0, FramePipe.Live);
    }

    [FfmpegFact]
    public void The_command_lines_differ_only_in_the_output()
    {
        var file = media.Get("cfr25.mp4");
        var standard = FfmpegVideoDecoder.BuildArguments(Request(file, DecodeTransport.Standard), null, null);
        var piped = FfmpegVideoDecoder.BuildArguments(Request(file, DecodeTransport.HighThroughput), null, null, output: @"\\.\pipe\x");

        Assert.Equal("pipe:1", standard[^1]);
        Assert.Equal(standard.SkipLast(1), piped.SkipLast(2));
        Assert.Equal(new[] { "-y", @"\\.\pipe\x" }, piped.TakeLast(2));
    }

    [FfmpegFact]
    public async Task A_decoder_that_fails_before_opening_the_pipe_is_an_error_at_once()
    {
        // Not a video at all: ffmpeg exits with an error before any frame — and before it opened its output.
        var path = Path.Combine(Path.GetTempPath(), $"aive-pipe-broken-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var baseline = FfmpegProcess.LiveProcesses;
        try
        {
            var file = media.Get("cfr25.mp4") with { Path = path };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<VideoDecodeException>(() => Decoder().OpenStreamAsync(Request(file, DecodeTransport.HighThroughput), CancellationToken.None));
            Assert.Equal(VideoDecodeError.DecoderFailed, error.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");   // the frame timeout is 20 s
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        Assert.Equal(0, FramePipe.Live);
    }

    [FfmpegFact]
    public async Task Cancelling_and_disposing_in_the_middle_leave_no_process_and_no_pipe()
    {
        var file = media.Get("hd2997.mp4");
        var baseline = FfmpegProcess.LiveProcesses;
        using var cts = new CancellationTokenSource();
        await using (var stream = await Decoder().OpenStreamAsync(Request(file, DecodeTransport.HighThroughput), CancellationToken.None))
        {
            Assert.NotNull(await stream.ReadFrameAsync());
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadFrameAsync(cts.Token).AsTask());
        }
        await using (var stream = await Decoder().OpenStreamAsync(Request(file, DecodeTransport.HighThroughput), CancellationToken.None))
        {
            Assert.NotNull(await stream.ReadFrameAsync());   // disposed with ffmpeg still writing into the pipe
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        Assert.Equal(0, FramePipe.Live);
    }

    [FfmpegFact]
    public void The_preview_keeps_stdout()
    {
        Assert.Equal(DecodeTransport.Standard, new VideoDecodeRequest
        {
            FilePath = "x", FirstSamplePoint = SourceFrameSelector.StillImage
        }.Transport);
        Assert.Equal(DecodeTransport.HighThroughput, new AiVideoEditor.Export.ExportDecodeSettings().Transport);
    }
}
