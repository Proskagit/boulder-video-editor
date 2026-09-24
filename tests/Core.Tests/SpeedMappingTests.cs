using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 7 Step 9 (D022): the exact playback mappings at other speeds — the video sample point
/// (D009 with t(n) = SourceIn + (FromFrame(n) − S)·s, δ = ½·min(P·s, Ssrc)), the audio timeline ↔
/// source sample mapping — and that a speed change is never a presentation-only snapshot change.
/// </summary>
public class SpeedMappingTests
{
    private const long Tps = TimeSpan.TicksPerSecond;
    private static readonly FrameRate[] Rates = { FrameRate.Fps24, FrameRate.Fps25, FrameRate.Ntsc30, FrameRate.Ntsc24, FrameRate.Fps60, FrameRate.Ntsc60 };
    private static readonly int[] Steps = { 5, 7, 10, 19, 20, 21, 27, 40, 79, 80 };

    /// <summary>Reference sample point as a BigInteger fraction (numerator, denominator).</summary>
    private static (BigInteger N, BigInteger D) Reference(MediaTime start, MediaTime sourceIn, ClipSpeed s, long n, FrameRate project, FrameRate? source)
    {
        BigInteger a = s.Numerator, b = s.Denominator;
        BigInteger fs = MediaTime.FromFrame(n, project).Ticks, fe = MediaTime.FromFrame(n + 1, project).Ticks;
        var p = fe - fs;
        // t = SourceIn + (fs − S)·a/b ; half-width ½·min(P·a/b, Tps·den/num)
        if (source is not { } r) return (2 * (b * sourceIn.Ticks + a * (fs - start.Ticks)) + a * p, 2 * b);
        var tN = (b * sourceIn.Ticks + a * (fs - start.Ticks)) * 2 * r.Numerator;
        var half = BigInteger.Min(p * a * r.Numerator, (BigInteger)Tps * r.Denominator * b);
        return (tN + half, 2 * b * r.Numerator);
    }

    [Fact]
    public void Video_sample_points_are_exact_for_every_speed_rate_and_source_rate()
    {
        foreach (var project in Rates)
            foreach (var source in Rates.Cast<FrameRate?>().Append(null))
                foreach (var k in Steps)
                {
                    var s = ClipSpeed.FromSteps(k);
                    var start = MediaTime.FromFrame(37, project);
                    var sourceIn = new MediaTime(12_345_679);
                    foreach (var n in new long[] { 37, 38, 40, 99, 1037 })
                    {
                        var point = SourceFrameSelector.SamplePoint(start, sourceIn, s, n, project, source);
                        var (rn, rd) = Reference(start, sourceIn, s, n, project, source);
                        Assert.Equal(rn * point.TicksDenominator, (BigInteger)point.TicksNumerator * rd);
                    }
                }
    }

    [Fact]
    public void At_1x_the_speed_overload_is_exactly_D009()
    {
        foreach (var project in Rates)
            foreach (var source in Rates.Cast<FrameRate?>().Append(null))
                foreach (var n in new long[] { 3, 4, 17, 1003 })
                {
                    var start = MediaTime.FromFrame(3, project);
                    var in1 = SourceFrameSelector.SamplePoint(start, new MediaTime(99_999), n, project, source);
                    var in2 = SourceFrameSelector.SamplePoint(start, new MediaTime(99_999), ClipSpeed.Normal, n, project, source);
                    Assert.Equal(in1, in2);
                }
    }

    [Fact]
    public void At_2x_frames_are_skipped_and_at_half_speed_repeated()
    {
        // 25 fps source and project, SourceIn 0: at 2× frame n shows source frame 2n; at 0.5× ⌊n/2 + ¼⌋.
        var rate = FrameRate.Fps25;
        var frames = Enumerable.Range(0, 200).Select(i => new SourceTimestamp(i, new TimeBase(1, 25))).ToList();
        int Shown(ClipSpeed s, long n) => SourceFrameSelector.Select(frames, MediaTime.Zero,
            SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, s, n, rate, rate));

        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, Enumerable.Range(0, 5).Select(n => Shown(ClipSpeed.FromSteps(40), n)));
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, Enumerable.Range(0, 6).Select(n => Shown(ClipSpeed.FromSteps(10), n)));
        Assert.Equal(new[] { 0, 4, 8, 12 }, Enumerable.Range(0, 4).Select(n => Shown(ClipSpeed.Max, n)));
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1, 1, 2 }, Enumerable.Range(0, 9).Select(n => Shown(ClipSpeed.Min, n)));
    }

    // --- Audio -----------------------------------------------------------------------------------------

    [Fact]
    public void SourceTimeAt_is_the_floor_of_the_exact_source_time()
    {
        foreach (var k in Steps)
        {
            var s = ClipSpeed.FromSteps(k);
            var start = new MediaTime(33_366_667);
            var sourceIn = new MediaTime(12_345_679);
            foreach (var sample in new long[] { 160_160, 160_161, 200_000, 1_234_567 })
            {
                // SourceIn + (sample·10⁷/48000 − S)·a/b
                var exact = new BigInteger(sourceIn.Ticks) * 48_000 * s.Denominator
                            + (new BigInteger(sample) * Tps - new BigInteger(start.Ticks) * 48_000) * s.Numerator;
                var denominator = new BigInteger(48_000) * s.Denominator;
                var expected = BigInteger.Divide(exact, denominator) - (exact.Sign < 0 && exact % denominator != 0 ? 1 : 0);
                Assert.Equal((long)expected, AudioTiming.SourceTimeAt(sample, start, sourceIn, s).Ticks);
            }
        }
    }

    [Fact]
    public void A_stream_starting_at_the_source_of_timeline_sample_k_is_placed_at_k()
    {
        foreach (var k in Steps)
        {
            var s = ClipSpeed.FromSteps(k);
            var start = new MediaTime(40_000_000);
            var sourceIn = new MediaTime(10_000_000);
            foreach (var sample in new long[] { 192_000, 192_001, 250_000, 999_999 })
            {
                var sourceSample = AudioTiming.NearestSample(AudioTiming.SourceTimeAt(sample, start, sourceIn, s));
                var placed = AudioTiming.TimelineSampleOfStreamStart(start, sourceIn, s, sourceSample);
                // Two roundings (tick floor, sample nearest) of ≤ 1 source sample, scaled by 1/s ≤ 4.
                Assert.InRange(placed - sample, -5, 5);
            }
        }
        // Exact case: 2×, clip at 1 s with SourceIn 3 s: timeline sample 96 000 (2 s) ↔ source 5 s = sample 240 000.
        Assert.Equal(96_000, AudioTiming.TimelineSampleOfStreamStart(new MediaTime(Tps), new MediaTime(3 * Tps), ClipSpeed.FromSteps(40), 240_000));
        Assert.Equal(new MediaTime(5 * Tps), AudioTiming.SourceTimeAt(96_000, new MediaTime(Tps), new MediaTime(3 * Tps), ClipSpeed.FromSteps(40)));
    }

    // --- Snapshot ----------------------------------------------------------------------------------------

    [Fact]
    public void A_speed_change_is_not_presentation_only_and_the_spans_carry_the_speed()
    {
        var rate = FrameRate.Fps25;
        var project = new Project { Settings = { FrameRate = rate } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        var a1 = new Track { Type = TrackType.Audio, Name = "A1" };
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(a1);
        var videoAsset = new MediaAsset { FilePath = @"C:\m\v.mp4", Kind = MediaKind.Video, Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), FrameRate = rate, AudioCodec = "aac" } };
        var audioAsset = new MediaAsset { FilePath = @"C:\m\a.wav", Kind = MediaKind.Audio, Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), AudioCodec = "pcm" } };
        project.MediaAssets.AddRange(new[] { videoAsset, audioAsset });
        var video = new VideoClip { MediaAssetId = videoAsset.Id, Duration = MediaTime.FromFrame(50, rate), SourceOut = MediaTime.FromFrame(50, rate) };
        var music = new AudioClip { MediaAssetId = audioAsset.Id, Duration = MediaTime.FromFrame(50, rate), SourceOut = MediaTime.FromFrame(50, rate) };
        v1.Clips.Add(video);
        a1.Clips.Add(music);

        var before = PlaybackSnapshotBuilder.Build(project, 1);
        video.Speed = ClipSpeed.FromSteps(27);
        music.Speed = ClipSpeed.Min;
        var after = PlaybackSnapshotBuilder.Build(project, 2);

        Assert.False(after.DiffersOnlyInPresentation(before));
        Assert.Equal(ClipSpeed.FromSteps(27), after.PictureAt(MediaTime.Zero)!.Speed);
        Assert.Equal(SpanStatus.Video, after.PictureAt(MediaTime.Zero)!.Status);
        Assert.Equal(new[] { ClipSpeed.FromSteps(27), ClipSpeed.Min }, after.AudioSpans.Select(a => a.Speed));
        Assert.All(after.AudioSpans, a => Assert.Equal(SpanStatus.Audio, a.Status));
    }
}
