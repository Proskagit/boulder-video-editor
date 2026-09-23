using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// One decoded video frame, independent of the decoder backend and of the UI toolkit:
/// 32-bit BGRA pixels (8 bits per channel, byte order B, G, R, A; rows top to bottom,
/// <see cref="Stride"/> bytes apart) plus the frame's exact source timestamp.
/// Immutable: the pixel buffer is owned by the frame and exposed read-only.
/// </summary>
public sealed class DecodedFrame
{
    public const int BytesPerPixel = 4;

    public DecodedFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, SourceTimestamp timestamp)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * BytesPerPixel) throw new ArgumentOutOfRangeException(nameof(stride));
        if (pixels.Length < (long)stride * height) throw new ArgumentException("Pixel buffer is smaller than stride × height.", nameof(pixels));
        if (!timestamp.TimeBase.IsValid) throw new ArgumentException("Timestamp has no valid time base.", nameof(timestamp));

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        Timestamp = timestamp;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes between the starts of consecutive rows.</summary>
    public int Stride { get; }

    /// <summary>BGRA pixel data, at least <c>Stride · Height</c> bytes.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>Presentation timestamp exactly as the source stream reports it.</summary>
    public SourceTimestamp Timestamp { get; }
}
