using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using Xunit;
using static AiVideoEditor.Core.Tests.CameraMotionEstimatorTests;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D048 stage 1 (§2, acceptance 1–2), the Core parts of the route selection at <c>T</c>: the clean-frame locator (the source
/// times of <c>T</c> and <c>T − 5 s</c> through the clip's timing, exact in ticks; the handle before <c>SourceIn</c>; the
/// file's first frame before its start) and the control-point transfer (a known camera motion recovered; an unreliable
/// pair → <see cref="Messages.RouteSelectionTransferFailed"/>, no point).
/// </summary>
public class RouteSelectionTests
{
    private static readonly FrameRate Fps25 = FrameRate.Fps25;
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static VideoClip Clip(double startSeconds, double sourceInSeconds, ClipSpeed speed, double durationSeconds = 20) => new()
    {
        MediaAssetId = Guid.NewGuid(), TimelineStart = S(startSeconds), Duration = S(durationSeconds), SourceIn = S(sourceInSeconds),
        SourceOut = S(sourceInSeconds + durationSeconds * speed.Numerator / speed.Denominator), Speed = speed
    };

    [Theory]
    [InlineData(20, 30.0, 25.0)]      // 1×: T 20 s on a clip at 10 s from the source's 20 s (a trimmed head)
    [InlineData(40, 40.0, 30.0)]      // 2×: five timeline seconds are ten source seconds
    [InlineData(10, 25.0, 22.5)]      // 0.5×
    public void The_clean_frame_is_five_timeline_seconds_before_T_through_the_speed(int steps, double target, double clean)
    {
        var clip = Clip(10, 20, ClipSpeed.FromSteps(steps));
        var frames = RouteSelectionLocator.Locate(clip, 500, Fps25)!.Value;   // T = 20 s
        Assert.Equal(new RouteSelectionFrames(S(target), S(clean), false), frames);
    }

    [Fact]
    public void Before_the_clips_start_the_handle_is_used_and_before_the_files_start_its_first_frame()
    {
        var handle = RouteSelectionLocator.Locate(Clip(10, 8, ClipSpeed.Normal), 300, Fps25)!.Value;   // T = 12 s
        Assert.Equal(new RouteSelectionFrames(S(10), S(5), false), handle);                            // before SourceIn (8 s)

        var start = RouteSelectionLocator.Locate(Clip(10, 2, ClipSpeed.Normal), 300, Fps25)!.Value;
        Assert.Equal(new RouteSelectionFrames(S(4), MediaTime.Zero, true), start);                     // −1 s → the first frame
    }

    [Fact]
    public void The_times_are_exact_in_ticks_at_a_fractional_rate()
    {
        var ntsc = new FrameRate(30000, 1001);
        var frames = RouteSelectionLocator.Locate(Clip(0, 0, ClipSpeed.Normal), 300, ntsc)!.Value;
        Assert.Equal(new MediaTime(100_100_000), frames.TargetSourceTime);   // 300 · 1001 / 30000 s
        Assert.Equal(new MediaTime(50_050_000), frames.CleanSourceTime);     // 5 s = 150 frames before
    }

    [Fact]
    public void T_must_be_a_frame_inside_the_clip()
    {
        var clip = Clip(10, 0, ClipSpeed.Normal);                            // frames [250, 750)
        Assert.NotNull(RouteSelectionLocator.Locate(clip, 250, Fps25));
        Assert.NotNull(RouteSelectionLocator.Locate(clip, 749, Fps25));
        Assert.Null(RouteSelectionLocator.Locate(clip, 750, Fps25));          // T = Ve: no frame of the clip
        Assert.Null(RouteSelectionLocator.Locate(clip, 249, Fps25));
    }

    [Fact]
    public void A_known_camera_motion_carries_the_click_back_to_the_clean_frame()
    {
        var truth = Similarity(1.03, 0.01, 9, -5);                           // the camera between the clean frame and T
        var holdOnClean = new NormalizedPoint(0.42, 0.61);
        var normalized = WallTransform.FromPixels(truth, W, H);
        var (x, y) = normalized.Apply(holdOnClean.X, holdOnClean.Y);         // where the user clicks at T

        var result = RouteSelectionTransfer.Transfer(Wall, S(0), Warp(Wall, truth), S(5), new NormalizedPoint(x, y));
        Assert.True(result.Succeeded, result.Error);
        var p = result.Point!.Value;
        var error = Math.Sqrt(Math.Pow((p.X - holdOnClean.X) * W, 2) + Math.Pow((p.Y - holdOnClean.Y) * H, 2));
        Assert.True(error <= 0.5, $"error {error:0.###} px");
        Assert.Null(result.Error);
    }

    [Fact]
    public void An_unreliable_pair_is_an_error_with_no_point_and_the_same_frame_is_the_click_itself()
    {
        var failed = RouteSelectionTransfer.Transfer(Wall, S(0), SyntheticWall(W, H, seed: 99), S(5), new NormalizedPoint(0.5, 0.5));
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Point);
        Assert.Equal(Messages.RouteSelectionTransferFailed, failed.Error);

        var same = RouteSelectionTransfer.Transfer(Wall, S(3), Wall, S(3), new NormalizedPoint(0.3, 0.7));
        Assert.Equal(new NormalizedPoint(0.3, 0.7), same.Point);
    }

    // --- the frames (D033) and the service ----------------------------------------------------------------------------

    private static MediaAsset Asset(string? transfer = null, bool missing = false) => new()
    {
        FilePath = "clip.mp4", Kind = MediaKind.Video, IsMissing = missing, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata
        {
            Duration = S(60), FrameRate = Fps25, AvgFrameRate = Fps25, Width = 1920, Height = 1080, StartTime = S(1),
            ColorTransfer = transfer, ColorPrimaries = transfer is null ? null : HdrColor.Bt2020Primaries,
            ColorMatrix = transfer is null ? null : HdrColor.Bt2020NclMatrix, BitDepth = transfer is null ? 8 : 10
        }
    };

    [Theory]
    [InlineData(null, null)]                                  // SDR: decoded as it is
    [InlineData(HdrColor.PqTransfer, HdrTransfer.Pq)]         // HDR: the D033 tone mapping to the SDR picture
    [InlineData(HdrColor.HlgTransfer, HdrTransfer.Hlg)]
    public async Task A_frame_is_decoded_by_source_time_with_the_assets_HDR_rule(string? transfer, HdrTransfer? expected)
    {
        var decoder = new ListDecoder(10, 11, 12, 13);        // frames at 10, 11, 12, 13 / 25 s of the file's clock
        var frame = await new RouteSelectionFrameSource(decoder).DecodeAsync(Asset(transfer), new MediaTime(4_400_000), 960, 960, default);

        var request = Assert.Single(decoder.Requests);
        Assert.Equal(expected, request.Hdr?.Transfer);
        Assert.Equal(new SourceSamplePoint(4_400_000, 1), request.FirstSamplePoint);
        Assert.Equal((960, 960, HardwareDecoding.Disabled, true, S(1)),
            (request.MaxWidth, request.MaxHeight, request.Hardware, request.StrictEnd, request.StartTime));
        Assert.Equal(new MediaTime(4_400_000), frame.SourceTime);                          // 36 / 25 s − 1 s: the last at or before
        Assert.Equal(11, frame.Frame.Timestamp.Pts - 25);
    }

    [Fact]
    public async Task A_stream_without_a_frame_is_a_decode_error()
    {
        await Assert.ThrowsAsync<VideoDecodeException>(() =>
            new RouteSelectionFrameSource(new ListDecoder()).DecodeAsync(Asset(), S(2), 960, 960, default));
    }

    [Fact]
    public async Task The_service_carries_the_click_to_the_clean_frame_and_decodes_it_at_the_proposal_size()
    {
        var truth = Similarity(1.02, 0.0, 6, -4);
        var holdOnClean = new NormalizedPoint(0.4, 0.55);
        var (x, y) = WallTransform.FromPixels(truth, W, H).Apply(holdOnClean.X, holdOnClean.Y);
        var clip = Clip(10, 20, ClipSpeed.Normal);
        var frames = new SceneFrames(t => t == S(30) ? Warp(Wall, truth) : Wall);    // T at 20 s → the source's 30 s

        var preparation = await new RouteSelectionService(frames).PrepareAsync(clip, Asset(), 500, Fps25, new NormalizedPoint(x, y), default);

        var prepared = preparation.Prepared!;
        Assert.Null(preparation.Error);
        var error = Math.Sqrt(Math.Pow((prepared.ControlPoint.X - holdOnClean.X) * W, 2) + Math.Pow((prepared.ControlPoint.Y - holdOnClean.Y) * H, 2));
        Assert.True(error <= 0.5, $"error {error:0.###} px");
        Assert.Equal(S(25), prepared.Clean.SourceTime);
        Assert.Equal(new[] { (S(30), 960), (S(25), 960), (S(25), 1920) }, frames.Requests);
    }

    [Fact]
    public async Task A_4K_clean_frame_is_decoded_inside_the_D047_proposal_box()
    {
        var asset = Asset();
        asset.Metadata!.Width = 3840;
        asset.Metadata.Height = 2160;
        var frames = new SceneFrames(_ => Wall);
        var preparation = await new RouteSelectionService(frames).PrepareAsync(Clip(10, 20, ClipSpeed.Normal), asset, 500, Fps25,
            new NormalizedPoint(0.5, 0.5), default);
        Assert.Null(preparation.Error);
        Assert.Equal((S(25), HoldProposalGenerator.DefaultMaxWidth), frames.Requests[^1]);
    }

    [Fact]
    public async Task Failures_are_messages_and_nothing_else()
    {
        var clip = Clip(10, 20, ClipSpeed.Normal);
        var click = new NormalizedPoint(0.5, 0.5);

        var unreliable = new SceneFrames(t => t == S(25) ? SyntheticWall(W, H, seed: 99) : Wall);
        var failed = await new RouteSelectionService(unreliable).PrepareAsync(clip, Asset(), 500, Fps25, click, default);
        Assert.Equal((null, Messages.RouteSelectionTransferFailed), (failed.Prepared, failed.Error));
        Assert.Equal(2, unreliable.Requests.Count);                                       // no clean frame for the model

        var broken = new RouteSelectionService(new RouteSelectionFrameSource(new ListDecoder(throws: true)));
        var decode = await broken.PrepareAsync(clip, Asset(), 500, Fps25, click, default);
        Assert.Equal(string.Format(Messages.RouteSelectionFrameUnavailable, "no decoder"), decode.Error);

        var none = new SceneFrames(_ => Wall);
        Assert.Equal(Messages.RouteSelectionMediaUnavailable, (await new RouteSelectionService(none).PrepareAsync(clip, Asset(missing: true), 500, Fps25, click, default)).Error);
        Assert.Equal(Messages.RouteSelectionOutsideClip, (await new RouteSelectionService(none).PrepareAsync(clip, Asset(), 750, Fps25, click, default)).Error);
        Assert.Empty(none.Requests);
    }

    /// <summary>Frames of the file's clock at 25 fps: each read returns the next listed pts, from the first.</summary>
    private sealed class ListDecoder(params int[] pts) : IVideoDecoder
    {
        private readonly bool _throws;
        public ListDecoder(bool throws) : this() => _throws = throws;
        public List<VideoDecodeRequest> Requests { get; } = new();

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            if (_throws) throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "no decoder");
            Requests.Add(request);
            return Task.FromResult<IVideoFrameStream>(new Stream(new Queue<int>(pts.Select(p => p + 25))));   // + the file's 1 s start
        }

        private sealed class Stream(Queue<int> pts) : IVideoFrameStream
        {
            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default) =>
                ValueTask.FromResult(pts.Count == 0 ? null
                    : new DecodedFrame(2, 2, 8, new byte[16], new SourceTimestamp(pts.Dequeue(), new TimeBase(1, 25))));

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>The synthetic wall as decoded frames; the time and the size asked are recorded.</summary>
    private sealed class SceneFrames(Func<MediaTime, GrayImage> scene) : IRouteSelectionFrameSource
    {
        public List<(MediaTime, int)> Requests { get; } = new();

        public Task<RouteSelectionFrame> DecodeAsync(MediaAsset asset, MediaTime sourceTime, int maxWidth, int maxHeight, CancellationToken cancellationToken)
        {
            Requests.Add((sourceTime, maxWidth));
            var image = scene(sourceTime);
            var pixels = new byte[image.Width * image.Height * 4];
            for (var i = 0; i < image.Pixels.Length; i++)
            {
                var v = (byte)Math.Clamp(Math.Round(image.Pixels[i]), 0, 255);
                (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (v, v, v, 255);
            }
            var frame = new DecodedFrame(image.Width, image.Height, image.Width * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
            return Task.FromResult(new RouteSelectionFrame(frame, sourceTime));
        }
    }
}