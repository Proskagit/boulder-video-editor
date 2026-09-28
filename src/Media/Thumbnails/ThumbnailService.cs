using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Thumbnails;

/// <summary>
/// Media Browser thumbnails (D024 Step 9.4), cached in a folder the caller chooses.
/// <list type="bullet">
/// <item>Picture: the frame D009 selects at source time <c>T = min(⌊Duration / 10⌋, 5 s)</c> (ticks, from the file's
/// start time): the last decoded frame whose source time is at or before T; the stream's first frame if every frame
/// starts later, its last frame if T lies after the end. Decoded by the app's <see cref="IVideoDecoder"/> — software,
/// scaled to fit <see cref="MaxWidth"/> × <see cref="MaxHeight"/> with its aspect ratio, upright as the decoder
/// delivers it — and selected by <see cref="SourceFrameSelector"/>, never by the decoder. Images are their one frame.</item>
/// <item>Cache: one file per asset, named from the asset id, the source file's current size and last-write time
/// (UTC ticks) and <see cref="RuleVersion"/>; any other name — the file changed, or the rules — is a miss. A
/// missing, damaged or unreadable file is a miss too, and the thumbnail is made again. Files are written to a
/// temporary name and moved into place; older files of the asset are removed once the new one is there.</item>
/// <item>Offline media (marked missing, or the file is gone now) is never decoded: the last cached thumbnail is
/// shown as it is, or none.</item>
/// </list>
/// No ffmpeg specifics here: decoding is the <see cref="IVideoDecoder"/>'s. No concurrency limit either — the caller
/// (the Media Browser's thumbnail queue) owns scheduling.
/// </summary>
public sealed class ThumbnailService : IThumbnailService
{
    /// <summary>Version of the thumbnail rules (source time, size bound, file format). Changing any rule changes it,
    /// which makes every cached thumbnail a miss.</summary>
    public const int CurrentRuleVersion = 1;

    public const int DefaultMaxWidth = 160;
    public const int DefaultMaxHeight = 90;

    private const string Extension = ".thumb";
    private static readonly long MaxSourceTimeTicks = 5 * TimeSpan.TicksPerSecond;

    private readonly IVideoDecoder _decoder;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(IVideoDecoder decoder, ILogger<ThumbnailService> logger)
    {
        _decoder = decoder;
        _logger = logger;
    }

    /// <summary>Rule version in cache file names (tests set another one to simulate a rule change).</summary>
    internal int RuleVersion { get; init; } = CurrentRuleVersion;

    public int MaxWidth { get; init; } = DefaultMaxWidth;
    public int MaxHeight { get; init; } = DefaultMaxHeight;

    /// <summary>The source time of the thumbnail: <c>min(⌊Duration / 10⌋, 5 s)</c> in ticks from the file's start time
    /// (zero when the duration is unknown).</summary>
    public static MediaTime SourceTime(MediaMetadata? metadata)
    {
        var duration = metadata?.Duration.Ticks ?? 0;
        return new MediaTime(Math.Min(Math.Max(duration, 0) / 10, MaxSourceTimeTicks));
    }

    public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder)
    {
        if (SourceFileCache.Identity(asset) is { } identity)
            return ThumbnailCacheFile.TryRead(Path.Combine(cacheFolder, FileName(asset, identity)));

        // Offline: the last thumbnail cached for the asset, whatever file (or rule version) it was made from.
        return SourceFileCache.LastCached(asset, cacheFolder, Extension, ThumbnailCacheFile.TryRead);
    }

    public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
    {
        if (SourceFileCache.Identity(asset) is not { } identity)
            return TryGetCached(asset, cacheFolder); // offline: never decoded
        if (!HasPicture(asset))
            return null;

        var path = Path.Combine(cacheFolder, FileName(asset, identity));
        if (ThumbnailCacheFile.TryRead(path) is { } cached)
            return cached;

        Thumbnail thumbnail;
        try
        {
            thumbnail = await DecodeAsync(asset, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No thumbnail for '{Path}': it could not be decoded.", asset.FilePath);
            return null;
        }

        Write(asset, cacheFolder, path, thumbnail);
        return thumbnail;
    }

    /// <summary>Video and images with completed analysis have a picture; audio and unanalysed media don't.</summary>
    private static bool HasPicture(MediaAsset asset) =>
        asset.Kind is MediaKind.Video or MediaKind.Image &&
        asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is not null;

    private async Task<Thumbnail> DecodeAsync(MediaAsset asset, CancellationToken ct)
    {
        var metadata = asset.Metadata!;
        var startTime = metadata.StartTime ?? MediaTime.Zero;
        var point = new SourceSamplePoint(SourceTime(metadata).Ticks, 1);

        await using var stream = await _decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = asset.FilePath,
            StartTime = startTime,
            FirstSamplePoint = point,
            NominalFrameRate = SourceFrameSelector.NominalRate(metadata),
            MaxWidth = MaxWidth,
            MaxHeight = MaxHeight,
            Hardware = HardwareDecoding.Disabled, // deterministic, and no competition with the Preview's decoders
            StrictEnd = true                      // a decoder that fails mid-stream leaves the choice uncertain
        }, ct);

        // The decoder's first frame is at or before the point, or the stream's first frame (hold-first). Advance while
        // the next frame is at or before the point; at the end of the stream the last frame stays (hold-last) — the
        // frame SourceFrameSelector.Select picks.
        var current = await stream.ReadFrameAsync(ct)
                      ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{asset.FilePath}' produced no frame.");
        while (await stream.ReadFrameAsync(ct) is { } next && SourceFrameSelector.IsAtOrBefore(next.Timestamp, startTime, point))
            current = next;

        return Pack(current);
    }

    private static Thumbnail Pack(DecodedFrame frame)
    {
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        var pixels = new byte[rowBytes * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
            source.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        return new Thumbnail(frame.Width, frame.Height, pixels);
    }

    /// <summary>Writes atomically (temporary file, then a move); a failed write only costs the cache — the thumbnail
    /// is still returned. Older files of the asset are removed once the new one is in place.</summary>
    private void Write(MediaAsset asset, string cacheFolder, string path, Thumbnail thumbnail)
    {
        try
        {
            SourceFileCache.Write(asset, cacheFolder, path, Extension, ThumbnailCacheFile.Encode(thumbnail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The thumbnail of '{Path}' could not be cached in '{Folder}'.", asset.FilePath, cacheFolder);
        }
    }

    private string FileName(MediaAsset asset, (long Size, long LastWriteTicks) identity) =>
        SourceFileCache.FileName(asset, identity, RuleVersion, Extension);
}
