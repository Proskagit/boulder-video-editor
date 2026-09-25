using System.Buffers.Binary;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Media.Thumbnails;

/// <summary>
/// The thumbnail cache file (D024 Step 9.4): a 16-byte header — magic <c>AIVT</c>, format version, width, height, each
/// a little-endian 32-bit value after the magic — followed by exactly width × height BGRA pixels (rows packed,
/// straight alpha). Anything else is not a thumbnail: <see cref="TryRead"/> returns null and the caller treats it
/// as a cache miss.
/// </summary>
internal static class ThumbnailCacheFile
{
    public const int FormatVersion = 1;
    public const int HeaderLength = 16;
    /// <summary>Larger than any thumbnail this service makes; bounds what a damaged header can make us allocate.</summary>
    public const int MaxSide = 4096;

    private static ReadOnlySpan<byte> Magic => "AIVT"u8;

    public static byte[] Encode(Thumbnail thumbnail)
    {
        var bytes = new byte[HeaderLength + thumbnail.Pixels.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), thumbnail.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), thumbnail.Height);
        thumbnail.Pixels.Span.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    /// <summary>The thumbnail in <paramref name="path"/>, or null when the file is missing, unreadable or not a
    /// complete thumbnail of this format.</summary>
    public static Thumbnail? TryRead(string path)
    {
        byte[] bytes;
        try
        {
            var length = new FileInfo(path).Length;
            if (length < HeaderLength || length > HeaderLength + (long)MaxSide * MaxSide * Thumbnail.BytesPerPixel)
                return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length < HeaderLength || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (version != FormatVersion || width is <= 0 or > MaxSide || height is <= 0 or > MaxSide)
            return null;
        if (bytes.Length - HeaderLength != (long)width * height * Thumbnail.BytesPerPixel)
            return null;
        return new Thumbnail(width, height, bytes.AsMemory(HeaderLength));
    }
}
