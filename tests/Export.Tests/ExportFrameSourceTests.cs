using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Tests.Playback;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// Phase 8 Step 2 (D023): the export's frame source is offline and blocking — never realtime playback
/// semantics (no late/previous frame on underrun, no placeholder, no pending layer) — decodes at full
/// resolution in software, and turns every decode failure into an <see cref="ExportException"/>.
/// </summary>
public sealed class ExportFrameSourceTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string File = @"C:\media\a.mp4";

    private readonly FakeVideoDecoder _decoder = new();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>One video clip [0, frames) of <see cref="File"/> at 1×, source = timeline.</summary>
    private static PlaybackSnapshot Snapshot(long frames, SpanStatus status = SpanStatus.Video)
    {
        var clip = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var span = new PictureSpan(clip, asset, status, F(0), F(frames), MediaTime.Zero,
            status == SpanStatus.Video ? null : "not playable");
        return new PlaybackSnapshot(1, Rate, F(frames),
            ImmutableArray.Create(new VideoLayer(Guid.NewGuid(), ImmutableArray.Create(span))),
            ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty.Add(asset, new PlaybackAsset(asset, File, MediaKind.Video, MediaTime.Zero, Rate)),
            new FrameSize(1920, 1080));
    }

    private static int Number(ExportFrame frame) => FakeVideoDecoder.Number(Assert.Single(frame.Layers).Frame!);

    [Fact]
    public async Task A_stalled_decoder_blocks_the_frame_instead_of_returning_an_earlier_one()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        var release = _decoder.HoldAfter(File, frames: 3);                       // delivers 0..2, then stalls
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        Assert.Equal(0, Number(await source.GetFrameAsync(0)));
        var pending = source.GetFrameAsync(10);
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);                                        // the Preview would show frame 2, late

        release.SetResult();
        Assert.Equal(10, Number(await pending));
    }

    [Fact]
    public async Task A_decode_failure_mid_stream_fails_the_export_at_that_frame()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        _decoder.FailAfter(File, 6, software: true);                              // frames 0..5, then the stream breaks
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        for (var n = 0; n < 5; n++) Assert.Equal(n, Number(await source.GetFrameAsync(n)));
        var error = await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(5));  // needs frame 6 to be certain
        Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
        Assert.Contains("a.mp4", error.Message);
    }

    [Theory]
    [InlineData(VideoDecodeError.FileNotFound, ExportFailure.DecodeFailed)]
    [InlineData(VideoDecodeError.FrameNotReached, ExportFailure.DecodeFailed)]
    [InlineData(VideoDecodeError.DecoderUnavailable, ExportFailure.EncoderUnavailable)]
    public async Task A_failure_to_open_fails_the_export(VideoDecodeError error, ExportFailure expected)
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        _decoder.FailOpen(File, error);
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        Assert.Equal(expected, (await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(0))).Failure);
    }

    [Fact]
    public async Task A_stream_without_frames_fails_the_export()
    {
        _decoder.Add(File, new FakeSource(Rate, 0));
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        Assert.Equal(ExportFailure.DecodeFailed, (await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(0))).Failure);
    }

    [Theory]
    [InlineData(SpanStatus.Offline)]
    [InlineData(SpanStatus.Unsupported)]
    public async Task An_unplayable_clip_is_an_error_not_a_placeholder(SpanStatus status)
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        await using var source = new ExportFrameSource(Snapshot(50, status), _decoder);

        var error = await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(0));
        Assert.Contains("not playable", error.Message);
        Assert.Empty(_decoder.Requests);
    }

    [Fact]
    public async Task Sources_are_decoded_at_full_resolution_in_software()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        await source.GetFrameAsync(0);

        var request = Assert.Single(_decoder.Requests);
        Assert.Equal((ExportDecodeSettings.FullResolutionBound, ExportDecodeSettings.FullResolutionBound), (request.MaxWidth, request.MaxHeight));
        Assert.True(request.MaxWidth >= 8192);
        Assert.Equal(HardwareDecoding.Disabled, request.Hardware);
    }

    [Fact]
    public async Task Skipping_frames_reads_forward_in_one_stream_and_frames_must_ascend()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        Assert.Equal(3, Number(await source.GetFrameAsync(3)));
        Assert.Equal(40, Number(await source.GetFrameAsync(40)));
        Assert.Equal(1, _decoder.OpenCount(File));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameAsync(40));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameAsync(39));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetFrameAsync(50));
    }

    [Fact]
    public async Task Cancellation_propagates_and_dispose_closes_every_stream()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        _decoder.HoldAfter(File, frames: 2);
        var source = new ExportFrameSource(Snapshot(50), _decoder);
        await source.GetFrameAsync(0);
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetFrameAsync(20, cts.Token));
        Assert.Equal(1, _decoder.LiveStreams);
        await source.DisposeAsync();
        Assert.Equal(0, _decoder.LiveStreams);
    }

    [Fact]
    public async Task A_layer_leaving_the_composition_closes_its_stream()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        var snapshot = Snapshot(30);
        var longer = new PlaybackSnapshot(1, Rate, F(40), snapshot.VideoLayers, snapshot.AudioSpans, snapshot.Assets, snapshot.Canvas);
        await using var source = new ExportFrameSource(longer, _decoder);

        await source.GetFrameAsync(29);
        Assert.Equal(1, _decoder.LiveStreams);
        Assert.Empty((await source.GetFrameAsync(30)).Layers);                     // the gap after the clip: no layer
        Assert.Equal(0, _decoder.LiveStreams);
        Assert.Empty(source.ReaderClipIds);
    }

    [Fact]
    public void The_export_cannot_use_realtime_playback_readers()
    {
        // The Preview's SpanReader / VideoPipeline live in Timeline; Export references Core only.
        var references = typeof(ExportFrameSource).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("AiVideoEditor.Timeline", references);
        Assert.Contains("AiVideoEditor.Core", references);
    }

    // --- FrameCount = the Preview's frame range --------------------------------------------------------

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(25, 1)]
    public void Frame_count_is_playbacks_last_frame_plus_one(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var random = new Random(num);
        for (var i = 0; i < 2_000; i++)
        {
            var duration = new MediaTime(random.NextInt64(1, 36_000_000_000));
            var snapshot = new PlaybackSnapshot(1, rate, duration, ImmutableArray<VideoLayer>.Empty, ImmutableArray<AudioSpan>.Empty,
                ImmutableDictionary<Guid, PlaybackAsset>.Empty, new FrameSize(2, 2));
            // PlaybackService shows at most frame CeilingFrame(Duration) − 1 (the frame at Duration has no clip).
            Assert.Equal(FrameMath.CeilingFrame(duration, rate), ExportOutput.For(snapshot).FrameCount);
        }
    }

    [Fact]
    public async Task An_export_frame_becomes_the_shared_draw_plan_at_the_canvas_size()
    {
        _decoder.Add(File, new FakeSource(Rate, 100));
        await using var source = new ExportFrameSource(Snapshot(50), _decoder);

        var frame = await source.GetFrameAsync(7);
        var plan = frame.DrawPlan();

        Assert.Equal(new FrameSize(1920, 1080), plan.Canvas);
        Assert.Equal(Affine2D.Identity, plan.CanvasToTarget);
        var op = Assert.IsType<FrameDraw>(Assert.Single(plan.Operations));
        Assert.Same(frame.Layers[0].Frame, op.Frame);
        Assert.True(CompositionDrawPlan.Build(frame.Canvas, Affine2D.Identity, frame.Layers).Operations.SequenceEqual(plan.Operations));
    }
}
