using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public sealed class HoldProposalGeneratorTests
{
    private static readonly FrameRate Rate = new(25, 1);
    private static readonly MediaTime Playhead = MediaTime.FromSeconds(1);

    [Fact]
    public async Task GenerateAsync_decodes_at_proposal_resolution_and_generates_proposals()
    {
        var decoder = new FakeVideoDecoder(new DecodedFrame(
            1920, 1080, 1920 * DecodedFrame.BytesPerPixel,
            new byte[1920 * 1080 * DecodedFrame.BytesPerPixel],
            new SourceTimestamp(25, new TimeBase(1, 25))));

        var segmentation = new FakeSegmentationService((frame, _) =>
        {
            Assert.Equal(1920, frame.Width);
            Assert.Equal(1080, frame.Height);
            return new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height));
        });

        var proposalService = new FakeProposalService((frame, options, _) =>
        {
            Assert.Equal(1920, frame.Width);
            return new ProposalGenerationResult(new[]
            {
                new HoldProposal(
                    Guid.NewGuid(),
                    ImmutableArray.Create(
                        new NormalizedPoint(0.1, 0.1),
                        new NormalizedPoint(0.2, 0.1),
                        new NormalizedPoint(0.2, 0.2),
                        new NormalizedPoint(0.1, 0.2)),
                    new NormalizedPoint(0.15, 0.15),
                    0.9,
                    0.01)
            }, 16);
        });

        var generator = new HoldProposalGenerator(decoder, segmentation, proposalService);
        var result = await generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions());

        Assert.Single(result.Proposals);
        Assert.Equal(0.9, result.Proposals[0].Score);
    }

    [Fact]
    public async Task GenerateAsync_reports_progress_in_order()
    {
        var decoder = new FakeVideoDecoder(CreateFrame(1920, 1080));
        var segmentation = new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height)));
        var proposalService = new FakeProposalService((_, _, _) => new ProposalGenerationResult(Array.Empty<HoldProposal>(), 16));
        var generator = new HoldProposalGenerator(decoder, segmentation, proposalService);

        var reports = new List<double>();
        var progress = new SynchronousProgress<double>(reports.Add);

        await generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions(), progress);

        Assert.Contains(0.0, reports);
        Assert.Contains(1.0, reports);
        Assert.True(reports.SequenceEqual(reports.OrderBy(x => x)));
    }

    [Fact]
    public async Task GenerateAsync_throws_when_decoder_fails()
    {
        var decoder = new FailingVideoDecoder(new VideoDecodeException(VideoDecodeError.FileNotFound, "not found"));
        var generator = new HoldProposalGenerator(decoder, new FakeSegmentationService(), new FakeProposalService());

        await Assert.ThrowsAsync<VideoDecodeException>(() =>
            generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions()));
    }

    [Fact]
    public async Task GenerateAsync_throws_when_model_not_ready()
    {
        var decoder = new FakeVideoDecoder(CreateFrame(1920, 1080));
        var segmentation = new FailingSegmentationService(new InvalidOperationException("Model missing"));
        var generator = new HoldProposalGenerator(decoder, segmentation, new FakeProposalService());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions()));
    }

    [Fact]
    public async Task GenerateAsync_empty_proposals_is_not_an_error()
    {
        var decoder = new FakeVideoDecoder(CreateFrame(1920, 1080));
        var segmentation = new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height)));
        var proposalService = new FakeProposalService((_, _, _) => new ProposalGenerationResult(Array.Empty<HoldProposal>(), 16));
        var generator = new HoldProposalGenerator(decoder, segmentation, proposalService);

        var result = await generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions());

        Assert.Empty(result.Proposals);
    }

    [Fact]
    public async Task GenerateAsync_respects_cancellation_before_decode()
    {
        var decoder = new FakeVideoDecoder(CreateFrame(1920, 1080));
        var segmentation = new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height)));
        var generator = new HoldProposalGenerator(decoder, segmentation, new FakeProposalService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(CreateClip(), CreateAsset(), Playhead, Rate, new ProposalPipelineOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task GenerateAsync_downscales_4k_source_to_1080p_box()
    {
        var decoder = new FakeVideoDecoder(new DecodedFrame(
            1920, 1080, 1920 * DecodedFrame.BytesPerPixel,
            new byte[1920 * 1080 * DecodedFrame.BytesPerPixel],
            new SourceTimestamp(25, new TimeBase(1, 25))));

        var capturedRequest = default(VideoDecodeRequest);
        decoder.CaptureRequest = r => capturedRequest = r;

        var generator = new HoldProposalGenerator(
            decoder,
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height))),
            new FakeProposalService((_, _, _) => new ProposalGenerationResult(Array.Empty<HoldProposal>(), 16)));

        await generator.GenerateAsync(CreateClip(3840, 2160), CreateAsset(3840, 2160), Playhead, Rate, new ProposalPipelineOptions());

        Assert.NotNull(capturedRequest);
        Assert.Equal(1920, capturedRequest!.MaxWidth);
        Assert.Equal(1080, capturedRequest.MaxHeight);
        Assert.Equal(HardwareDecoding.Disabled, capturedRequest.Hardware);
    }

    [Fact]
    public async Task GenerateAsync_maps_proposal_coordinates_to_source_space()
    {
        // Source is 4K, decoded at 1920×1080. A proposal at normalized (0.5, 0.5) in the decoded frame
        // must remain at (0.5, 0.5) — normalized coordinates are display-oriented and aspect-preserving.
        var decoder = new FakeVideoDecoder(new DecodedFrame(
            1920, 1080, 1920 * DecodedFrame.BytesPerPixel,
            new byte[1920 * 1080 * DecodedFrame.BytesPerPixel],
            new SourceTimestamp(25, new TimeBase(1, 25))));

        var proposalService = new FakeProposalService((_, _, _) => new ProposalGenerationResult(new[]
        {
            new HoldProposal(
                Guid.NewGuid(),
                ImmutableArray.Create(
                    new NormalizedPoint(0.45, 0.45),
                    new NormalizedPoint(0.55, 0.45),
                    new NormalizedPoint(0.55, 0.55),
                    new NormalizedPoint(0.45, 0.55)),
                new NormalizedPoint(0.5, 0.5),
                0.9,
                0.01)
        }, 16));

        var generator = new HoldProposalGenerator(
            decoder,
            new FakeSegmentationService((frame, _) => new FakeHoldFrame(frame.Width, frame.Height, _ => TestMaskSet(frame.Width, frame.Height))),
            proposalService);

        var result = await generator.GenerateAsync(CreateClip(3840, 2160), CreateAsset(3840, 2160), Playhead, Rate, new ProposalPipelineOptions());

        Assert.Equal(new NormalizedPoint(0.5, 0.5), result.Proposals[0].Centroid);
    }

    #region Helpers

    private static VideoClip CreateClip(int width = 1920, int height = 1080) =>
        new()
        {
            Id = Guid.NewGuid(),
            MediaAssetId = Guid.NewGuid(),
            TimelineStart = MediaTime.Zero,
            Duration = MediaTime.FromSeconds(10),
            SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromSeconds(10)
        };

    private static MediaAsset CreateAsset(int width = 1920, int height = 1080) =>
        new()
        {
            Id = Guid.NewGuid(),
            FilePath = "test.mp4",
            Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Width = width,
                Height = height,
                DisplayWidth = width,
                DisplayHeight = height,
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

    private static HoldMaskSet TestMaskSet(int w, int h)
    {
        var pixels = new byte[w * h];
        for (var y = h / 2 - 5; y < h / 2 + 5; y++)
            for (var x = w / 2 - 5; x < w / 2 + 5; x++)
                pixels[y * w + x] = 1;
        return new HoldMaskSet(ImmutableArray.Create(new HoldMask(w, h, pixels, 0.95)));
    }

    private sealed class FakeVideoDecoder : IVideoDecoder
    {
        private readonly DecodedFrame _frame;
        public Action<VideoDecodeRequest>? CaptureRequest { get; set; }

        public FakeVideoDecoder(DecodedFrame frame) => _frame = frame;

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            CaptureRequest?.Invoke(request);
            return Task.FromResult<IVideoFrameStream>(new FakeFrameStream(_frame));
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

    private sealed class FailingSegmentationService(Exception exception) : IHoldSegmentationService
    {
        public InferenceDevice Device => InferenceDevice.Gpu;
        public event EventHandler? DeviceChanged { add { } remove { } }

        public Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HoldModelStatus(HoldModelState.Ready, null));

        public Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromException<IHoldFrame>(exception);
    }

    private sealed class FakeProposalService : IHoldProposalService
    {
        private readonly Func<IHoldFrame, ProposalPipelineOptions, CancellationToken, ProposalGenerationResult>? _generate;

        public FakeProposalService() { }

        public FakeProposalService(Func<IHoldFrame, ProposalPipelineOptions, CancellationToken, ProposalGenerationResult> generate) =>
            _generate = generate;

        public Task<ProposalGenerationResult> GenerateProposalsAsync(
            IHoldFrame frame,
            ProposalPipelineOptions options,
            CancellationToken cancellationToken = default)
        {
            if (_generate is null) return Task.FromResult(new ProposalGenerationResult(Array.Empty<HoldProposal>(), 0));
            return Task.FromResult(_generate(frame, options, cancellationToken));
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

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    #endregion
}
