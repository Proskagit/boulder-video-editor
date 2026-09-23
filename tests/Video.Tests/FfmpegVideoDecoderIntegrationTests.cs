using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// End-to-end: real ffmpeg decode → real PTS (showinfo) → <see cref="DecodedFrame"/> →
/// <see cref="SourceFrameSelector"/> → the frame number read back from the pixels must equal
/// the number the D009 rule gives for the generation recipe's intended frame times.
/// </summary>
[Collection(MediaCollection.Name)]
public class FfmpegVideoDecoderIntegrationTests
{
    private const int Window = 45;
    private static readonly Rational Millisecond = Rational.Of(1, 1000);

    private readonly TestMedia _media;
    private readonly ITestOutputHelper _output;

    public FfmpegVideoDecoderIntegrationTests(TestMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    // --- Rate conversion and same rate, at the start / middle / trimmed / end of the source ----

    public static TheoryData<string, int, int, string> RateCases()
    {
        var data = new TheoryData<string, int, int, string>();
        var pairs = new (string File, FrameRate Project)[]
        {
            ("cfr25.mp4", FrameRate.Ntsc30),     // 25 → 29.97
            ("cfr24.mp4", FrameRate.Ntsc30),     // 24 → 29.97
            ("cfr2997.mp4", FrameRate.Fps24),    // 29.97 → 24
            ("cfr5994.mp4", FrameRate.Ntsc30),   // 59.94 → 29.97
            ("cfr25.mp4", FrameRate.Fps25),      // same rate
            ("cfr2997.mp4", FrameRate.Ntsc30),   // same rate, NTSC
            ("hevc2997.mp4", FrameRate.Ntsc30),  // H.265, same rate
        };
        foreach (var (file, project) in pairs)
            foreach (var position in new[] { "first", "middle", "trim", "last" })
                data.Add(file, project.Numerator, project.Denominator, position);
        return data;
    }

    [FfmpegTheory]
    [MemberData(nameof(RateCases))]
    public async Task SelectedFrames_MatchExpected(string fileName, int projectNum, int projectDen, string position)
    {
        var file = _media.Get(fileName);
        var project = new FrameRate(projectNum, projectDen);
        var (clipStart, sourceIn, firstFrame) = Place(file, project, position);

        var outcome = await DecodeAsync(file, project, clipStart, sourceIn, firstFrame, Window);

        AssertOutcome(file, outcome);
    }

    [FfmpegTheory]
    [InlineData("first")]
    [InlineData("middle")]
    [InlineData("last")]
    public async Task Vfr_DroppedFrames_HoldThePreviousFrame(string position)
    {
        var file = _media.Get("vfr.mkv");
        var (clipStart, sourceIn, firstFrame) = position == "middle"
            ? (MediaTime.Zero, MediaTime.FromSeconds(1.6), 0L) // window 1.6–3.4 s spans the dropped odd frames
            : Place(file, FrameRate.Fps25, position);

        var outcome = await DecodeAsync(file, FrameRate.Fps25, clipStart, sourceIn, firstFrame, Window);

        AssertOutcome(file, outcome);
        if (position == "middle")
            Assert.Contains(outcome.Selected.Zip(outcome.Selected.Skip(1)), pair => pair.First == pair.Second); // a held frame
    }

    [FfmpegTheory]
    [InlineData("first")]
    [InlineData("middle")]
    [InlineData("last")]
    public async Task JitteredPts_SameRate_IsIdentity(string position)
    {
        var file = _media.Get("jitter.mp4");
        var (clipStart, sourceIn, firstFrame) = Place(file, FrameRate.Fps25, position);
        if (position == "last") // keep SourceIn on the 25 fps grid so no sample point is within 1 ms of a jittered frame
            sourceIn = MediaTime.FromFrame(file.Metadata.Duration.ToFrameFloor(FrameRate.Fps25) - Window, FrameRate.Fps25);

        var outcome = await DecodeAsync(file, FrameRate.Fps25, clipStart, sourceIn, firstFrame, Window);

        AssertUnambiguous(file, outcome.Points, Millisecond);
        AssertOutcome(file, outcome);
        Assert.Equal(outcome.Selected.Distinct().Count(), outcome.Selected.Count); // no duplicates or skips
        Assert.All(outcome.Selected.Zip(outcome.Selected.Skip(1)), p => Assert.Equal(p.First + 1, p.Second));
    }

    // --- start_time ---------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData("video.ts", "first")]
    [InlineData("video.ts", "middle")]
    [InlineData("offset.ts", "first")]
    [InlineData("offset.ts", "middle")]
    [InlineData("offset.ts", "last")]
    public async Task NonZeroStartTime_IsTheOrigin(string fileName, string position)
    {
        var file = _media.Get(fileName);
        Assert.True(file.Metadata.StartTime is { } start && start > MediaTime.FromSeconds(1), "expected an MPEG-TS start_time > 1 s");

        var (clipStart, sourceIn, firstFrame) = Place(file, FrameRate.Fps25, position);
        var outcome = await DecodeAsync(file, FrameRate.Fps25, clipStart, sourceIn, firstFrame, Window);

        AssertOutcome(file, outcome);
        _output.WriteLine($"{fileName}: format start_time {file.Metadata.StartTime}, first video frame at source time {file.Frames[0].Time}");
    }

    [FfmpegFact]
    public async Task VideoStartingAfterAudio_HoldsFirstFrameUntilItsSourceTime()
    {
        // format start_time comes from the audio; video starts ≈0.34 s later. Source time 0
        // must be the format start (D009), so the first timeline frames hold video frame 0.
        var file = _media.Get("offset.ts");
        var videoOffset = file.Frames[0].Time;
        Assert.True(videoOffset > Rational.Of(3, 10) && videoOffset < Rational.Of(4, 10), $"video offset {videoOffset}");

        var outcome = await DecodeAsync(file, FrameRate.Fps25, MediaTime.Zero, MediaTime.Zero, 0, 20);

        AssertOutcome(file, outcome);
        // Timeline frame n samples at n/25 + 20 ms. Frame 0 is shown (held before its own start,
        // then as itself) until the sample point reaches frame 1 at offset + 40 ms.
        var frame1 = videoOffset + Rational.Of(1, 25);
        var expectedHeld = Enumerable.Range(0, 20).Count(n => Rational.Of(2 * n + 1, 50) < frame1);
        var held = outcome.Selected.TakeWhile(n => n == 0).Count();
        _output.WriteLine($"video offset {videoOffset}, held {held}, selected [{string.Join(",", outcome.Selected)}]");
        Assert.True(expectedHeld >= 8, $"expected several held frames, offset {videoOffset}");
        Assert.Equal(expectedHeld, held);
        Assert.Equal(1, outcome.Selected[held]);
    }

    // --- Sparse VFR, end of stream, preroll limits ----------------------------------------------

    [FfmpegTheory]
    [InlineData(4.0, 10)]  // inside the 2–8 s gap: hold frame 49
    [InlineData(7.8, 12)]  // across the end of the gap
    public async Task SparseVfr_GrowsPrerollUntilTheFrameIsReached(double sourceInSeconds, int count)
    {
        var file = _media.Get("sparse.mkv");
        var outcome = await DecodeAsync(file, FrameRate.Fps25, MediaTime.Zero, MediaTime.FromSeconds(sourceInSeconds), 0, count);

        AssertOutcome(file, outcome);
        Assert.Equal(49, outcome.Selected[0]);
        Assert.True(outcome.Attempts > 1, "a 2-frame preroll cannot reach across a 6 s gap");
        _output.WriteLine($"attempts: {outcome.Attempts}, args: {string.Join(' ', outcome.Arguments)}");
    }

    [FfmpegFact]
    public async Task SparseVfr_BeyondMaxPreroll_FailsWithControlledError()
    {
        var file = _media.Get("sparse.mkv");
        var settings = new FfmpegVideoDecoderSettings { MaxPreroll = TimeSpan.FromSeconds(1) };

        var ex = await Assert.ThrowsAsync<VideoDecodeException>(() =>
            DecodeAsync(file, FrameRate.Fps25, MediaTime.Zero, MediaTime.FromSeconds(5), 0, 5, settings: settings));
        Assert.Equal(VideoDecodeError.FrameNotReached, ex.Error);
    }

    [FfmpegFact]
    public async Task ContainerLongerThanVideo_HoldsTheLastFrame()
    {
        // Sample points at 5.0 s+ while the video ends at 3.96 s: the first seek yields no frames.
        var file = _media.Get("longaudio.mp4");
        var outcome = await DecodeAsync(file, FrameRate.Fps25, MediaTime.Zero, MediaTime.FromSeconds(5), 0, 10);

        AssertOutcome(file, outcome);
        Assert.All(outcome.Selected, n => Assert.Equal(99, n));
        Assert.True(outcome.Attempts > 1);
    }

    [FfmpegFact]
    public async Task LastFrameOfSource_IsReachedAndHeld()
    {
        var file = _media.Get("cfr25.mp4");
        // Window ends exactly at the source end; the last point samples 11.98 s → frame 299.
        var sourceIn = file.Metadata.Duration - MediaTime.FromFrame(10, FrameRate.Fps25);
        var outcome = await DecodeAsync(file, FrameRate.Fps25, MediaTime.Zero, sourceIn, 0, 10);

        AssertOutcome(file, outcome);
        Assert.Equal(299, outcome.Selected[^1]);
    }

    // --- Hardware vs software, scaling, errors ----------------------------------------------------

    [FfmpegTheory]
    [InlineData("cfr25.mp4", 30000, 1001)]
    [InlineData("cfr2997.mp4", 30000, 1001)]
    [InlineData("cfr5994.mp4", 30000, 1001)]
    [InlineData("hevc2997.mp4", 30000, 1001)]
    [InlineData("vfr.mkv", 25, 1)]
    [InlineData("jitter.mp4", 25, 1)]
    [InlineData("offset.ts", 25, 1)]
    public async Task HardwareAuto_SelectsTheSameFramesAsSoftware(string fileName, int projectNum, int projectDen)
    {
        var file = _media.Get(fileName);
        var project = new FrameRate(projectNum, projectDen);
        var (clipStart, sourceIn, firstFrame) = Place(file, project, "middle");

        var software = await DecodeAsync(file, project, clipStart, sourceIn, firstFrame, Window, HardwareDecoding.Disabled);
        var hardware = await DecodeAsync(file, project, clipStart, sourceIn, firstFrame, Window, HardwareDecoding.Auto);

        AssertOutcome(file, hardware);
        Assert.Equal(software.Selected, hardware.Selected);
        Assert.Equal(software.Frames.Select(f => (f.Frame.Timestamp, f.Number)), hardware.Frames.Select(f => (f.Frame.Timestamp, f.Number)));
    }

    [FfmpegTheory]
    [InlineData(HardwareDecoding.Disabled)]
    [InlineData(HardwareDecoding.Auto)]
    public async Task LongSequence_EveryFrameIsDelivered_WithoutPipeStall(HardwareDecoding hardware)
    {
        // 300 frames → ~90 KB of showinfo lines on stderr, far beyond an OS pipe buffer:
        // only possible if stderr is drained independently of the stdout frame reads.
        var file = _media.Get("cfr25.mp4");
        var point = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, 0, FrameRate.Fps25, FrameRate.Fps25);
        await using var stream = await NewDecoder().OpenStreamAsync(
            new VideoDecodeRequest { FilePath = file.Path, FirstSamplePoint = point, NominalFrameRate = FrameRate.Fps25, Hardware = hardware },
            CancellationToken.None);

        var numbers = new List<long>();
        var pts = new List<long>();
        while (await stream.ReadFrameAsync() is { } frame)
        {
            numbers.Add(ReadNumber(frame));
            pts.Add(frame.Timestamp.Pts);
        }

        Assert.Equal(Enumerable.Range(0, 300).Select(i => (long)i), numbers);
        Assert.True(pts.Zip(pts.Skip(1)).All(p => p.First < p.Second), "PTS must increase");
    }

    [FfmpegFact]
    public async Task FailingHardwareAccelerator_RelaunchesInSoftware()
    {
        // An accelerator ffmpeg rejects (exit code ≠ 0) stands in for an unavailable GPU decoder.
        var file = _media.Get("cfr25.mp4");
        var settings = new FfmpegVideoDecoderSettings { HardwareAccelerator = "nonexistent" };

        var outcome = await DecodeAsync(file, FrameRate.Ntsc30, MediaTime.Zero, MediaTime.FromSeconds(3), 0, 20,
            HardwareDecoding.Auto, settings: settings);

        AssertOutcome(file, outcome);
        Assert.DoesNotContain("-hwaccel", outcome.Arguments);
        Assert.Equal(1, outcome.Attempts); // the software relaunch does not count as a preroll attempt
    }

    [FfmpegFact]
    public async Task FramesAreScaledDownToTheRequestedMaximum()
    {
        var file = _media.Get("cfr25.mp4");
        var outcome = await DecodeAsync(file, FrameRate.Ntsc30, MediaTime.Zero, MediaTime.FromSeconds(2), 0, 20, maxWidth: 128, maxHeight: 72);

        AssertOutcome(file, outcome);
        Assert.All(outcome.Frames, f => Assert.Equal((128, 72, 128 * 4), (f.Frame.Width, f.Frame.Height, f.Frame.Stride)));
    }

    [FfmpegFact]
    public async Task MissingFile_And_InvalidFile_FailWithControlledErrors()
    {
        var decoder = NewDecoder();
        var point = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, 0, FrameRate.Fps25, null);

        var missing = await Assert.ThrowsAsync<VideoDecodeException>(() =>
            decoder.OpenAsync(new VideoDecodeRequest { FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"), FirstSamplePoint = point }));
        Assert.Equal(VideoDecodeError.FileNotFound, missing.Error);

        var garbage = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        await File.WriteAllBytesAsync(garbage, Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 31 % 251)).ToArray());
        try
        {
            var invalid = await Assert.ThrowsAsync<VideoDecodeException>(() =>
                decoder.OpenAsync(new VideoDecodeRequest { FilePath = garbage, FirstSamplePoint = point }));
            Assert.Contains(invalid.Error, new[] { VideoDecodeError.DecoderFailed, VideoDecodeError.NoVideo });
            _output.WriteLine(invalid.Message);
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    // --- Helpers ------------------------------------------------------------------------------------

    internal sealed record Outcome(
        IReadOnlyList<long> Selected, IReadOnlyList<long> Expected, IReadOnlyList<(DecodedFrame Frame, long Number)> Frames,
        IReadOnlyList<SourceSamplePoint> Points, int Attempts, IReadOnlyList<string> Arguments);

    /// <summary>Clip placement for a named position: returns clip start, SourceIn and the first
    /// timeline frame of the <see cref="Window"/>-frame window.</summary>
    private static (MediaTime ClipStart, MediaTime SourceIn, long FirstFrame) Place(MediaFile file, FrameRate project, string position)
    {
        var duration = file.Metadata.Duration;
        return position switch
        {
            "first" => (MediaTime.Zero, MediaTime.Zero, 0),
            // Clip at frame 37, SourceIn on the project grid in the middle of the source.
            "middle" => (MediaTime.FromFrame(37, project), MediaTime.FromFrame(duration.ToFrameFloor(project) / 2, project), 37),
            // Arbitrary trimmed SourceIn (not on any frame grid), clip at frame 300.
            "trim" => (MediaTime.FromFrame(300, project), new MediaTime(duration.Ticks / 3 + 123_457), 300),
            // Window ends exactly at the end of the source.
            "last" => (MediaTime.FromFrame(100, project), duration - (MediaTime.FromFrame(100 + Window, project) - MediaTime.FromFrame(100, project)), 100),
            _ => throw new ArgumentOutOfRangeException(nameof(position))
        };
    }

    private static FfmpegVideoDecoder NewDecoder(FfmpegVideoDecoderSettings? settings = null) =>
        new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance, settings);

    private static async Task<Outcome> DecodeAsync(MediaFile file, FrameRate project, MediaTime clipStart, MediaTime sourceIn,
        long firstFrame, int count, HardwareDecoding hardware = HardwareDecoding.Disabled,
        int maxWidth = 1280, int maxHeight = 720, FfmpegVideoDecoderSettings? settings = null)
    {
        var nominal = SourceFrameSelector.NominalRate(file.Metadata);
        var origin = file.Metadata.StartTime ?? MediaTime.Zero;
        var points = Enumerable.Range(0, count)
            .Select(i => SourceFrameSelector.SamplePoint(clipStart, sourceIn, firstFrame + i, project, nominal))
            .ToList();

        var request = new VideoDecodeRequest
        {
            FilePath = file.Path,
            StartTime = origin,
            FirstSamplePoint = points[0],
            NominalFrameRate = nominal,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Hardware = hardware
        };

        await using var stream = await NewDecoder(settings).OpenStreamAsync(request, CancellationToken.None);
        var frames = new List<(DecodedFrame Frame, long Number)>();
        while (frames.Count < 5_000 && await stream.ReadFrameAsync() is { } frame)
        {
            frames.Add((frame, ReadNumber(frame)));
            if (!SourceFrameSelector.IsAtOrBefore(frame.Timestamp, origin, points[^1]))
                break; // past the last sample point
        }

        var timestamps = frames.Select(f => f.Frame.Timestamp).ToList();
        var selected = points.Select(p => frames[SourceFrameSelector.Select(timestamps, origin, p)].Number).ToList();
        var expected = points.Select(p => ExpectedNumber(file, p)).ToList();
        return new Outcome(selected, expected, frames, points, stream.Attempts, stream.Arguments);
    }

    /// <summary>The D009 rule applied to the recipe's intended frame times (not decoder output).</summary>
    private static long ExpectedNumber(MediaFile file, SourceSamplePoint point)
    {
        var at = Seconds(point);
        var last = file.Frames.LastOrDefault(f => f.Time <= at);
        return last == default ? file.Frames[0].Number : last.Number;
    }

    private static Rational Seconds(SourceSamplePoint p) =>
        Rational.Of(System.Numerics.BigInteger.Parse(p.TicksNumerator.ToString()), (System.Numerics.BigInteger)p.TicksDenominator * TimeSpan.TicksPerSecond);

    private void AssertOutcome(MediaFile file, Outcome outcome)
    {
        _output.WriteLine($"{file.Name}: attempts {outcome.Attempts}, decoded {outcome.Frames.Count}, selected [{string.Join(",", outcome.Selected)}]");
        AssertPixelsMatchTimestamps(file, outcome.Frames);
        Assert.True(outcome.Expected.SequenceEqual(outcome.Selected),
            $"{file.Name}: expected [{string.Join(",", outcome.Expected)}] got [{string.Join(",", outcome.Selected)}]");
    }

    /// <summary>Every decoded frame's pixel number belongs to the frame the recipe placed at its
    /// PTS (within 1 ms) — proves PTS and pixels are paired correctly.</summary>
    private static void AssertPixelsMatchTimestamps(MediaFile file, IReadOnlyList<(DecodedFrame Frame, long Number)> frames)
    {
        Assert.NotEmpty(frames);
        var origin = Rational.FromTicks((file.Metadata.StartTime ?? MediaTime.Zero).Ticks);
        foreach (var (frame, number) in frames)
        {
            var ts = frame.Timestamp;
            var time = Rational.Of((System.Numerics.BigInteger)ts.Pts * ts.TimeBase.Numerator, ts.TimeBase.Denominator) - origin;
            var match = file.Frames.FirstOrDefault(f => (f.Time - time).Abs() <= Millisecond);
            Assert.True(match != default, $"{file.Name}: no generated frame near source time {time} (pts {ts.Pts}·{ts.TimeBase})");
            Assert.True(match.Number == number, $"{file.Name}: pixels say frame {number}, pts {ts.Pts}·{ts.TimeBase} = {time} is frame {match.Number}");
        }
    }

    /// <summary>For files with approximate timestamps: no intended frame time may be within
    /// <paramref name="margin"/> of a sample point, or the expected value would be ill-defined.</summary>
    private static void AssertUnambiguous(MediaFile file, IEnumerable<SourceSamplePoint> points, Rational margin)
    {
        foreach (var p in points)
        {
            var at = Seconds(p);
            Assert.DoesNotContain(file.Frames, f => (f.Time - at).Abs() < margin);
        }
    }

    /// <summary>Reads the 16-bit frame number from the bit columns (green channel at mid-height).</summary>
    internal static long ReadNumber(DecodedFrame frame)
    {
        var pixels = frame.Pixels.Span;
        var y = frame.Height / 2;
        long number = 0;
        for (var bit = 0; bit < 16; bit++)
        {
            var x = (16 * bit + 8) * frame.Width / 256;
            if (pixels[y * frame.Stride + x * DecodedFrame.BytesPerPixel + 1] > 128)
                number |= 1L << bit;
        }
        return number;
    }
}
