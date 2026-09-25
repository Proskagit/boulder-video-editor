using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 8 Step 4 (D023): the shared audio placement (playback and export) — which timeline samples a clip owns,
/// which source position the decoder is asked for, where a decoded stream lands — for 1×, 0.25×, 4× (and an odd
/// speed), trim, split, resuming inside a clip (pause/seek), SourceIn/SourceOut and span edges. At 1× everything is
/// exact; at other speeds the only error is the documented per-stream rounding (D022).
/// </summary>
public class AudioPlacementTests
{
    private const long Tps = TimeSpan.TicksPerSecond;
    private static readonly FrameRate Rate = FrameRate.Ntsc30;
    private static readonly int[] SpeedSteps = { 5, 20, 27, 80 };   // 0.25×, 1×, 1.35×, 4×

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>A clip of <paramref name="frames"/> frames from <paramref name="startFrame"/> whose source starts
    /// at <paramref name="sourceIn"/>; SourceOut per the D022 timing rule.</summary>
    private static (AudioPlacement Placement, MediaTime Start, MediaTime SourceIn, MediaTime SourceOut) Clip(
        long startFrame, long frames, MediaTime sourceIn, ClipSpeed speed)
    {
        var start = F(startFrame);
        var end = F(startFrame + frames);
        var sourceOut = speed.IsNormal ? sourceIn + (end - start) : sourceIn + SpeedTiming.SourceLength(frames, speed, Rate);
        return (AudioPlacement.Of(start, end, sourceIn, speed), start, sourceIn, sourceOut);
    }

    /// <summary>Exact source sample (fractional) played at timeline sample k.</summary>
    private static double ExactSource(long k, MediaTime start, MediaTime sourceIn, ClipSpeed speed) =>
        sourceIn.Ticks * 48_000.0 / Tps + (k - start.Ticks * 48_000.0 / Tps) * (double)speed.ToDecimal();

    /// <summary>Where a decoder asked for timeline sample k lands when it starts exactly at the requested source
    /// sample (its first sample = NearestSample of the request).</summary>
    private static long Placed(AudioPlacement p, long k) => p.TimelineSampleOfStream(AudioTiming.NearestSample(p.SourcePositionAt(k)));

    // --- bounds ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_clip_owns_the_samples_from_the_ceiling_of_its_start_to_the_ceiling_of_its_end()
    {
        foreach (var steps in SpeedSteps)
        {
            var (p, start, _, _) = Clip(7, 100, new MediaTime(3 * Tps), ClipSpeed.FromSteps(steps));
            Assert.Equal(AudioTiming.CeilingSample(start), p.FirstSample);
            Assert.Equal(AudioTiming.CeilingSample(F(107)), p.EndSample);
            Assert.Equal((p.FirstSample, p.EndSample, p.FirstSample + 5), (p.Clamp(0), p.Clamp(long.MaxValue), p.Clamp(p.FirstSample + 5)));
        }
    }

    [Fact]
    public void Consecutive_clips_partition_the_samples_without_gap_or_overlap()
    {
        var speed = ClipSpeed.FromSteps(27);
        var clips = new[] { Clip(0, 13, MediaTime.Zero, speed), Clip(13, 1, MediaTime.Zero, speed), Clip(14, 29, MediaTime.Zero, ClipSpeed.Normal) };
        for (var i = 1; i < clips.Length; i++)
            Assert.Equal(clips[i - 1].Placement.EndSample, clips[i].Placement.FirstSample);
    }

    [Fact]
    public void The_placement_of_a_span_is_the_placement_of_its_timing()
    {
        var span = new AudioSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, F(10), F(90), new MediaTime(12_345_678), 0.5)
            { Speed = ClipSpeed.FromSteps(5) };

        Assert.Equal(AudioPlacement.Of(F(10), F(90), new MediaTime(12_345_678), ClipSpeed.FromSteps(5)), AudioPlacement.Of(span));
    }

    // --- 1×: exact ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 12_345_677)]
    [InlineData(301, 999)]
    [InlineData(1001, 50_000_003)]
    public void At_1x_sample_k_plays_source_sample_k_plus_d_exactly(long startFrame, long sourceInTicks)
    {
        var (p, start, sourceIn, _) = Clip(startFrame, 3000, new MediaTime(sourceInTicks), ClipSpeed.Normal);
        var d = AudioTiming.SourceOffset(start, sourceIn);

        foreach (var k in new[] { p.FirstSample, p.FirstSample + 1, p.FirstSample + 12_345, p.EndSample - 1 })
        {
            Assert.Equal(new MediaTime(AudioTiming.SampleToTicksFloor(k + d)), p.SourcePositionAt(k));
            Assert.Equal(k + d, AudioTiming.NearestSample(p.SourcePositionAt(k)));
            Assert.Equal(k, Placed(p, k));                                              // requested = placed, exactly
            Assert.Equal(k - 100, p.TimelineSampleOfStream(k + d - 100));               // a preroll lands before k
        }
    }

    // --- other speeds: the D022 mapping ---------------------------------------------------------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(27)]
    [InlineData(80)]
    public void At_other_speeds_the_request_is_the_exact_source_time_and_the_stream_lands_at_k(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var (p, start, sourceIn, _) = Clip(30, 600, new MediaTime(21_000_001), speed);
        var s = (double)speed.ToDecimal();

        foreach (var k in new[] { p.FirstSample, p.FirstSample + 7, p.FirstSample + 100_003, p.EndSample - 1 })
        {
            Assert.Equal(AudioTiming.SourceTimeAt(k, start, sourceIn, speed), p.SourcePositionAt(k));
            Assert.InRange(p.SourcePositionAt(k).Ticks * 48_000.0 / Tps - ExactSource(k, start, sourceIn, speed), -48_000.0 / Tps - 1e-6, 1e-6);   // floored to a tick
            // Two roundings of ≤ ½ source sample each, scaled to timeline samples by 1/s.
            Assert.InRange(Placed(p, k) - k, -Math.Ceiling(0.5 / s + 0.5), Math.Ceiling(0.5 / s + 0.5));
        }
    }

    [Fact]
    public void Worked_examples_at_quarter_and_four_times_speed()
    {
        // 0.25×: clip at 1 s with SourceIn 3 s: timeline 3 s (sample 144 000) plays source 3.5 s (sample 168 000).
        var quarter = AudioPlacement.Of(new MediaTime(Tps), new MediaTime(9 * Tps), new MediaTime(3 * Tps), ClipSpeed.FromSteps(5));
        Assert.Equal(new MediaTime(35_000_000), quarter.SourcePositionAt(144_000));
        Assert.Equal(144_000, quarter.TimelineSampleOfStream(168_000));

        // 4×: same clip: timeline 3 s plays source 11 s (sample 528 000).
        var four = AudioPlacement.Of(new MediaTime(Tps), new MediaTime(9 * Tps), new MediaTime(3 * Tps), ClipSpeed.FromSteps(80));
        Assert.Equal(new MediaTime(11 * Tps), four.SourcePositionAt(144_000));
        Assert.Equal(144_000, four.TimelineSampleOfStream(528_000));
    }

    // --- resuming inside a clip (pause, seek) ----------------------------------------------------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(80)]
    public void Resuming_anywhere_inside_a_clip_plays_what_continuous_playback_plays(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var s = (double)speed.ToDecimal();
        var (p, _, _, _) = Clip(15, 900, new MediaTime(4 * Tps + 777), speed);

        // A stream opened at the clip start and one opened at k (resume after a pause) — each starting exactly at
        // its requested source sample and advancing by s per timeline sample — agree on every later sample.
        long first = AudioTiming.NearestSample(p.SourcePositionAt(p.FirstSample)), firstAt = p.TimelineSampleOfStream(first);
        foreach (var k in new[] { p.FirstSample + 1, p.FirstSample + 48_000, p.FirstSample + 250_001, p.EndSample - 2 })
        {
            long resumed = AudioTiming.NearestSample(p.SourcePositionAt(k)), resumedAt = p.TimelineSampleOfStream(resumed);
            var m = k + 1;
            var continuous = first + (m - firstAt) * s;
            var afterPause = resumed + (m - resumedAt) * s;
            if (speed.IsNormal) Assert.Equal(continuous, afterPause);
            else Assert.InRange(afterPause - continuous, -(s + 1), s + 1);             // each stream within ½ timeline sample
        }
    }

    // --- trim and split (the D022 timing rule) -----------------------------------------------------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(80)]
    public void Split_halves_continue_each_other_at_the_cut(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var sourceIn = new MediaTime(2 * Tps + 13);
        var whole = Clip(100, 400, sourceIn, speed);
        // split at frame 100 + 157: the right half starts at SourceIn + SourceLength(157) (1×: + the duration)
        var cut = speed.IsNormal ? sourceIn + (F(257) - F(100)) : sourceIn + SpeedTiming.SourceLength(157, speed, Rate);
        var left = Clip(100, 157, sourceIn, speed);
        var right = Clip(257, 243, cut, speed);

        Assert.Equal(left.Placement.EndSample, right.Placement.FirstSample);
        Assert.Equal(whole.Placement.EndSample, right.Placement.EndSample);
        foreach (var k in new[] { right.Placement.FirstSample, right.Placement.FirstSample + 10_000, right.Placement.EndSample - 1 })
        {
            var inWhole = AudioTiming.NearestSample(whole.Placement.SourcePositionAt(k));
            var inRight = AudioTiming.NearestSample(right.Placement.SourcePositionAt(k));
            if (speed.IsNormal) Assert.Equal(inWhole, inRight);
            else Assert.InRange(inRight - inWhole, -1, 1);                               // the floored cut: < 1 tick + rounding
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(80)]
    public void Trimming_the_start_keeps_the_source_of_every_remaining_sample(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var sourceIn = new MediaTime(Tps);
        var original = Clip(50, 300, sourceIn, speed);
        var moved = speed.IsNormal ? F(80) - F(50) : SpeedTiming.SourceLength(30, speed, Rate);
        var trimmed = Clip(80, 270, sourceIn + moved, speed);

        foreach (var k in new[] { trimmed.Placement.FirstSample, trimmed.Placement.FirstSample + 4_321, trimmed.Placement.EndSample - 1 })
        {
            var before = AudioTiming.NearestSample(original.Placement.SourcePositionAt(k));
            var after = AudioTiming.NearestSample(trimmed.Placement.SourcePositionAt(k));
            if (speed.IsNormal) Assert.Equal(before, after);
            else Assert.InRange(after - before, -1, 1);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(27)]
    [InlineData(80)]
    public void The_clip_starts_at_SourceIn_and_never_plays_past_SourceOut(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var (p, start, sourceIn, sourceOut) = Clip(33, 211, new MediaTime(7 * Tps + 5), speed);

        // The first owned sample (⌈S⌉) plays SourceIn plus less than one timeline sample × s — minus, at 1×, up to the
        // half sample of the per-clip offset rounding (D013).
        var halfSample = Tps / 48_000.0 / 2;
        Assert.InRange(p.SourcePositionAt(p.FirstSample).Ticks - sourceIn.Ticks, speed.IsNormal ? -halfSample - 1 : -1, Tps / 48_000.0 * (double)speed.ToDecimal() + 1);
        Assert.True(p.SourcePositionAt(p.EndSample - 1) < sourceOut, $"{p.SourcePositionAt(p.EndSample - 1)} ≥ {sourceOut}");
    }

    [Fact]
    public void The_decode_request_carries_the_file_its_start_time_the_position_and_the_speed()
    {
        var asset = new PlaybackAsset(Guid.NewGuid(), @"C:\m\a.mp4", MediaKind.Video, new MediaTime(14_000_000), Rate);
        var (p, _, _, _) = Clip(10, 100, new MediaTime(Tps), ClipSpeed.FromSteps(80));

        var request = p.Request(asset, p.FirstSample + 10);

        Assert.Equal((@"C:\m\a.mp4", new MediaTime(14_000_000), p.SourcePositionAt(p.FirstSample + 10), ClipSpeed.FromSteps(80)),
            (request.FilePath, request.StartTime, request.SourcePosition, request.Speed));
    }
}

/// <summary>Phase 8 Step 4 (D023): the mix shared by playback and export — Σ sample × gain, then clamp to [−1, 1].</summary>
public class AudioMixTests
{
    [Fact]
    public void Samples_are_added_times_their_gain()
    {
        var mix = new float[] { 0.1f, -0.1f, 0, 0 };
        AudioMix.Add(new[] { 0.2f, 0.2f, -0.4f, 0.25f }, mix, 0.5f);

        Assert.Equal(new[] { 0.1f + 0.2f * 0.5f, -0.1f + 0.2f * 0.5f, -0.2f, 0.125f }, mix);
    }

    [Fact]
    public void Several_sources_sum_gain_0_is_silence_and_gain_2_doubles()
    {
        var mix = new float[4];
        AudioMix.Add(new[] { 0.1f, 0.1f, 0.1f, 0.1f }, mix, 1);
        AudioMix.Add(new[] { 0.2f, 0.2f, 0.2f, 0.2f }, mix, 2);
        AudioMix.Add(new[] { 0.9f, 0.9f, 0.9f, 0.9f }, mix, 0);
        AudioMix.Add(new[] { 0.05f, -0.05f }, mix.AsSpan(2), 1);                  // a shorter source, placed later

        Assert.Equal(new[] { 0.1f + 0.4f, 0.1f + 0.4f, 0.1f + 0.4f + 0.05f, 0.1f + 0.4f - 0.05f }, mix);
    }

    [Fact]
    public void Clamping_happens_after_the_sum_and_only_at_plus_and_minus_one()
    {
        var mix = new float[6];
        AudioMix.Add(new[] { 0.8f, -0.8f, 0.3f, 1f, -1f, 0.999f }, mix, 1);
        AudioMix.Add(new[] { 0.8f, -0.8f, -0.9f, 0f, 0f, 0f }, mix, 1);

        AudioMix.Clamp(mix);

        Assert.Equal(new[] { 1f, -1f, 0.3f - 0.9f, 1f, -1f, 0.999f }, mix);   // no limiter, no normalization, no headroom
    }

    [Fact]
    public void Silence_stays_silence()
    {
        var mix = new float[8];
        AudioMix.Clamp(mix);
        Assert.All(mix, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void The_gain_of_a_span_is_its_effective_gain()
    {
        AudioSpan Span(double volume, bool muted) =>
            new(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero, volume, IsMuted: muted);

        Assert.Equal(0.5f, AudioMix.Gain(Span(0.5, false)));
        Assert.Equal(2f, AudioMix.Gain(Span(2, false)));
        Assert.Equal(0f, AudioMix.Gain(Span(1.5, true)));
        Assert.Equal(0f, AudioMix.Gain(Span(0, false)));
        Assert.Equal((float)(1 / 3.0), AudioMix.Gain(Span(1 / 3.0, false)));
    }

    [Fact]
    public void More_samples_than_positions_is_rejected() =>
        Assert.Throws<ArgumentException>(() => AudioMix.Add(new float[4], new float[2], 1));
}
