using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// D071 §2: the Preview's picture quality, chosen on the Preview's bar — the largest picture the Preview decodes, and, below
/// <see cref="Original"/>, a proxy of that size for a heavy source (<see cref="PreviewDecoding.NeedsProxy"/>). The export,
/// the thumbnails and every analysis never use it: they decode the source.
/// </summary>
public enum PreviewQuality
{
    /// <summary>The source file at its own size — no proxy, no bound.</summary>
    Original,

    /// <summary>1440p: 2560 × 1440 (its long side × its short side).</summary>
    High,

    /// <summary>1080p: 1920 × 1080 — the default.</summary>
    Medium,

    /// <summary>720p: 1280 × 720 — the Preview of 1.3 and earlier (then for portrait pictures too, 405 × 720).</summary>
    Low
}

/// <summary>D071 §2–§3: how the Preview decodes a source at a <see cref="PreviewQuality"/>.</summary>
public static class PreviewDecoding
{
    public const PreviewQuality DefaultQuality = PreviewQuality.Medium;

    /// <summary>A source whose long side is larger than this is «4K-class»: a proxy is made for it.</summary>
    public const int ProxyLongSideAbove = 1920;

    /// <summary>The bound of a quality as (long side, short side); null for <see cref="PreviewQuality.Original"/>.</summary>
    public static (int Long, int Short)? Box(PreviewQuality quality) => quality switch
    {
        PreviewQuality.High => (2560, 1440),
        PreviewQuality.Medium => (1920, 1080),
        PreviewQuality.Low => (1280, 720),
        _ => null
    };

    /// <summary>
    /// The largest decoded picture of a source shown <paramref name="displayWidth"/> × <paramref name="displayHeight"/> (its
    /// display orientation) at <paramref name="quality"/>: the quality's box turned like the picture — a portrait picture
    /// gets the box upright, so 720p is 720 × 1280 for it, not 405 × 720. Null: no bound (<see cref="PreviewQuality.Original"/>
    /// or an unknown size — the source's own size).
    /// </summary>
    public static FrameSize? Bound(PreviewQuality quality, int? displayWidth, int? displayHeight)
    {
        if (Box(quality) is not { } box || displayWidth is not > 0 || displayHeight is not > 0) return null;
        return displayHeight > displayWidth ? new FrameSize(box.Short, box.Long) : new FrameSize(box.Long, box.Short);
    }

    /// <summary>
    /// D071 §3 (amendment 2): a proxy is made for a video whose decoding is heavy — its long side above
    /// <see cref="ProxyLongSideAbove"/> («4K») or coded in HEVC —, once, at <see cref="ProxyQuality"/>, whatever quality is chosen.
    /// Offline media, stills and media not analysed yet get none; nor a video whose colour is not known yet (D033 T3: the
    /// background probe may still find HDR — its proxy would have another picture). Judged by that persistent fact, never by
    /// the transient refresh flag (a refresh ends without an event); a colour that changes asks for the proxy again.
    /// </summary>
    public static bool NeedsProxy(MediaAsset asset)
    {
        if (asset.Kind != MediaKind.Video || asset.IsMissing) return false;
        if (asset.AnalysisStatus != MediaAnalysisStatus.Completed || asset.Metadata is not { } m || m.NeedsColorProbe) return false;
        var w = m.DisplayWidth ?? m.Width ?? 0;
        var h = m.DisplayHeight ?? m.Height ?? 0;
        if (w <= 0 || h <= 0) return false;
        return Math.Max(w, h) > ProxyLongSideAbove || IsHevc(m.VideoCodec);
    }

    /// <summary>D071 amendment 2: the one proxy of a file is 1080p («Среднее»).</summary>
    public const PreviewQuality ProxyQuality = PreviewQuality.Medium;

    /// <summary>D071 amendment 2: the qualities the proxy serves — «Среднее» and «Минимум» (the latter scaled down on the fly);
    /// «Высокое» and «Оригинал» decode the source.</summary>
    public static bool UsesProxy(PreviewQuality quality) => quality is PreviewQuality.Medium or PreviewQuality.Low;

    /// <summary>The proxy's picture: the source's display size fitted into <see cref="ProxyQuality"/>'s box (never enlarged),
    /// both sides even (H.264 4:2:0).</summary>
    public static FrameSize ProxySize(int displayWidth, int displayHeight) => ProxySize(ProxyQuality, displayWidth, displayHeight);

    /// <summary>The picture of a proxy at <paramref name="quality"/>'s box (never enlarged), both sides even.</summary>
    public static FrameSize ProxySize(PreviewQuality quality, int displayWidth, int displayHeight)
    {
        var bound = Bound(quality, displayWidth, displayHeight) ?? new FrameSize(displayWidth, displayHeight);
        var scale = Math.Min(1.0, Math.Min(bound.Width / (double)displayWidth, bound.Height / (double)displayHeight));
        return new FrameSize(Even(displayWidth * scale), Even(displayHeight * scale));

        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2);
    }

    private static bool IsHevc(string? codec) =>
        codec is not null && (codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) || codec.Equals("h265", StringComparison.OrdinalIgnoreCase));
}
