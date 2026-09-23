using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class AudioTimingTests
{
    private const long Rate = AudioFormat.SampleRate;

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]             // 0.0048 sample → next sample
    [InlineData(208L, 1L)]           // 0.9984 sample
    [InlineData(209L, 2L)]           // 1.0032 samples
    [InlineData(10_000_000L, 48_000L)]
    [InlineData(333_667L, 1_602L)]   // 29.97 frame 1: 1601.6 samples
    [InlineData(-1L, 0L)]
    [InlineData(-209L, -1L)]
    public void CeilingSample_IsExact(long ticks, long expected) =>
        Assert.Equal(expected, AudioTiming.CeilingSample(new MediaTime(ticks)));

    [Theory]
    [InlineData(104L, 0L)]   // 0.4992 sample
    [InlineData(105L, 1L)]   // 0.504 sample
    [InlineData(10_000_000L, 48_000L)]
    [InlineData(-105L, -1L)]
    public void NearestSample_RoundsHalfUp(long ticks, long expected) =>
        Assert.Equal(expected, AudioTiming.NearestSample(new MediaTime(ticks)));

    public static TheoryData<int, int> Grids => new() { { 30000, 1001 }, { 24000, 1001 }, { 60000, 1001 }, { 25, 1 }, { 24, 1 }, { 30, 1 } };

    [Theory]
    [MemberData(nameof(Grids))]
    public void AdjacentClips_PartitionSamples_WithoutGapOrOverlap(int num, int den)
    {
        // Clips on the project frame grid, back to back: every timeline sample belongs to exactly
        // one clip, and sample k is in [S, E) exactly when S ≤ k/48000 < E (checked in rationals).
        var rate = new FrameRate(num, den);
        var random = new Random(num);
        long frame = 0;
        var previousEnd = AudioTiming.CeilingSample(MediaTime.Zero);
        for (var clip = 0; clip < 2_000; clip++)
        {
            var start = MediaTime.FromFrame(frame, rate);
            frame += random.Next(1, 90);
            var end = MediaTime.FromFrame(frame, rate);

            var first = AudioTiming.CeilingSample(start);
            var last = AudioTiming.CeilingSample(end); // exclusive
            Assert.Equal(previousEnd, first);
            previousEnd = last;

            Assert.True(InClip(first, start, end));
            Assert.False(InClip(first - 1, start, end));
            Assert.True(last - 1 < first || InClip(last - 1, start, end));
            Assert.False(InClip(last, start, end));
        }
    }

    [Theory]
    [InlineData(123_456_789L, 987_654_321L)]
    [InlineData(0L, 1L)]
    [InlineData(5_000_000L, 104L)]
    [InlineData(333_667L, 0L)]
    public void SourceOffset_IsWithinHalfASampleOfTheExactOffset(long clipStartTicks, long sourceInTicks)
    {
        // Timeline sample k plays source sample k + d for the whole clip, so the only error is
        // the one rounding of d: |d − (SourceIn − S)·48000/10⁷| ≤ ½ sample, the same for every k.
        var d = AudioTiming.SourceOffset(new MediaTime(clipStartTicks), new MediaTime(sourceInTicks));
        var exact = new BigInteger(sourceInTicks - clipStartTicks) * Rate;          // in samples · 10⁷
        var error = BigInteger.Abs(new BigInteger(d) * TimeSpan.TicksPerSecond - exact);
        Assert.True(2 * error <= TimeSpan.TicksPerSecond, $"offset {d} is more than ½ sample off");
    }

    [Fact]
    public void LongTimelines_DoNotOverflow()
    {
        var day = MediaTime.FromSeconds(86_400);
        Assert.Equal(86_400L * Rate, AudioTiming.CeilingSample(day));
        Assert.Equal(86_400L * Rate, AudioTiming.FloorSample(day));
        Assert.Equal(-86_400L * Rate, AudioTiming.SourceOffset(day, MediaTime.Zero));
    }

    private static bool InClip(long sample, MediaTime start, MediaTime end)
    {
        // S ≤ k/48000 < E  ⇔  S·48000 ≤ k·10⁷ < E·48000
        var k = new BigInteger(sample) * TimeSpan.TicksPerSecond;
        return new BigInteger(start.Ticks) * Rate <= k && k < new BigInteger(end.Ticks) * Rate;
    }
}
