using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>Phase 8 Step 1 (D023): the output of an export — canvas size, exact project frame rate,
/// whole frames covering the sequence, and exactly as many 48 kHz samples as those frames last.</summary>
public class ExportOutputTests
{
    private static PlaybackSnapshot Snapshot(FrameRate rate, MediaTime duration, FrameSize? canvas = null) =>
        new(1, rate, duration, ImmutableArray<VideoLayer>.Empty, ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty, canvas ?? new FrameSize(1920, 1080));

    [Fact]
    public void Size_and_rate_come_from_the_snapshot()
    {
        var output = ExportOutput.For(Snapshot(FrameRate.Ntsc30, MediaTime.FromFrame(10, FrameRate.Ntsc30), new FrameSize(1080, 1920)));

        Assert.Equal(new FrameSize(1080, 1920), output.Size);
        Assert.Equal(new FrameRate(30000, 1001), output.FrameRate);
    }

    [Theory]
    [InlineData(25, 1, 250)]
    [InlineData(30000, 1001, 300)]
    [InlineData(24000, 1001, 1)]
    [InlineData(60, 1, 7201)]
    public void A_duration_on_the_grid_is_exactly_its_frames_and_samples(int num, int den, long frames)
    {
        var rate = new FrameRate(num, den);
        var duration = MediaTime.FromFrame(frames, rate);

        var output = ExportOutput.For(Snapshot(rate, duration));

        Assert.Equal(frames, output.FrameCount);
        Assert.Equal(duration, output.Duration);
        // ceil(duration · 48000 / 10⁷), checked independently
        var expected = (long)Math.Ceiling((decimal)duration.Ticks * 48000 / 10_000_000);
        Assert.Equal(expected, output.AudioSampleCount);
    }

    [Fact]
    public void Ten_seconds_at_25_fps_are_250_frames_and_480000_samples()
    {
        var output = ExportOutput.For(Snapshot(FrameRate.Fps25, MediaTime.FromSeconds(10)));

        Assert.Equal((250L, 480_000L), (output.FrameCount, output.AudioSampleCount));
    }

    [Fact]
    public void A_duration_off_the_grid_is_covered_by_one_more_frame()
    {
        var rate = FrameRate.Fps25;
        var output = ExportOutput.For(Snapshot(rate, MediaTime.FromFrame(10, rate) + new MediaTime(1)));

        Assert.Equal(11, output.FrameCount);
        Assert.Equal(MediaTime.FromFrame(11, rate), output.Duration);
    }

    /// <summary>The frame-count boundary (same rule as playback's last frame,
    /// <c>FrameMath.CeilingFrame(Duration) − 1</c>, checked against it in Export.Tests): exactly N/FPS →
    /// N frames (last index N − 1); one tick more → N + 1; less than one frame → 1.</summary>
    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(25, 1)]
    public void Frame_count_boundary(int num, int den)
    {
        var rate = new FrameRate(num, den);
        foreach (var n in new long[] { 1, 2, 24, 1000, 107_892 })
        {
            var exact = ExportOutput.For(Snapshot(rate, MediaTime.FromFrame(n, rate)));
            Assert.Equal(n, exact.FrameCount);
            Assert.True(MediaTime.FromFrame(exact.FrameCount - 1, rate) < MediaTime.FromFrame(n, rate)); // last index N − 1 starts inside
            Assert.Equal(n + 1, ExportOutput.For(Snapshot(rate, MediaTime.FromFrame(n, rate) + new MediaTime(1))).FrameCount);
            Assert.Equal(n, ExportOutput.For(Snapshot(rate, MediaTime.FromFrame(n, rate) - new MediaTime(1))).FrameCount);
        }

        var oneFrame = MediaTime.FromFrame(1, rate);
        foreach (var ticks in new[] { 1L, oneFrame.Ticks / 2, oneFrame.Ticks - 1 })
            Assert.Equal(1, ExportOutput.For(Snapshot(rate, new MediaTime(ticks))).FrameCount);
    }

    [Fact]
    public void An_empty_sequence_has_no_frames_and_no_samples()
    {
        var output = ExportOutput.For(Snapshot(FrameRate.Fps30, MediaTime.Zero));

        Assert.Equal((0L, 0L), (output.FrameCount, output.AudioSampleCount));
    }

    [Fact]
    public void The_format_is_fixed()
    {
        Assert.Equal(ExportContainer.Mp4, ExportFormat.Container);
        Assert.Equal(ExportVideoCodec.H264, ExportFormat.VideoCodec);
        Assert.Equal(ExportAudioCodec.Aac, ExportFormat.AudioCodec);
        Assert.Equal(".mp4", ExportFormat.FileExtension);
        Assert.Equal(18, ExportFormat.VideoCrf);
        Assert.Equal("medium", ExportFormat.VideoPreset);
        Assert.Equal((48_000, 2, 192_000), (ExportFormat.AudioSampleRate, ExportFormat.AudioChannels, ExportFormat.AudioBitrateBps));
    }

    [Fact]
    public void A_job_needs_an_output_path_and_computes_its_output()
    {
        var snapshot = Snapshot(FrameRate.Fps25, MediaTime.FromSeconds(2));

        Assert.Throws<ArgumentException>(() => new ExportJob(snapshot, " "));
        Assert.Equal(50, new ExportJob(snapshot, @"C:\out.mp4").Output.FrameCount);
    }
}
