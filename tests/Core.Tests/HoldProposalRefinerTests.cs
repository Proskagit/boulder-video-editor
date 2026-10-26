using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public sealed class HoldProposalRefinerTests
{
    private static readonly FrameRate Rate = new(25, 1);
    private static readonly MediaTime Playhead = MediaTime.FromSeconds(1);

    [Fact]
    public async Task RefineAsync_success_creates_valid_hold_at_full_resolution()
    {
        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h)),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ =>
                RectangleMask(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2, 100, 100))));

        var result = await refiner.RefineAsync(
            CreateProposal(new NormalizedPoint(0.5, 0.5)),
            CreateClip(),
            CreateAsset(1920, 1080),
            Playhead,
            Rate);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Hold);
        Assert.True(result.Hold!.Points.Length >= HoldMarkupRules.MinPoints);
        Assert.True(result.Hold.SurfaceLab.IsFinite);
        Assert.True(result.Hold.WallLab.IsFinite);
        Assert.True(result.Hold.SourceTime >= MediaTime.Zero);
    }

    [Theory]
    [InlineData(1920, 1080, 0.25, 0.75, 480, 810)]
    [InlineData(1080, 1920, 0.5, 0.5, 540, 960)]
    [InlineData(2560, 1080, 0.5, 0.5, 1280, 540)]
    public async Task RefineAsync_maps_normalized_centroid_to_full_resolution_pixels(
        int displayWidth, int displayHeight, double nx, double ny, double expectedX, double expectedY)
    {
        var requestCapture = default(VideoDecodeRequest);
        var promptCapture = default(PointD?);

        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h), r => requestCapture = r),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, points =>
            {
                promptCapture = points[0];
                return RectangleMask(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2, 80, 80);
            })));

        await refiner.RefineAsync(
            CreateProposal(new NormalizedPoint(nx, ny)),
            CreateClip(displayWidth, displayHeight),
            CreateAsset(displayWidth, displayHeight),
            Playhead,
            Rate);

        Assert.NotNull(requestCapture);
        Assert.Equal(displayWidth, requestCapture!.MaxWidth);
        Assert.Equal(displayHeight, requestCapture.MaxHeight);
        Assert.NotNull(promptCapture);
        Assert.Equal(expectedX, promptCapture!.Value.X, 1e-6);
        Assert.Equal(expectedY, promptCapture.Value.Y, 1e-6);
    }

    [Fact]
    public async Task RefineAsync_empty_mask_returns_failure_without_hold()
    {
        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h)),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ =>
                new HoldMaskSet(ImmutableArray.Create<HoldMask>()))));

        var result = await refiner.RefineAsync(
            CreateProposal(new NormalizedPoint(0.5, 0.5)),
            CreateClip(),
            CreateAsset(),
            Playhead,
            Rate);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Hold);
        Assert.Equal(Messages.Holds_RefinementFailed, result.ErrorMessage);
    }

    [Fact]
    public async Task RefineAsync_model_error_is_propagated()
    {
        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h)),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ =>
                throw new InvalidOperationException("Model not ready"))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            refiner.RefineAsync(CreateProposal(), CreateClip(), CreateAsset(), Playhead, Rate));
    }

    [Fact]
    public async Task RefineAsync_decoder_error_is_propagated()
    {
        var refiner = CreateRefiner(
            new FailingVideoDecoder(new VideoDecodeException(VideoDecodeError.FileNotFound, "missing")),
            new FakeSegmentationService());

        await Assert.ThrowsAsync<VideoDecodeException>(() =>
            refiner.RefineAsync(CreateProposal(), CreateClip(), CreateAsset(), Playhead, Rate));
    }

    [Fact]
    public async Task RefineAsync_respects_cancellation()
    {
        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h)),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ =>
                RectangleMask(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2, 80, 80))));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            refiner.RefineAsync(CreateProposal(), CreateClip(), CreateAsset(), Playhead, Rate, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RefineAsync_reports_progress_in_order()
    {
        var refiner = CreateRefiner(
            new FakeVideoDecoder((w, h) => CreateFrame(w, h)),
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ =>
                RectangleMask(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2, 80, 80))));

        var reports = new List<double>();
        var progress = new SynchronousProgress<double>(reports.Add);

        await refiner.RefineAsync(CreateProposal(), CreateClip(), CreateAsset(), Playhead, Rate, progress);

        Assert.Contains(0.0, reports);
        Assert.Contains(1.0, reports);
        Assert.True(reports.SequenceEqual(reports.OrderBy(x => x)));
    }

    #region Helpers

    private static IHoldProposalRefiner CreateRefiner(IVideoDecoder decoder, IHoldSegmentationService segmentation) =>
        new HoldProposalRefiner(decoder, segmentation);

    private static HoldProposal CreateProposal(NormalizedPoint? centroid = null)
    {
        var c = centroid ?? new NormalizedPoint(0.5, 0.5);
        return new HoldProposal(
            Guid.NewGuid(),
            ImmutableArray.Create(
                new NormalizedPoint(c.X - 0.05, c.Y - 0.05),
                new NormalizedPoint(c.X + 0.05, c.Y - 0.05),
                new NormalizedPoint(c.X + 0.05, c.Y + 0.05),
                new NormalizedPoint(c.X - 0.05, c.Y + 0.05)),
            c,
            0.9,
            0.01);
    }

    private static VideoClip CreateClip(int displayWidth = 1920, int displayHeight = 1080) =>
        new()
        {
            Id = Guid.NewGuid(),
            MediaAssetId = Guid.NewGuid(),
            TimelineStart = MediaTime.Zero,
            Duration = MediaTime.FromSeconds(10),
            SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromSeconds(10)
        };

    private static MediaAsset CreateAsset(int displayWidth = 1920, int displayHeight = 1080) =>
        new()
        {
            Id = Guid.NewGuid(),
            FilePath = "test.mp4",
            Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Width = displayWidth,
                Height = displayHeight,
                DisplayWidth = displayWidth,
                DisplayHeight = displayHeight,
                FrameRate = new FrameRate(25, 1),
                AvgFrameRate = new FrameRate(25, 1),
                Duration = MediaTime.FromSeconds(10),
                StartTime = MediaTime.Zero
            }
        };

    private static DecodedFrame CreateFrame(int width, int height) =>
        new(width, height, width * DecodedFrame.BytesPerPixel,
            new byte[width * height * DecodedFrame.BytesPerPixel],
            new SourceTimestamp(25, new TimeBase(1, 25)));

    private static HoldMaskSet RectangleMask(int frameW, int frameH, int cx, int cy, int rw, int rh)
    {
        var pixels = new byte[frameW * frameH];
        var x0 = Math.Max(0, cx - rw / 2);
        var y0 = Math.Max(0, cy - rh / 2);
        var x1 = Math.Min(frameW, x0 + rw);
        var y1 = Math.Min(frameH, y0 + rh);
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                pixels[y * frameW + x] = 1;
        return new HoldMaskSet(ImmutableArray.Create(new HoldMask(frameW, frameH, pixels, 0.95)));
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class FakeVideoDecoder : IVideoDecoder
    {
        private readonly Func<int, int, DecodedFrame> _frameFactory;
        private readonly Action<VideoDecodeRequest>? _capture;

        public FakeVideoDecoder(Func<int, int, DecodedFrame> frameFactory, Action<VideoDecodeRequest>? capture = null)
        {
            _frameFactory = frameFactory;
            _capture = capture;
        }

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _capture?.Invoke(request);
            return Task.FromResult<IVideoFrameStream>(new FakeFrameStream(_frameFactory(request.MaxWidth, request.MaxHeight)));
        }
    }

    private sealed class FailingVideoDecoder(VideoDecodeException exception) : IVideoDecoder
    {
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            Task.FromException<IVideoFrameStream>(exception);
    }

    private sealed class FakeFrameStream(DecodedFrame frame) : IVideoFrameStream
    {
        private bool _read;
        public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
        {
            if (_read) return new ValueTask<DecodedFrame?>((DecodedFrame?)null);
            _read = true;
            return new ValueTask<DecodedFrame?>(frame);
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeSegmentationService : IHoldSegmentationService
    {
        private readonly Func<DecodedFrame, CancellationToken, IHoldFrame>? _factory;

        public FakeSegmentationService() { }

        public FakeSegmentationService(Func<DecodedFrame, CancellationToken, IHoldFrame> factory) => _factory = factory;

        public InferenceDevice Device => InferenceDevice.Gpu;
        public event EventHandler? DeviceChanged { add { } remove { } }

        public Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HoldModelStatus(HoldModelState.Ready, null));

        public Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_factory is null) throw new InvalidOperationException("No factory configured.");
            return Task.FromResult(_factory(frame, cancellationToken));
        }
    }

    private sealed class FakeHoldFrame : IHoldFrame
    {
        private readonly int _width;
        private readonly int _height;
        private readonly Func<IReadOnlyList<PointD>, HoldMaskSet> _predict;

        public FakeHoldFrame(int width, int height, Func<IReadOnlyList<PointD>, HoldMaskSet> predict)
        {
            _width = width;
            _height = height;
            _predict = predict;
        }

        public int Width => _width;
        public int Height => _height;

        public Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default) =>
            Task.FromResult(_predict(points));

        public void Dispose() { }
    }

    #endregion
}
