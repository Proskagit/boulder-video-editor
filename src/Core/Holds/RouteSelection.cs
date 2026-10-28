using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>The two moments of a route selection at <c>T</c> (D048 §2), in the clip's source time: the frame at <c>T</c>
/// and the clean frame at <c>T − 5 s</c> (timeline time). <see cref="CleanAtFileStart"/>: <c>T − 5 s</c> mapped before the
/// file's start, so the file's first frame is used.</summary>
public readonly record struct RouteSelectionFrames(MediaTime TargetSourceTime, MediaTime CleanSourceTime, bool CleanAtFileStart);

/// <summary>
/// D048 §2, the clean-frame locator: from <c>T</c> (a timeline frame inside a video clip) the source time of <c>T</c> and of
/// <c>T − 5 s</c>, through the clip's timing (D009 / D022: <c>TimelineStart</c>, <c>SourceIn</c>, the exact speed) by the
/// same rule as the hold animation (<see cref="HoldAnimationRule.SourceTimeAt"/>, exact in ticks). Before the clip's start
/// the mapping continues into the file's frames before <c>SourceIn</c> (the handle); before the file's start, the file's
/// first frame. Never another frame silently: the transfer checks whatever it returns.
/// </summary>
public static class RouteSelectionLocator
{
    /// <summary>How far before <c>T</c> the clean frame is, in timeline time (D048 §1.3).</summary>
    public static readonly MediaTime CleanOffset = MediaTime.FromSeconds(5);

    /// <summary>The frames for <paramref name="tFrame"/>, or null when it is not a frame of <paramref name="clip"/>
    /// (<c>Vs ≤ T &lt; Ve</c>; the route selection's own rule, unlike the layout's <c>T ≤ Ve</c>).</summary>
    public static RouteSelectionFrames? Locate(VideoClip clip, long tFrame, FrameRate rate)
    {
        long vs = clip.TimelineStart.ToNearestFrame(rate), ve = clip.TimelineEnd.ToNearestFrame(rate);
        if (tFrame < vs || tFrame >= ve) return null;
        var target = HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, tFrame, rate);
        var clean = HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed,
            tFrame - CleanOffset.ToNearestFrame(rate), rate);
        return clean < MediaTime.Zero
            ? new RouteSelectionFrames(target, MediaTime.Zero, true)
            : new RouteSelectionFrames(target, clean, false);
    }
}

/// <summary>The control hold's point carried from <c>T</c> to the clean frame, or the reason it could not be.</summary>
public readonly record struct RouteSelectionTransferResult(NormalizedPoint? Point, string? Error)
{
    public bool Succeeded => Point is not null;
}

/// <summary>
/// D048 §2, the control-point transfer: the camera's motion between the clean frame (the reference) and the frame at
/// <c>T</c> by <see cref="CameraMotionEstimator"/> (its reliability rule: D-15's working thresholds, 3 / 6 px at the 1920
/// scale), and the click at <c>T</c> mapped back by the inverse transform. Unreliable, singular or off the frame →
/// <see cref="Messages.RouteSelectionTransferFailed"/>, nothing created.
/// </summary>
public static class RouteSelectionTransfer
{
    public static RouteSelectionTransferResult Transfer(GrayImage clean, MediaTime cleanTime, GrayImage atT, MediaTime tTime, NormalizedPoint click)
    {
        if (tTime == cleanTime) return new RouteSelectionTransferResult(click, null);
        var estimator = new CameraMotionEstimator(clean, cleanTime);
        estimator.Add(atT, tTime);
        var sample = estimator.Build().Samples.Single(s => s.SourceTime == tTime);
        if (!sample.IsReliable) return Failed();
        var inverse = sample.ReferenceToFrame.Inverse();
        if (!inverse.IsFinite) return Failed();
        var (x, y) = inverse.Apply(click.X, click.Y);
        return double.IsFinite(x) && double.IsFinite(y) && x is >= 0 and <= 1 && y is >= 0 and <= 1
            ? new RouteSelectionTransferResult(new NormalizedPoint(x, y), null)
            : Failed();
    }

    private static RouteSelectionTransferResult Failed() => new(null, Messages.RouteSelectionTransferFailed);
}

/// <summary>A decoded frame of a route selection and its time in the source (relative to the file's start).</summary>
public sealed record RouteSelectionFrame(DecodedFrame Frame, MediaTime SourceTime);

/// <summary>Decodes one source frame of an asset by its source time (D048 §2: the frames at <c>t_T</c> and <c>t_C</c>,
/// the latter possibly in the handle before the clip's <c>SourceIn</c>).</summary>
public interface IRouteSelectionFrameSource
{
    /// <summary>The last frame at or before <paramref name="sourceTime"/>, fitted inside
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/>.</summary>
    /// <exception cref="VideoDecodeException">The frame could not be decoded.</exception>
    Task<RouteSelectionFrame> DecodeAsync(MediaAsset asset, MediaTime sourceTime, int maxWidth, int maxHeight, CancellationToken cancellationToken);
}

/// <summary>
/// The existing decoder (software, deterministic, no competition with the Preview) by source time — the same frame choice
/// as the tracking (<c>CameraMotionCoordinator</c>: the last frame at or before the time) and the asset's HDR rule
/// (D033, <see cref="HdrColor"/>): an HDR file tone-mapped to the SDR picture the user sees, an SDR one unchanged. The
/// frame is copied (decoders may reuse their buffers).
/// </summary>
public sealed class RouteSelectionFrameSource(IVideoDecoder decoder) : IRouteSelectionFrameSource
{
    public async Task<RouteSelectionFrame> DecodeAsync(MediaAsset asset, MediaTime sourceTime, int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var metadata = asset.Metadata ?? throw new InvalidOperationException(Messages.RouteSelectionMediaUnavailable);
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var point = new SourceSamplePoint(sourceTime.Ticks, 1);
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = asset.FilePath,
            StartTime = startTime,
            FirstSamplePoint = point,
            NominalFrameRate = SourceFrameSelector.NominalRate(metadata),
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Hardware = HardwareDecoding.Disabled,
            Hdr = HdrColor.Classify(asset.Kind, metadata).Conversion,
            StrictEnd = true
        }, cancellationToken).ConfigureAwait(false);

        var current = await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{asset.FilePath}' produced no frame.");
        current = Copy(current);
        while (await stream.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } next &&
               SourceFrameSelector.IsAtOrBefore(next.Timestamp, startTime, point))
            current = Copy(next);
        return new RouteSelectionFrame(current, CameraMotionMath.SourceTimeOf(current.Timestamp, startTime));
    }

    private static DecodedFrame Copy(DecodedFrame frame)
    {
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        var pixels = new byte[rowBytes * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
            source.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        return new DecodedFrame(frame.Width, frame.Height, rowBytes, pixels, frame.Timestamp);
    }
}

/// <summary>The clean frame ready for the search: the frames located, the clean frame at the D047 proposal size and the
/// control point carried onto it.</summary>
public sealed record RouteSelectionPrepared(RouteSelectionFrames Frames, RouteSelectionFrame Clean, NormalizedPoint ControlPoint);

/// <summary>A preparation, or the message why there is none (nothing created either way).</summary>
public readonly record struct RouteSelectionPreparation(RouteSelectionPrepared? Prepared, string? Error);

/// <summary>
/// D048 §2, from the click at <c>T</c> to the clean frame: locate <c>t_T</c> / <c>t_C</c>, decode both at the tracking's
/// analysis size (<see cref="AnalysisWidth"/>, the HDR rule), carry the click (<see cref="RouteSelectionTransfer"/>), decode
/// the clean frame for the model and the colour samples at the D047 proposal size (the display size fitted inside
/// <see cref="HoldProposalGenerator.DefaultMaxWidth"/> × <see cref="HoldProposalGenerator.DefaultMaxHeight"/>: a 4K frame
/// would cost megabytes per candidate mask; the contours are normalized either way). Every failure is a message — a
/// missing file, a frame that can't be decoded, an unreliable transfer; nothing is changed by this class at all.
/// </summary>
public sealed class RouteSelectionService(IRouteSelectionFrameSource frames)
{
    /// <summary>The tracking's analysis width (<c>CameraMotionCoordinator.AnalysisWidth</c>): the D-15 thresholds hold.</summary>
    public const int AnalysisWidth = 960;

    public async Task<RouteSelectionPreparation> PrepareAsync(VideoClip clip, MediaAsset asset, long tFrame, FrameRate rate,
        NormalizedPoint click, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.IsMissing || asset.Metadata is not { } metadata) return Fail(Messages.RouteSelectionMediaUnavailable);
        var width = metadata.DisplayWidth ?? metadata.Width;
        var height = metadata.DisplayHeight ?? metadata.Height;
        if (width is not > 0 || height is not > 0) return Fail(Messages.RouteSelectionMediaUnavailable);
        if (RouteSelectionLocator.Locate(clip, tFrame, rate) is not { } located) return Fail(Messages.RouteSelectionOutsideClip);

        try
        {
            var atT = await frames.DecodeAsync(asset, located.TargetSourceTime, AnalysisWidth, AnalysisWidth, cancellationToken).ConfigureAwait(false);
            var clean = await frames.DecodeAsync(asset, located.CleanSourceTime, AnalysisWidth, AnalysisWidth, cancellationToken).ConfigureAwait(false);
            var transfer = await Task.Run(() => RouteSelectionTransfer.Transfer(GrayImage.FromBgra(clean.Frame), clean.SourceTime,
                GrayImage.FromBgra(atT.Frame), atT.SourceTime, click), cancellationToken).ConfigureAwait(false);
            if (!transfer.Succeeded) return Fail(transfer.Error!);
            var full = await frames.DecodeAsync(asset, located.CleanSourceTime, Math.Min(width.Value, HoldProposalGenerator.DefaultMaxWidth),
                Math.Min(height.Value, HoldProposalGenerator.DefaultMaxHeight), cancellationToken).ConfigureAwait(false);
            return new RouteSelectionPreparation(new RouteSelectionPrepared(located, full, transfer.Point!.Value), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is VideoDecodeException or InvalidOperationException or IOException)
        {
            return Fail(string.Format(Messages.RouteSelectionFrameUnavailable, ex.Message));
        }
    }

    private static RouteSelectionPreparation Fail(string message) => new(null, message);
}
