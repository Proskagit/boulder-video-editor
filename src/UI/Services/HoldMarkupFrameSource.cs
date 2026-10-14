using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Services;

/// <summary>The markup frame (D034 §1): the clip's source frame under the playhead at its full display size, and its time
/// in the source (relative to the file's start).</summary>
public sealed record HoldMarkupFrame(DecodedFrame Frame, MediaTime SourceTime);

/// <summary>Decodes the markup frame of a video clip.</summary>
public interface IHoldMarkupFrameSource
{
    Task<HoldMarkupFrame> GetAsync(VideoClip clip, MediaAsset asset, MediaTime playhead, FrameRate rate, CancellationToken cancellationToken);
}

/// <summary>
/// The frame the Preview shows for <c>clip</c> at the playhead (D009 / D022: <see cref="SourceFrameSelector"/>), decoded by
/// the existing decoder at the asset's display size (D019: display-oriented) with the Preview's HDR rule (D033) — the
/// picture the user sees, at full resolution. Software decoding: deterministic, no competition with the Preview.
/// </summary>
public sealed class HoldMarkupFrameSource(IVideoDecoder decoder) : IHoldMarkupFrameSource
{
    public async Task<HoldMarkupFrame> GetAsync(VideoClip clip, MediaAsset asset, MediaTime playhead, FrameRate rate, CancellationToken cancellationToken)
    {
        var metadata = asset.Metadata ?? throw new InvalidOperationException($"{asset.FileName} has no media information yet.");
        var width = metadata.DisplayWidth ?? metadata.Width ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var height = metadata.DisplayHeight ?? metadata.Height ?? throw new InvalidOperationException($"The size of {asset.FileName} is unknown.");
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var point = SourceFrameSelector.SamplePoint(clip, playhead.ToFrameFloor(rate), rate, metadata);

        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = asset.FilePath,
            StartTime = startTime,
            FirstSamplePoint = point,
            NominalFrameRate = SourceFrameSelector.NominalRate(metadata),
            MaxWidth = width,
            MaxHeight = height,
            Hardware = HardwareDecoding.Disabled,
            Hdr = HdrColor.Classify(asset.Kind, metadata).Conversion,
            StrictEnd = true
        }, cancellationToken);

        // The frame SourceFrameSelector picks: the last one at or before the point (hold-first / hold-last).
        var current = await stream.ReadFrameAsync(cancellationToken)
                      ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{asset.FilePath}' produced no frame.");
        while (await stream.ReadFrameAsync(cancellationToken) is { } next && SourceFrameSelector.IsAtOrBefore(next.Timestamp, startTime, point))
            current = next;

        return new HoldMarkupFrame(Copy(current), SourceTimeOf(current.Timestamp, startTime));
    }

    /// <summary>The frame's own copy (decoders may reuse their buffers).</summary>
    private static DecodedFrame Copy(DecodedFrame frame)
    {
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        var pixels = new byte[rowBytes * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
            source.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        return new DecodedFrame(frame.Width, frame.Height, rowBytes, pixels, frame.Timestamp);
    }

    /// <summary>The timestamp in 100 ns ticks relative to the file's start, rounded to the nearest tick (exact rational).</summary>
    public static MediaTime SourceTimeOf(SourceTimestamp timestamp, MediaTime startTime)
    {
        var tb = timestamp.TimeBase;
        Int128 numerator = (Int128)timestamp.Pts * tb.Numerator * TimeSpan.TicksPerSecond;
        Int128 denominator = tb.Denominator;
        var ticks = (long)((numerator * 2 + denominator) / (denominator * 2));   // round half up (non-negative)
        return new MediaTime(Math.Max(0, ticks - startTime.Ticks));
    }
}
