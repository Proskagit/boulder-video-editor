using System.Diagnostics;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// Step 31.2 integration (D047): decodes a markup frame at the proposal-pass resolution (1920×1080 by default),
/// runs the pinned hold segmentation model and generates ranked proposals through <see cref="IHoldProposalService"/>.
/// No project mutation occurs here; the caller decides which proposals to refine into <see cref="Hold"/> entities.
/// </summary>
public sealed class HoldProposalGenerator : IHoldProposalGenerator
{
    /// <summary>The default proposal-pass resolution. The decoder fits the source inside this box preserving aspect ratio.</summary>
    public const int DefaultMaxWidth = 1920;

    public const int DefaultMaxHeight = 1080;

    private readonly IVideoDecoder _decoder;
    private readonly IHoldSegmentationService _segmentation;
    private readonly IHoldProposalService _proposalService;

    public HoldProposalGenerator(
        IVideoDecoder decoder,
        IHoldSegmentationService segmentation,
        IHoldProposalService proposalService)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
        _proposalService = proposalService ?? throw new ArgumentNullException(nameof(proposalService));
    }

    public async Task<ProposalGenerationResult> GenerateAsync(
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        ProposalPipelineOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);
        options.Validate();

        var totalSw = Stopwatch.StartNew();
        var metadata = asset.Metadata ?? throw new InvalidOperationException($"{asset.FileName} has no media information yet.");
        var displayWidth = metadata.DisplayWidth ?? metadata.Width ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var displayHeight = metadata.DisplayHeight ?? metadata.Height ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var samplePoint = SourceFrameSelector.SamplePoint(clip, playhead.ToFrameFloor(rate), rate, metadata);

        progress?.Report(0);

        var decodeSw = Stopwatch.StartNew();
        var frame = await DecodeProposalFrameAsync(
            asset.FilePath,
            startTime,
            samplePoint,
            SourceFrameSelector.NominalRate(metadata),
            displayWidth,
            displayHeight,
            HdrColor.Classify(asset.Kind, metadata).Conversion,
            progress,
            cancellationToken).ConfigureAwait(false);
        var decodeMs = decodeSw.Elapsed.TotalMilliseconds;

        progress?.Report(0.25);

        var prepareSw = Stopwatch.StartNew();
        using var holdFrame = await _segmentation.PrepareFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        var prepareMs = prepareSw.Elapsed.TotalMilliseconds;

        progress?.Report(0.35);

        var result = await _proposalService.GenerateProposalsAsync(holdFrame, options, cancellationToken).ConfigureAwait(false);

        progress?.Report(1.0);

        var totalMs = totalSw.Elapsed.TotalMilliseconds;
        var serviceDiagnostics = result.Diagnostics;
        return result with
        {
            Diagnostics = new ProposalGenerationDiagnostics(
                DecodeMs: decodeMs,
                PrepareFrameMs: prepareMs,
                PredictionLoopMs: serviceDiagnostics?.PredictionLoopMs ?? 0,
                PredictionCount: serviceDiagnostics?.PredictionCount ?? 0,
                PostProcessMs: serviceDiagnostics?.PostProcessMs ?? 0,
                NmsMs: serviceDiagnostics?.NmsMs ?? 0,
                TotalMs: totalMs)
        };
    }

    private async Task<DecodedFrame> DecodeProposalFrameAsync(
        string filePath,
        MediaTime startTime,
        SourceSamplePoint samplePoint,
        FrameRate? nominalRate,
        int displayWidth,
        int displayHeight,
        HdrConversion? hdrConversion,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = await _decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = filePath,
            StartTime = startTime,
            FirstSamplePoint = samplePoint,
            NominalFrameRate = nominalRate,
            MaxWidth = DefaultMaxWidth,
            MaxHeight = DefaultMaxHeight,
            Hardware = HardwareDecoding.Disabled,
            Hdr = hdrConversion,
            StrictEnd = true
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report(0.05);

        var current = await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{filePath}' produced no frame.");

        // SourceFrameSelector: pick the last frame at or before the sample point (hold-first / hold-last).
        while (await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } next &&
               SourceFrameSelector.IsAtOrBefore(next.Timestamp, startTime, samplePoint))
        {
            current = next;
        }

        progress?.Report(0.2);

        // Decoders may reuse their internal buffers; copy so the frame outlives the stream.
        return CopyFrame(current);
    }

    private static DecodedFrame CopyFrame(DecodedFrame frame)
    {
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        var pixels = new byte[rowBytes * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
            source.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        return new DecodedFrame(frame.Width, frame.Height, rowBytes, pixels, frame.Timestamp);
    }
}
