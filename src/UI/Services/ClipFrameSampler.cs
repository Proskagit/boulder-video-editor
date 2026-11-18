using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Services;

/// <summary>D069 §7: a few small frames of a video clip, evenly through it — what «Автоулучшение» measures.</summary>
public interface IClipFrameSampler
{
    /// <summary>Up to <paramref name="count"/> BGRA frames of <paramref name="clip"/> (at most <paramref name="maxWidth"/> wide), as
    /// the Preview shows them (the HDR rule included); a frame that cannot be decoded is left out.</summary>
    Task<IReadOnlyList<DecodedFrame>> SampleAsync(VideoClip clip, MediaAsset asset, FrameRate rate, int count, int maxWidth,
        CancellationToken ct);
}

/// <summary>Decodes each sample with the existing decoder (software, reduced size), one short decode per frame.</summary>
public sealed class ClipFrameSampler(IVideoDecoder decoder) : IClipFrameSampler
{
    public async Task<IReadOnlyList<DecodedFrame>> SampleAsync(VideoClip clip, MediaAsset asset, FrameRate rate, int count, int maxWidth,
        CancellationToken ct)
    {
        var metadata = asset.Metadata;
        var width = metadata?.DisplayWidth ?? metadata?.Width ?? maxWidth;
        var height = metadata?.DisplayHeight ?? metadata?.Height ?? maxWidth * 9 / 16;
        var scale = Math.Min(1.0, (double)maxWidth / Math.Max(1, width));
        var startTime = metadata?.StartTime ?? MediaTime.Zero;
        long first = clip.TimelineStart.ToFrameCeiling(rate), last = Math.Max(first, clip.TimelineEnd.ToFrameCeiling(rate) - 1);
        var frames = new List<DecodedFrame>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var frame = first + (long)Math.Floor((last - first) * (i + 0.5) / count);
            var point = SourceFrameSelector.SamplePoint(clip, frame, rate, metadata);
            try
            {
                await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
                {
                    FilePath = asset.FilePath,
                    StartTime = startTime,
                    FirstSamplePoint = point,
                    NominalFrameRate = SourceFrameSelector.NominalRate(metadata),
                    MaxWidth = Math.Max(16, (int)(width * scale)),
                    MaxHeight = Math.Max(16, (int)(height * scale)),
                    Hardware = HardwareDecoding.Disabled,
                    Hdr = HdrColor.Classify(asset.Kind, metadata).Conversion,
                    StrictEnd = true
                }, ct);
                if (await stream.ReadFrameAsync(ct) is { } decoded) frames.Add(Copy(decoded));
            }
            catch (VideoDecodeException)
            {
                // A sample that cannot be decoded is left out; the others still tell the picture.
            }
        }
        return frames;
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
