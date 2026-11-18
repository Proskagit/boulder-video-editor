using System.Buffers.Binary;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.Media.Thumbnails;

/// <summary>
/// D068 item 10: the filmstrips of the timeline's video clips. A video gets <see cref="FrameCount"/> pictures — one per two
/// seconds, 2 to <see cref="MaxFrames"/> — at the middles of equal parts of its length, each the frame the thumbnail's rule
/// (<see cref="ThumbnailService"/>) selects there, small (<see cref="FrameWidth"/> × <see cref="FrameHeight"/>), software
/// decoded. Cached like the thumbnails: one file per asset (<c>.strip</c>: magic <c>BVSF</c>, version, count, then per frame
/// its source ticks, its length and a thumbnail cache file), named from the source file's identity; offline media is never
/// decoded.
/// </summary>
public sealed class FilmstripService : IFilmstripService
{
    public const int RuleVersion = 1;
    public const int MaxFrames = 16;
    public const int FrameWidth = 96;
    public const int FrameHeight = 54;
    private const string Extension = ".strip";
    private static ReadOnlySpan<byte> Magic => "BVSF"u8;

    private readonly ThumbnailService _frames;
    private readonly ILogger<FilmstripService> _logger;

    public FilmstripService(IVideoDecoder decoder, ILogger<FilmstripService> logger)
    {
        _frames = new ThumbnailService(decoder, NullLogger<ThumbnailService>.Instance) { MaxWidth = FrameWidth, MaxHeight = FrameHeight };
        _logger = logger;
    }

    /// <summary>How many pictures a video of <paramref name="duration"/> gets: one per two seconds, 2 … <see cref="MaxFrames"/>.</summary>
    public static int FrameCount(MediaTime duration) =>
        (int)Math.Clamp(duration.Ticks / (2 * TimeSpan.TicksPerSecond), 2, MaxFrames);

    /// <summary>The source times of the pictures: the middles of <see cref="FrameCount"/> equal parts of the video.</summary>
    public static IReadOnlyList<MediaTime> SourceTimes(MediaTime duration)
    {
        var count = FrameCount(duration);
        var length = Math.Max(duration.Ticks, 0);
        return Enumerable.Range(0, count).Select(i => new MediaTime(length * (2 * i + 1) / (2 * count))).ToList();
    }

    public Filmstrip? TryGetCached(MediaAsset asset, string cacheFolder)
    {
        if (asset.Kind != MediaKind.Video) return null;
        if (SourceFileCache.Identity(asset) is { } identity)
            return TryRead(Path.Combine(cacheFolder, FileName(asset, identity)));
        return SourceFileCache.LastCached(asset, cacheFolder, Extension, TryRead);
    }

    public async Task<Filmstrip?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
    {
        if (asset.Kind != MediaKind.Video) return null;
        if (SourceFileCache.Identity(asset) is not { } identity)
            return TryGetCached(asset, cacheFolder);           // offline: never decoded
        if (!ThumbnailService.HasPicture(asset) || asset.Metadata?.Duration is not { } duration)
            return null;

        var path = Path.Combine(cacheFolder, FileName(asset, identity));
        if (TryRead(path) is { } cached)
            return cached;

        var frames = new List<FilmstripFrame>();
        try
        {
            foreach (var time in SourceTimes(duration))
                frames.Add(new FilmstripFrame(time, await _frames.DecodeAtAsync(asset, time, ct)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No filmstrip for '{Path}': it could not be decoded.", asset.FilePath);
            return null;
        }

        var filmstrip = new Filmstrip(frames);
        try
        {
            SourceFileCache.Write(asset, cacheFolder, path, Extension, Encode(filmstrip));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The filmstrip of '{Path}' could not be cached in '{Folder}'.", asset.FilePath, cacheFolder);
        }
        return filmstrip;
    }

    private static string FileName(MediaAsset asset, (long Size, long LastWriteTicks) identity) =>
        SourceFileCache.FileName(asset, identity, RuleVersion, Extension);

    internal static byte[] Encode(Filmstrip filmstrip)
    {
        var parts = filmstrip.Frames.Select(f => (f.SourceTime.Ticks, Bytes: ThumbnailCacheFile.Encode(f.Picture))).ToList();
        var bytes = new byte[12 + parts.Sum(p => 12 + p.Bytes.Length)];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), RuleVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), parts.Count);
        var at = 12;
        foreach (var (ticks, frame) in parts)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), ticks);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 8), frame.Length);
            frame.CopyTo(bytes.AsSpan(at + 12));
            at += 12 + frame.Length;
        }
        return bytes;
    }

    /// <summary>The filmstrip in <paramref name="path"/>, or null when missing, unreadable or not a complete one.</summary>
    internal static Filmstrip? TryRead(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != RuleVersion) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count is < 1 or > MaxFrames) return null;

        var frames = new List<FilmstripFrame>(count);
        var at = 12;
        for (var i = 0; i < count; i++)
        {
            if (at + 12 > bytes.Length) return null;
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 8));
            if (length < ThumbnailCacheFile.HeaderLength || (long)at + 12 + length > bytes.Length) return null;
            if (ThumbnailCacheFile.Decode(bytes.AsMemory(at + 12, length)) is not { } picture) return null;
            frames.Add(new FilmstripFrame(new MediaTime(ticks), picture));
            at += 12 + length;
        }
        return at == bytes.Length ? new Filmstrip(frames) : null;
    }
}
