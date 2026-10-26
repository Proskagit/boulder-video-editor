using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// Step 31.3 (D047): refine one accepted proposal at the source frame's full display resolution. The proposal's centroid
/// (in normalized display-oriented source coordinates) is re-prompted on the full-resolution frame; the best mask becomes
/// the hold's contour and colour samples.
/// </summary>
public sealed class HoldProposalRefiner : IHoldProposalRefiner
{
    private readonly IVideoDecoder _decoder;
    private readonly IHoldSegmentationService _segmentation;

    public HoldProposalRefiner(IVideoDecoder decoder, IHoldSegmentationService segmentation)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
    }

    public async Task<HoldProposalRefinementResult> RefineAsync(
        HoldProposal proposal,
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);

        var metadata = asset.Metadata ?? throw new InvalidOperationException($"{asset.FileName} has no media information yet.");
        var displayWidth = metadata.DisplayWidth ?? metadata.Width ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var displayHeight = metadata.DisplayHeight ?? metadata.Height ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var samplePoint = SourceFrameSelector.SamplePoint(clip, playhead.ToFrameFloor(rate), rate, metadata);

        progress?.Report(0);

        var frame = await DecodeFullResolutionFrameAsync(
            asset.FilePath,
            startTime,
            samplePoint,
            SourceFrameSelector.NominalRate(metadata),
            displayWidth,
            displayHeight,
            HdrColor.Classify(asset.Kind, metadata).Conversion,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(0.4);

        var prompt = new PointD(proposal.Centroid.X * frame.Width, proposal.Centroid.Y * frame.Height);

        using var holdFrame = await _segmentation.PrepareFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        var maskSet = await holdFrame.PredictAsync(new[] { prompt }, cancellationToken).ConfigureAwait(false);
        var chosen = HoldCandidateRules.Choose(maskSet);
        if (chosen is not { } k)
        {
            return new HoldProposalRefinementResult(null, Messages.Holds_RefinementFailed);
        }

        progress?.Report(0.7);

        var mask = maskSet.Masks[k];
        var contour = MaskContour.Outer(mask.Pixels, mask.Width, mask.Height);
        if (contour.Length < HoldMarkupRules.MinPoints)
        {
            return new HoldProposalRefinementResult(null, Messages.Holds_RefinementFewPoints);
        }

        if (HoldMarkupRules.ValidateHold(CreateHold(contour, default, default, frame.Timestamp, startTime)) is { } validationError)
        {
            return new HoldProposalRefinementResult(null, string.Format(CultureInfo.InvariantCulture, Messages.Holds_RefinementInvalid, validationError));
        }

        var (surfaceLab, wallLab) = HoldColorSampler.Sample(frame, mask.Pixels, Array.Empty<IReadOnlyList<NormalizedPoint>>());
        var hold = CreateHold(contour, surfaceLab, wallLab, frame.Timestamp, startTime);

        progress?.Report(1.0);

        return new HoldProposalRefinementResult(hold, null);
    }

    public async Task<IReadOnlyList<HoldProposalRefinementResult>> RefineBatchAsync(
        IReadOnlyList<HoldProposal> proposals,
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);

        var metadata = asset.Metadata ?? throw new InvalidOperationException($"{asset.FileName} has no media information yet.");
        var displayWidth = metadata.DisplayWidth ?? metadata.Width ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var displayHeight = metadata.DisplayHeight ?? metadata.Height ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var samplePoint = SourceFrameSelector.SamplePoint(clip, playhead.ToFrameFloor(rate), rate, metadata);

        progress?.Report(0);

        var frame = await DecodeFullResolutionFrameAsync(
            asset.FilePath,
            startTime,
            samplePoint,
            SourceFrameSelector.NominalRate(metadata),
            displayWidth,
            displayHeight,
            HdrColor.Classify(asset.Kind, metadata).Conversion,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(0.3);

        using var holdFrame = await _segmentation.PrepareFrameAsync(frame, cancellationToken).ConfigureAwait(false);

        var results = new List<HoldProposalRefinementResult>(proposals.Count);
        var otherHolds = new List<IReadOnlyList<NormalizedPoint>>();
        for (var i = 0; i < proposals.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = proposals[i];
            var prompt = new PointD(proposal.Centroid.X * frame.Width, proposal.Centroid.Y * frame.Height);
            var maskSet = await holdFrame.PredictAsync(new[] { prompt }, cancellationToken).ConfigureAwait(false);
            var chosen = HoldCandidateRules.Choose(maskSet);
            if (chosen is not { } k)
            {
                results.Add(new HoldProposalRefinementResult(null, Messages.Holds_RefinementFailed));
            }
            else
            {
                var mask = maskSet.Masks[k];
                var contour = MaskContour.Outer(mask.Pixels, mask.Width, mask.Height);
                if (contour.Length < HoldMarkupRules.MinPoints)
                {
                    results.Add(new HoldProposalRefinementResult(null, Messages.Holds_RefinementFewPoints));
                }
                else if (HoldMarkupRules.ValidateHold(CreateHold(contour, default, default, frame.Timestamp, startTime)) is { } validationError)
                {
                    results.Add(new HoldProposalRefinementResult(null, string.Format(CultureInfo.InvariantCulture, Messages.Holds_RefinementInvalid, validationError)));
                }
                else
                {
                    var (surfaceLab, wallLab) = HoldColorSampler.Sample(frame, mask.Pixels, otherHolds);
                    var hold = CreateHold(contour, surfaceLab, wallLab, frame.Timestamp, startTime);
                    results.Add(new HoldProposalRefinementResult(hold, null));
                    otherHolds.Add(hold.Points);   // exclude subsequent wall samples from this hold's area
                }
            }

            progress?.Report(0.3 + 0.7 * (i + 1) / proposals.Count);
        }

        return results;
    }

    private static Hold CreateHold(
        ImmutableArray<NormalizedPoint> contour,
        OkLabColor surfaceLab,
        OkLabColor wallLab,
        SourceTimestamp timestamp,
        MediaTime startTime)
    {
        var sourceTime = SourceTimeOf(timestamp, startTime);
        return new Hold(Guid.NewGuid(), contour, surfaceLab, wallLab, sourceTime);
    }

    /// <summary>The timestamp in 100 ns ticks relative to the file's start, rounded to the nearest tick (exact rational).</summary>
    private static MediaTime SourceTimeOf(SourceTimestamp timestamp, MediaTime startTime)
    {
        var tb = timestamp.TimeBase;
        Int128 numerator = (Int128)timestamp.Pts * tb.Numerator * TimeSpan.TicksPerSecond;
        Int128 denominator = tb.Denominator;
        var ticks = (long)((numerator * 2 + denominator) / (denominator * 2));   // round half up (non-negative)
        return new MediaTime(Math.Max(0, ticks - startTime.Ticks));
    }

    private async Task<DecodedFrame> DecodeFullResolutionFrameAsync(
        string filePath,
        MediaTime startTime,
        SourceSamplePoint samplePoint,
        FrameRate? nominalRate,
        int displayWidth,
        int displayHeight,
        HdrConversion? hdrConversion,
        CancellationToken cancellationToken)
    {
        await using var stream = await _decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = filePath,
            StartTime = startTime,
            FirstSamplePoint = samplePoint,
            NominalFrameRate = nominalRate,
            MaxWidth = displayWidth,
            MaxHeight = displayHeight,
            Hardware = HardwareDecoding.Disabled,
            Hdr = hdrConversion,
            StrictEnd = true
        }, cancellationToken).ConfigureAwait(false);

        var current = await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, string.Format(CultureInfo.InvariantCulture, Messages.Holds_NoFrameProduced, filePath));

        while (await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } next &&
               SourceFrameSelector.IsAtOrBefore(next.Timestamp, startTime, samplePoint))
        {
            current = next;
        }

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
