using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.5d: the display rule of timeline waveforms — the height linear in the volume (200 % reaches the full
/// height, PO-W2), a pixel column showing the largest peak of exactly the source range its timeline samples play
/// (<c>AudioPlacement</c>: trim, position and speed ≠ 1×), nothing outside the clip or after the sound, and only the
/// columns inside the viewport computed.
/// </summary>
public sealed class WaveformLayoutTests
{
    // 480 samples (10 ms) per peak; at 100 px/s one column is 480 timeline samples — one peak at 1×.
    private const int PerPeak = 480;
    private const double Pps = 100;

    /// <summary>Peak i = i + 1 (1…200): which peak a column shows is its value minus one.</summary>
    private static readonly Waveform Ramp = new(PerPeak, 200L * PerPeak, Enumerable.Range(1, 200).Select(i => (byte)i).ToArray());

    private static ClipWaveform Clip(double startSeconds = 0, double seconds = 1, double sourceInSeconds = 0,
        ClipSpeed? speed = null, double volume = 2, double pps = Pps, Waveform? data = null) =>
        new(data ?? Ramp, MediaTime.FromSeconds(startSeconds), MediaTime.FromSeconds(startSeconds + seconds),
            MediaTime.FromSeconds(sourceInSeconds), speed ?? ClipSpeed.Normal, volume, IsMuted: false, LowerHalf: false, pps);

    /// <summary>The peak a column shows, back from its fraction at 200 % (fraction = peak / 255).</summary>
    private static int PeakOf(ClipWaveform clip, int column) => (int)Math.Round(WaveformLayout.Column(clip, column) * 255);

    [Theory]
    [InlineData(255, 2.0, 1.0)]    // full scale at 200 %: the full height
    [InlineData(255, 1.0, 0.5)]    // at 100 %: half of it
    [InlineData(128, 1.0, 128 / 510.0)]
    [InlineData(255, 0.0, 0.0)]    // silent clip
    [InlineData(0, 2.0, 0.0)]      // silence
    public void The_height_is_linear_in_the_volume_and_reaches_the_full_height_at_200_percent(byte peak, double volume, double fraction) =>
        Assert.Equal(fraction, WaveformLayout.Fraction(peak, volume), 12);

    [Fact]
    public void At_1x_each_column_shows_its_own_source_range()
    {
        var clip = Clip();
        Assert.Equal(new[] { 1, 2, 3, 100 }, new[] { 0, 1, 2, 99 }.Select(c => PeakOf(clip, c)));
    }

    [Fact]
    public void A_trimmed_clip_shows_its_source_range_and_its_timeline_position_doesn_t_matter()
    {
        var trimmed = Clip(startSeconds: 7, sourceInSeconds: 1);                     // source 1 s → peak 100 onwards
        Assert.Equal(new[] { 101, 102, 150 }, new[] { 0, 1, 49 }.Select(c => PeakOf(trimmed, c)));
    }

    [Fact]
    public void At_2x_a_column_covers_twice_the_source_and_at_half_speed_half_of_it()
    {
        var fast = Clip(seconds: 0.5, speed: ClipSpeed.FromSteps(40));
        Assert.Equal(new[] { 2, 4, 100 }, new[] { 0, 1, 49 }.Select(c => PeakOf(fast, c)));       // max of peaks 2c, 2c+1

        var slow = Clip(seconds: 2, speed: ClipSpeed.FromSteps(10));
        Assert.Equal(new[] { 1, 1, 2, 2, 100 }, new[] { 0, 1, 2, 3, 199 }.Select(c => PeakOf(slow, c))); // peak ⌊c/2⌋
    }

    [Fact]
    public void Zoomed_out_a_column_shows_the_loudest_peak_it_covers()
    {
        var clip = Clip(seconds: 2, pps: 1);                                        // one column = 1 s = 100 peaks
        Assert.Equal(new[] { 100, 200 }, new[] { 0, 1 }.Select(c => PeakOf(clip, c)));
        Assert.Equal(2, WaveformLayout.ColumnCount(clip));
    }

    [Fact]
    public void Nothing_is_shown_outside_the_clip_or_after_the_sound()
    {
        var clip = Clip(seconds: 3);                                                // the sound ends at 2 s
        Assert.Equal(0, WaveformLayout.Column(clip, -1));
        Assert.Equal(200, PeakOf(clip, 199));
        Assert.Equal(0, WaveformLayout.Column(clip, 200));                          // after the sound: silence
        Assert.Equal(0, WaveformLayout.Column(clip, 300));                          // after the clip
        Assert.Equal(300, WaveformLayout.ColumnCount(clip));
    }

    [Fact]
    public void The_volume_scales_every_column()
    {
        var full = Clip(data: new Waveform(PerPeak, PerPeak, new byte[] { 255 }), seconds: 0.01);
        Assert.Equal(1.0, WaveformLayout.Column(full, 0), 12);
        Assert.Equal(0.25, WaveformLayout.Column(full with { Volume = 0.5 }, 0), 12);
    }

    [Theory]
    [InlineData(0, 800, 5000, 0, 800)]       // the clip starts at the viewport's left edge
    [InlineData(-1000.5, 800, 5000, 1000, 1801)] // scrolled into the clip
    [InlineData(300, 800, 5000, 0, 500)]     // starts inside the viewport
    [InlineData(-4900, 800, 5000, 4900, 5000)] // its end is visible
    [InlineData(900, 800, 5000, 0, 0)]       // right of the viewport: nothing
    [InlineData(-6000, 800, 5000, 6000, 6000)] // left of it: nothing
    public void Only_the_columns_inside_the_viewport_are_drawn(double originX, double viewport, double width, int from, int to) =>
        Assert.Equal((from, to), WaveformView.Visible(originX, viewport, width));
}
