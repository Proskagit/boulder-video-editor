using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D024 Step 9.5a: the <see cref="Waveform"/> data — one peak per started group of samples, the linear peak scale
/// (rounded up, so any sound is visible) and the largest peak over a source range, which the timeline display reads.
/// </summary>
public sealed class WaveformTests
{
    [Theory]
    [InlineData(0, 256, 0)]
    [InlineData(1, 256, 1)]
    [InlineData(256, 256, 1)]
    [InlineData(257, 256, 2)]
    [InlineData(48_000, 256, 188)]
    public void There_is_one_peak_per_started_group(long samples, int perPeak, long peaks) =>
        Assert.Equal(peaks, Waveform.PeakCountFor(samples, perPeak));

    [Fact]
    public void The_peaks_must_match_the_sample_count()
    {
        Assert.Throws<ArgumentException>(() => new Waveform(256, 257, new byte[1]));
        Assert.Throws<ArgumentException>(() => new Waveform(256, 256, new byte[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Waveform(0, 0, Array.Empty<byte>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Waveform(256, -1, Array.Empty<byte>()));
        Assert.Equal(0, new Waveform(256, 0, Array.Empty<byte>()).SampleCount);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1e-6f, 1)]      // rounded up: any sound shows
    [InlineData(0.5f, 128)]
    [InlineData(-0.5f, 128)]
    [InlineData(0.999f, 255)]
    [InlineData(1f, 255)]
    [InlineData(-3f, 255)]      // louder than full scale: capped
    [InlineData(float.NaN, 0)]
    public void A_peak_is_the_amplitude_on_a_linear_scale_rounded_up(float amplitude, byte peak) =>
        Assert.Equal(peak, Waveform.ToPeak(amplitude));

    [Fact]
    public void The_largest_peak_of_a_range_takes_every_group_it_touches()
    {
        // 4 groups of 10 samples, the last one partial (35 samples).
        var waveform = new Waveform(10, 35, new byte[] { 5, 50, 20, 7 });

        Assert.Equal(5, waveform.MaxPeak(0, 10));
        Assert.Equal(50, waveform.MaxPeak(9, 11));        // touches groups 0 and 1
        Assert.Equal(20, waveform.MaxPeak(20, 35));
        Assert.Equal(20, waveform.MaxPeak(25, 26));
        Assert.Equal(7, waveform.MaxPeak(30, 1000));      // after the end: nothing more
        Assert.Equal(50, waveform.MaxPeak(-100, 1000));
        Assert.Equal(5, waveform.MaxPeak(-100, 1));       // before the start: nothing
        Assert.Equal(0, waveform.MaxPeak(-100, 0));
        Assert.Equal(0, waveform.MaxPeak(35, 1000));
        Assert.Equal(0, waveform.MaxPeak(12, 12));        // empty
        Assert.Equal(0, waveform.MaxPeak(20, 10));
    }
}
