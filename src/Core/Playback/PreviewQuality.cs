using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// D071 §2: the Preview's picture quality, chosen on the Preview's bar — the largest picture the Preview decodes, applied on the
/// fly (amendment 3: no proxies). The export, the thumbnails and every analysis never use it: they decode the source at their own
/// sizes.
/// </summary>
public enum PreviewQuality
{
    /// <summary>The source file at its own size — no bound; the default (D072 §0, the owner: «Оригинал» by default).</summary>
    Original,

    /// <summary>1440p: 2560 × 1440 (its long side × its short side).</summary>
    High,

    /// <summary>1080p: 1920 × 1080 (the default of D071, until D072 §0).</summary>
    Medium,

    /// <summary>720p: 1280 × 720 — the Preview of 1.3 and earlier (then for portrait pictures too, 405 × 720).</summary>
    Low
}

/// <summary>D071 §2: how large the Preview decodes a source at a <see cref="PreviewQuality"/>.</summary>
public static class PreviewDecoding
{
    public const PreviewQuality DefaultQuality = PreviewQuality.Original;

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
}
