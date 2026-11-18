using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>One picture of a <see cref="Filmstrip"/>: the source frame the D009 rule selects at <see cref="SourceTime"/>.</summary>
public sealed record FilmstripFrame(MediaTime SourceTime, Thumbnail Picture);

/// <summary>D068 item 10: pictures of a video spread over its whole length, ordered by time — what the timeline's clips show
/// (each place of a clip shows the picture nearest to its source time).</summary>
public sealed class Filmstrip
{
    public Filmstrip(IReadOnlyList<FilmstripFrame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("A filmstrip has at least one frame.", nameof(frames));
        Frames = frames;
    }

    public IReadOnlyList<FilmstripFrame> Frames { get; }

    /// <summary>The frame whose source time is nearest to <paramref name="sourceTime"/>.</summary>
    public FilmstripFrame Nearest(MediaTime sourceTime)
    {
        var best = Frames[0];
        var bestDistance = Math.Abs((best.SourceTime - sourceTime).Ticks);
        for (var i = 1; i < Frames.Count; i++)
        {
            var distance = Math.Abs((Frames[i].SourceTime - sourceTime).Ticks);
            if (distance >= bestDistance) continue;
            best = Frames[i];
            bestDistance = distance;
        }
        return best;
    }
}

/// <summary>
/// D068 item 10: filmstrips of video assets, kept in a cache folder the caller chooses — the same rules as
/// <see cref="IThumbnailService"/> (a cached one is current while the file has the same size and last-write time; offline
/// media only ever gets its last cached one). Playback and export never use them.
/// </summary>
public interface IFilmstripService
{
    /// <summary>A cached filmstrip, without decoding anything (see <see cref="IThumbnailService.TryGetCached"/>).</summary>
    Filmstrip? TryGetCached(MediaAsset asset, string cacheFolder);

    /// <summary>The current filmstrip: cached, or decoded, cached and returned. Null for anything but an analysed video that
    /// can be decoded. Cancellation throws <see cref="OperationCanceledException"/>.</summary>
    Task<Filmstrip?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default);
}
