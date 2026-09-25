using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>A Media Browser thumbnail: <see cref="Width"/> × <see cref="Height"/> BGRA pixels, rows packed
/// (stride = width · 4), straight alpha — the layout of a decoded frame (<c>DecodedFrame</c>).</summary>
public sealed class Thumbnail
{
    public const int BytesPerPixel = 4;

    public Thumbnail(int width, int height, ReadOnlyMemory<byte> pixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (pixels.Length != (long)width * height * BytesPerPixel)
            throw new ArgumentException("The pixel buffer must hold exactly width × height BGRA pixels.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public int Stride => Width * BytesPerPixel;
}

/// <summary>
/// Thumbnails of media assets, kept in a cache folder the caller chooses (D024 Step 9.4). The picture is the source
/// frame the D009 rule selects at a fixed source time; a cached thumbnail is current while the source file has the
/// same size and last-write time and the thumbnail rules are unchanged. Playback and export never use thumbnails.
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// A cached thumbnail, without decoding anything: for media whose file is there, only one that matches the file
    /// as it is now; for offline media (marked missing, or the file is gone) the last one cached, without looking at
    /// the source. Null when there is none (or it is unreadable).
    /// </summary>
    Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder);

    /// <summary>
    /// The current thumbnail: a cache hit, or on a miss the frame decoded, written to the cache and returned. Null
    /// when the asset has no thumbnail to make — audio, not analysed, offline (never decoded), or the source could
    /// not be decoded. Cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default);
}
