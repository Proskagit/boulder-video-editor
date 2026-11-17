using System.Collections.Immutable;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests.Audio;

/// <summary>D064 §3–§4: the volume points' interpolation, the ducking curve, the speech intervals, the soft clip and the span's
/// gain curve — the one rule the Preview's mixer and the export share.</summary>
public class AudioMixRulesTests
{
    private static VolumePoint P(double seconds, double gain) => new(Guid.NewGuid(), MediaTime.FromSeconds(seconds), gain);

    private static VolumeEnvelope Env(params VolumePoint[] points) => VolumeEnvelope.Of(points)!;

    [Fact]
    public void Before_the_first_and_after_the_last_point_their_gains_hold()
    {
        var env = Env(P(1, 0.5), P(2, 1.5));
        Assert.Equal(0.5, env.At(MediaTime.Zero));
        Assert.Equal(0.5, env.At(MediaTime.FromSeconds(1)));
        Assert.Equal(1.5, env.At(MediaTime.FromSeconds(2)));
        Assert.Equal(1.5, env.At(MediaTime.FromSeconds(9)));
    }

    [Fact]
    public void Between_two_points_the_gain_moves_evenly_in_dB()
    {
        var env = Env(P(0, 0.25), P(2, 1));                          // −12 dB → 0 dB
        Assert.Equal(0.5, env.At(MediaTime.FromSeconds(1)), 12);     // half way: −6 dB
        var quarter = 20 * Math.Log10(env.At(MediaTime.FromSeconds(0.5)));
        Assert.Equal(0.75 * 20 * Math.Log10(0.25), quarter, 9);        // a quarter of the way: ¾ of the dB below
    }

    [Fact]
    public void A_segment_with_a_silent_end_moves_linearly_in_amplitude()
    {
        var env = Env(P(0, 0), P(1, 1));
        Assert.Equal(0.25, env.At(MediaTime.FromSeconds(0.25)), 12);
        Assert.Equal(0, env.At(MediaTime.Zero));
    }

    [Fact]
    public void The_points_are_validated_and_equal_by_value()
    {
        var id = Guid.NewGuid();
        var a = new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(id, MediaTime.FromSeconds(1), 1)));
        var b = new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(id, MediaTime.FromSeconds(1), 1)));
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(VolumeEnvelope.AreValid(ImmutableArray.Create(P(1, 1), P(1, 0.5))));     // two at one moment
        Assert.False(VolumeEnvelope.AreValid(ImmutableArray.Create(P(2, 1), P(1, 0.5))));     // not sorted
        Assert.False(VolumeEnvelope.AreValid(ImmutableArray.Create(P(1, 2.5))));              // above 200 %
        Assert.False(VolumeEnvelope.AreValid(ImmutableArray.Create(P(1, double.NaN))));
        Assert.False(VolumeEnvelope.AreValid(ImmutableArray<VolumePoint>.Empty));
        Assert.Null(a.Without(id));
        Assert.Null(a.With(new VolumePoint(Guid.NewGuid(), MediaTime.FromSeconds(1), 0.3)));  // the time is taken
    }

    [Fact]
    public void The_soft_clip_passes_minus_one_dBFS_unchanged_and_never_reaches_full_scale()
    {
        Assert.Equal(0.5f, SoftClip.Apply(0.5f));
        Assert.Equal(SoftClip.Knee, SoftClip.Apply(SoftClip.Knee));
        Assert.Equal(-0.8f, SoftClip.Apply(-0.8f));
        foreach (var x in new[] { 0.9f, 1f, 1.5f, 4f, 100f })
        {
            Assert.InRange(SoftClip.Apply(x), SoftClip.Knee, 1f);
            Assert.Equal(-SoftClip.Apply(x), SoftClip.Apply(-x));
        }
        Assert.True(SoftClip.Apply(1.5f) > SoftClip.Apply(1f));              // monotonic
        Assert.Equal(0f, SoftClip.Apply(float.NaN));
        // Continuous at the knee: a step just above it changes the output by about the step.
        Assert.Equal(SoftClip.Knee + 0.001f, SoftClip.Apply(SoftClip.Knee + 0.001f), 4);
    }

    [Fact]
    public void The_ducking_curve_is_down_inside_the_speech_and_ramps_in_dB_around_it()
    {
        // Speech at [48 000, 96 000), attack 4 800, release 9 600, −20 dB.
        var curve = new DuckingCurve(new[] { (48_000L, 96_000L) }, -20, 4_800, 9_600);
        Assert.Equal(1, curve.Gain(0));
        Assert.Equal(1, curve.Gain(48_000 - 4_800));
        Assert.Equal(0.1, curve.Gain(48_000), 12);
        Assert.Equal(0.1, curve.Gain(95_999), 12);
        Assert.Equal(Math.Pow(10, -10 / 20.0), curve.Gain(48_000 - 2_400), 12);          // half the attack: −10 dB
        Assert.Equal(1, curve.Gain(96_000 + 9_600 - 1 + 1));
        Assert.True(curve.Gain(96_000 + 4_800) is > 0.1 and < 1);
        Assert.False(curve.Affects(0, 48_000 - 4_800));
        Assert.True(curve.Affects(0, 48_000 - 4_799));
        Assert.True(curve.Affects(96_000 + 9_000, 200_000));
        Assert.False(curve.Affects(96_000 + 9_600, 200_000));
    }

    [Fact]
    public void Overlapping_ramps_take_the_lowest_gain_and_overlapping_speech_merges()
    {
        var curve = new DuckingCurve(new[] { (0L, 1_000L), (500L, 2_000L), (3_000L, 4_000L) }, -12, 2_000, 2_000);
        Assert.Equal(2, curve.Speech.Count());
        // Between 2 000 and 3 000 the release of the first and the attack of the second meet: the lower of the two.
        var at = 2_600L;
        var release = -12 * (1 - (at - 2_000 + 1) / 2_000.0);
        var attack = -12 * (1 - (3_000 - at) / 2_000.0);
        Assert.Equal(Math.Pow(10, Math.Min(release, attack) / 20), curve.Gain(at), 12);
    }

    [Fact]
    public void Speech_windows_become_intervals_with_short_gaps_closed_and_blips_dropped()
    {
        var p = new float[40];
        for (var i = 2; i < 12; i++) p[i] = 0.9f;      // 10 windows = 320 ms
        p[13] = 0.9f;                                   // a 32 ms gap at 12: closed
        p[30] = 0.9f;                                   // one window alone (32 ms): dropped
        var intervals = new SpeechActivity(p).Intervals(0.5);
        var only = Assert.Single(intervals);
        Assert.Equal(2L * SpeechActivity.WindowSamples, only.Start);
        Assert.Equal(14L * SpeechActivity.WindowSamples, only.End);
        Assert.Empty(new SpeechActivity(p).Intervals(0.95));
    }

    [Fact]
    public void The_threshold_follows_the_sensitivity()
    {
        var id = Guid.NewGuid();
        Assert.Equal(0.85, DuckingSettings.Default(id) with { Sensitivity = 0 } is var a ? a.SpeechThreshold : 0, 12);
        Assert.Equal(0.25, (DuckingSettings.Default(id) with { Sensitivity = 100 }).SpeechThreshold, 12);
        Assert.True(DuckingSettings.Default(id).IsValid);
        Assert.False((DuckingSettings.Default(id) with { DepthDb = -1 }).IsValid);
        Assert.False((DuckingSettings.Default(Guid.Empty)).IsValid);
    }

    [Fact]
    public void A_gain_curve_without_points_fades_or_ducking_is_the_constant_gain()
    {
        var span = new AudioSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero, 0.5)
        {
            TrackGain = 0.8
        };
        var curve = AudioGainCurve.Of(span, FrameRate.Fps25);
        Assert.Equal((float)(0.5 * 0.8), curve.Gain);
        Assert.True(curve.IsConstant(0, 48_000));
        Assert.Equal(curve.Gain, curve.At(1234));
        Assert.True(AudioGainCurve.Of(span with { IsMuted = true }, FrameRate.Fps25).IsSilent);
    }

    [Fact]
    public void A_gain_curve_multiplies_the_points_at_the_source_time_and_the_ducking()
    {
        // The clip at 1 s plays its source from 3 s; the points are in source time.
        var span = new AudioSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, MediaTime.FromSeconds(1), MediaTime.FromSeconds(3),
            MediaTime.FromSeconds(3), 1)
        {
            Volumes = Env(P(3, 1), P(4, 0.5)),
            Ducking = new DuckingCurve(new[] { (96_000L, 144_000L) }, -6, 0, 4_800)
        };
        var curve = AudioGainCurve.Of(span, FrameRate.Fps25);
        Assert.False(curve.IsConstant(48_000, 48_100));
        Assert.Equal(1f, curve.At(48_000));                                   // source 3 s
        Assert.Equal(0.5f, curve.At(96_000 - 1 + 1) / (float)Math.Pow(10, -6 / 20.0), 4);   // source 4 s, ducked
        Assert.Equal(0.5f, curve.At(143_999) / (float)Math.Pow(10, -6 / 20.0), 4);
    }

    [Fact]
    public void Mixing_by_any_block_size_gives_the_same_samples()
    {
        var span = new AudioSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero, 0.9)
        {
            Volumes = Env(P(0.1, 1), P(0.4, 0.2), P(0.8, 1.6)), SoundFadeInFrames = 3, FadeOutFrames = 2,
            Ducking = new DuckingCurve(new[] { (10_000L, 20_000L) }, -9, 1_000, 3_000)
        };
        var curve = AudioGainCurve.Of(span, FrameRate.Fps25);
        var source = Enumerable.Range(0, 48_000 * 2).Select(i => (float)Math.Sin(i * 0.01)).ToArray();

        float[] Mix(int block)
        {
            var mix = new float[source.Length];
            for (var from = 0; from < 48_000; from += block)
            {
                var frames = Math.Min(block, 48_000 - from);
                AudioMix.Add(source.AsSpan(from * 2, frames * 2), mix.AsSpan(from * 2, frames * 2), curve, from);
            }
            AudioMix.Finish(mix);
            return mix;
        }

        Assert.Equal(Mix(48_000), Mix(441));
        Assert.Equal(Mix(48_000), Mix(4_096));
    }
}
