using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class SourceFrameSelectorTests
{
    private static readonly FrameRate Fps50 = new(50, 1);

    /// <summary>CFR frames at <paramref name="rate"/> in time base 1/num (pts = i·den),
    /// the form MP4 muxers produce; <paramref name="startPts"/> offsets the whole stream.</summary>
    private static List<SourceTimestamp> Cfr(FrameRate rate, int count, long startPts = 0)
    {
        var tb = new TimeBase(1, rate.Numerator);
        return Enumerable.Range(0, count).Select(i => new SourceTimestamp(startPts + (long)i * rate.Denominator, tb)).ToList();
    }

    /// <summary>Same frames with pts rounded half-up to whole milliseconds, as MKV stores them.</summary>
    private static List<SourceTimestamp> CfrMilliseconds(FrameRate rate, int count)
    {
        var tb = new TimeBase(1, 1000);
        return Enumerable.Range(0, count)
            .Select(i => new SourceTimestamp((long)((2L * i * 1000 * rate.Denominator + rate.Numerator) / (2L * rate.Numerator)), tb))
            .ToList();
    }

    private static int SelectAt(IReadOnlyList<SourceTimestamp> frames, long timelineFrame, FrameRate project, FrameRate? source,
        MediaTime? startTime = null, MediaTime? clipStart = null, MediaTime? sourceIn = null) =>
        SourceFrameSelector.Select(frames, startTime ?? MediaTime.Zero,
            SourceFrameSelector.SamplePoint(clipStart ?? MediaTime.Zero, sourceIn ?? MediaTime.Zero, timelineFrame, project, source));

    /// <summary>Reference: the source frame index for the exact rational sample point
    /// m/Rp + ½·min(1/Rp, 1/Rs), computed with BigInteger (no ticks involved).</summary>
    internal static long ExactIndex(long m, FrameRate project, FrameRate source)
    {
        // Common denominator 2·pN·sN (all times in seconds).
        BigInteger pN = project.Numerator, pD = project.Denominator, sN = source.Numerator, sD = source.Denominator;
        var denominator = 2 * pN * sN;
        var point = 2 * m * pD * sN + BigInteger.Min(pD * sN, sD * pN);
        return (long)BigInteger.Divide(point * sN, denominator * sD); // floor(point·Rs), all positive
    }

    // --- Approved reference tables --------------------------------------------------

    public static TheoryData<FrameRate, FrameRate, int[]> Tables => new()
    {
        { FrameRate.Fps25, FrameRate.Ntsc30, new[] { 0, 1, 2, 2, 3, 4, 5, 6, 7, 7, 8, 9 } },
        { FrameRate.Fps24, FrameRate.Ntsc30, new[] { 0, 1, 2, 2, 3, 4, 5, 6, 6, 7, 8, 9 } },
        { FrameRate.Ntsc30, FrameRate.Fps24, new[] { 0, 1, 2, 4, 5, 6, 7, 9, 10, 11, 12, 14 } },
        { FrameRate.Ntsc60, FrameRate.Ntsc30, new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22 } },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void ReferenceTables(FrameRate source, FrameRate project, int[] expected)
    {
        var frames = Cfr(source, 100);
        var actual = Enumerable.Range(0, expected.Length).Select(n => SelectAt(frames, n, project, source)).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void ReferenceTables_MatchExactRationalRule_OverLongRuns(FrameRate source, FrameRate project, int[] _)
    {
        var frames = Cfr(source, 40_000);
        for (long n = 0; n < 20_000; n++)
            Assert.Equal(ExactIndex(n, project, source), SelectAt(frames, n, project, source));
    }

    // --- Same frame rate ----------------------------------------------------------------

    public static TheoryData<FrameRate> SameRates => new()
    {
        FrameRate.Fps24, FrameRate.Fps25, FrameRate.Fps30, FrameRate.Ntsc24, FrameRate.Ntsc30, FrameRate.Ntsc60, FrameRate.Fps60
    };

    [Theory]
    [MemberData(nameof(SameRates))]
    public void SameRate_IsIdentity_ForAMillionFrames(FrameRate rate)
    {
        // Checks "frame n is at or before the point, frame n+1 is not" directly, without
        // materializing a million-entry list. Covers the tick-rounded grid (e.g. 29.97 frame 2
        // starts at 667333 ticks while the source frame starts at 667333⅓).
        var tb = new TimeBase(1, rate.Numerator);
        for (long n = 0; n < 1_000_000; n++)
        {
            var point = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, n, rate, rate);
            Assert.True(SourceFrameSelector.IsAtOrBefore(new SourceTimestamp(n * rate.Denominator, tb), MediaTime.Zero, point));
            Assert.False(SourceFrameSelector.IsAtOrBefore(new SourceTimestamp((n + 1) * rate.Denominator, tb), MediaTime.Zero, point));
        }
    }

    [Theory]
    [MemberData(nameof(SameRates))]
    public void SameRate_WithoutNominalRate_IsIdentity(FrameRate rate)
    {
        var frames = Cfr(rate, 2_000);
        for (var n = 0; n < 1_999; n++)
            Assert.Equal(n, SelectAt(frames, n, rate, source: null));
    }

    [Theory]
    [MemberData(nameof(SameRates))]
    public void SameRate_ClipOffsetOnTimeline_AndInSource_IsIdentity(FrameRate rate)
    {
        var frames = Cfr(rate, 3_000);
        const long startFrame = 1234, inFrame = 777;
        var clipStart = MediaTime.FromFrame(startFrame, rate);
        var sourceIn = MediaTime.FromFrame(inFrame, rate);
        for (var i = 0; i < 2_000; i++)
            Assert.Equal(inFrame + i, SelectAt(frames, startFrame + i, rate, rate, clipStart: clipStart, sourceIn: sourceIn));
    }

    // --- PTS jitter -----------------------------------------------------------------------

    [Theory]
    [InlineData(30000, 1001)]
    [InlineData(24000, 1001)]
    [InlineData(60000, 1001)]
    [InlineData(25, 1)]
    public void SameRate_MillisecondTimestamps_IsIdentity(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var frames = CfrMilliseconds(rate, 20_000);
        for (var n = 0; n < 19_999; n++)
            Assert.Equal(n, SelectAt(frames, n, rate, rate));
    }

    [Fact]
    public void Ntsc60InNtsc30_MillisecondTimestamps_ShowsEveryOtherFrame()
    {
        var frames = CfrMilliseconds(FrameRate.Ntsc60, 20_000);
        for (var n = 0; n < 9_999; n++)
            Assert.Equal(2 * n, SelectAt(frames, n, FrameRate.Ntsc30, FrameRate.Ntsc60));
    }

    [Theory]
    [InlineData(30000, 1001, 30000, 1001, 1)]
    [InlineData(25, 1, 25, 1, 1)]
    [InlineData(60000, 1001, 30000, 1001, 2)]
    [InlineData(60, 1, 30, 1, 2)]
    public void RandomJitter_WithinFortyPercentOfAFrame_DoesNotChangeTheFrame(int sNum, int sDen, int pNum, int pDen, int step)
    {
        var source = new FrameRate(sNum, sDen);
        var project = new FrameRate(pNum, pDen);
        // Fine time base (1/90000·den) so jitter is representable; source frame length = 90000·sDen/sNum units.
        var tb = new TimeBase(1, 90_000L * sNum);
        var frameUnits = 90_000L * sDen; // one source frame, in tb units
        var random = new Random(12345);
        var frames = Enumerable.Range(0, 10_000)
            .Select(i => new SourceTimestamp(i * frameUnits + (long)((random.NextDouble() * 0.8 - 0.4) * frameUnits), tb))
            .ToList();

        for (var n = 0; n < 10_000 / step - 1; n++)
            Assert.Equal(n * step, SelectAt(frames, n, project, source));
    }

    // --- VFR --------------------------------------------------------------------------------

    [Fact]
    public void Vfr_FrameMissing_HoldsThePreviousFrame()
    {
        // 25 fps nominal, MKV ms timestamps; odd frames 50..99 dropped (like the ffmpeg
        // `select` VFR test file). Timeline 25 fps: frame n shows the last present source frame ≤ n.
        var present = Enumerable.Range(0, 250).Where(i => !(i is >= 50 and <= 99 && i % 2 == 1)).ToList();
        var frames = present.Select(i => new SourceTimestamp(i * 40L, new TimeBase(1, 1000))).ToList();

        for (var n = 0; n < 250; n++)
        {
            var index = SelectAt(frames, n, FrameRate.Fps25, FrameRate.Fps25);
            Assert.Equal(present.Last(p => p <= n), present[index]);
        }
    }

    [Fact]
    public void Vfr_IrregularIntervals_UsesRealPtsNotNominalRate()
    {
        // Nominal (avg) 30 fps but frames arrive at 0, 10, 100, 110, 500 ms.
        var ms = new long[] { 0, 10, 100, 110, 500 };
        var frames = ms.Select(p => new SourceTimestamp(p, new TimeBase(1, 1000))).ToList();
        var rate = FrameRate.Fps30; // δ = 1/60 s ≈ 16.7 ms

        Assert.Equal(1, SelectAt(frames, 0, rate, rate));  // point 16.7 ms → frame at 10 ms
        Assert.Equal(1, SelectAt(frames, 2, rate, rate));  // point 83.3 ms
        Assert.Equal(3, SelectAt(frames, 3, rate, rate));  // point 116.7 ms → frame at 110 ms
        Assert.Equal(3, SelectAt(frames, 14, rate, rate)); // point 483.3 ms
        Assert.Equal(4, SelectAt(frames, 15, rate, rate)); // point 516.7 ms
    }

    [Fact]
    public void SamplePoint_ExactlyOnPts_IsIncluded_OneTickLater_IsNot()
    {
        // 25 fps, no nominal rate: frame 0 samples at P/2 = 200000 ticks.
        var point = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, 0, FrameRate.Fps25, null);
        var tb = new TimeBase(1, TimeSpan.TicksPerSecond);
        Assert.True(SourceFrameSelector.IsAtOrBefore(new SourceTimestamp(200_000, tb), MediaTime.Zero, point));
        Assert.False(SourceFrameSelector.IsAtOrBefore(new SourceTimestamp(200_001, tb), MediaTime.Zero, point));
    }

    // --- Clip boundaries --------------------------------------------------------------------

    [Fact]
    public void ClipBoundaries_FirstAndLastFrame_AndOutsideRejected()
    {
        var rate = FrameRate.Ntsc30;
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid() };
        clip.TimelineStart = MediaTime.FromFrame(10, rate);
        clip.Duration = MediaTime.FromFrame(20, rate) - clip.TimelineStart;
        clip.SourceIn = MediaTime.FromFrame(5, rate);
        clip.SourceOut = clip.SourceIn + clip.Duration;
        var metadata = new MediaMetadata { FrameRate = rate, AvgFrameRate = rate };
        var frames = Cfr(rate, 100);

        Assert.Equal(5, SourceFrameSelector.Select(frames, MediaTime.Zero, SourceFrameSelector.SamplePoint(clip, 10, rate, metadata)));
        Assert.Equal(14, SourceFrameSelector.Select(frames, MediaTime.Zero, SourceFrameSelector.SamplePoint(clip, 19, rate, metadata)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceFrameSelector.SamplePoint(clip, 9, rate, metadata));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceFrameSelector.SamplePoint(clip, 20, rate, metadata));

        // At 2× (D022) timeline frame n shows source frame SourceIn + 2·(n − start).
        clip.Speed = ClipSpeed.FromSteps(40);
        Assert.Equal(5, SourceFrameSelector.Select(frames, MediaTime.Zero, SourceFrameSelector.SamplePoint(clip, 10, rate, metadata)));
        Assert.Equal(7, SourceFrameSelector.Select(frames, MediaTime.Zero, SourceFrameSelector.SamplePoint(clip, 11, rate, metadata)));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void SamplePoint_StaysInsideSourceInSourceOut(FrameRate source, FrameRate project, int[] _)
    {
        // For every frame of a clip, SourceIn ≤ point < SourceOut (exact rational compare).
        var clipStart = MediaTime.FromFrame(300, project);
        var sourceIn = MediaTime.FromSeconds(1.2345678);
        const long frames = 500;
        var sourceOut = sourceIn + (MediaTime.FromFrame(300 + frames, project) - clipStart);
        for (var n = 300L; n < 300 + frames; n++)
        {
            var p = SourceFrameSelector.SamplePoint(clipStart, sourceIn, n, project, source);
            Assert.True(p.TicksNumerator >= (Int128)sourceIn.Ticks * p.TicksDenominator);
            Assert.True(p.TicksNumerator < (Int128)sourceOut.Ticks * p.TicksDenominator);
        }
    }

    // --- Hold-first / hold-last ------------------------------------------------------------

    [Fact]
    public void HoldFirst_WhenVideoStartsAfterTheOrigin()
    {
        // Video stream begins two frames after the container start time.
        var frames = Cfr(FrameRate.Fps25, 50, startPts: 2);
        Assert.Equal(0, SelectAt(frames, 0, FrameRate.Fps25, FrameRate.Fps25));
        Assert.Equal(0, SelectAt(frames, 1, FrameRate.Fps25, FrameRate.Fps25));
        Assert.Equal(0, SelectAt(frames, 2, FrameRate.Fps25, FrameRate.Fps25));
        Assert.Equal(1, SelectAt(frames, 3, FrameRate.Fps25, FrameRate.Fps25));
    }

    [Fact]
    public void HoldLast_AfterTheLastFrame()
    {
        var frames = Cfr(FrameRate.Fps25, 10);
        Assert.Equal(9, SelectAt(frames, 9, FrameRate.Fps25, FrameRate.Fps25));
        Assert.Equal(9, SelectAt(frames, 10, FrameRate.Fps25, FrameRate.Fps25));
        Assert.Equal(9, SelectAt(frames, 500, FrameRate.Fps25, FrameRate.Fps25));
    }

    [Fact]
    public void EmptyFrameList_ReturnsMinusOne() =>
        Assert.Equal(-1, SelectAt(new List<SourceTimestamp>(), 0, FrameRate.Fps25, FrameRate.Fps25));

    // --- Origin ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(14_000_000L)]   // MPEG-TS style start_time 1.4 s
    [InlineData(-210_000L)]     // MP4 edit list with negative start_time −0.021 s
    [InlineData(0L)]
    public void StartTime_IsTheOriginOfSourceTime(long startTicks)
    {
        // 25 fps in a 1/90000 time base whose first frame is at the start time.
        var tb = new TimeBase(1, 90_000);
        var startPts = startTicks * 90_000 / TimeSpan.TicksPerSecond; // exact for these values
        Assert.Equal(startTicks, startPts * TimeSpan.TicksPerSecond / 90_000);
        var frames = Enumerable.Range(0, 500).Select(i => new SourceTimestamp(startPts + i * 3600L, tb)).ToList();

        for (var n = 0; n < 499; n++)
            Assert.Equal(n, SelectAt(frames, n, FrameRate.Fps25, FrameRate.Fps25, startTime: new MediaTime(startTicks)));
    }

    [Fact]
    public void Fps25InFps50_ShowsEachSourceFrameTwice()
    {
        var frames = Cfr(FrameRate.Fps25, 1_000);
        for (var n = 0; n < 1_998; n++)
            Assert.Equal(n / 2, SelectAt(frames, n, Fps50, FrameRate.Fps25));
    }

    [Fact]
    public void NominalRate_PrefersAvgFrameRate_FallsBackToRFrameRate()
    {
        Assert.Equal(FrameRate.Fps25, SourceFrameSelector.NominalRate(new MediaMetadata { FrameRate = FrameRate.Fps60, AvgFrameRate = FrameRate.Fps25 }));
        Assert.Equal(FrameRate.Fps60, SourceFrameSelector.NominalRate(new MediaMetadata { FrameRate = FrameRate.Fps60 }));
        Assert.Null(SourceFrameSelector.NominalRate(new MediaMetadata()));
        Assert.Null(SourceFrameSelector.NominalRate(null));
    }
}
