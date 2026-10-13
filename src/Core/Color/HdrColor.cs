using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Color;

/// <summary>How an asset's picture is decoded (D033 §3).</summary>
public enum HdrKind
{
    /// <summary>Not HDR as far as its metadata says (SDR, untagged, unknown, or not a video): the existing SDR path.</summary>
    Sdr,

    /// <summary>HLG or PQ with BT.2020 primaries, BT.2020 NCL matrix and at least 10 bits: converted by the rule.</summary>
    Hdr,

    /// <summary>The metadata states an HDR transfer (HLG or PQ) but another combination: the SDR path, named in the
    /// Inspector and warned about by the export preflight (D033 OQ1).</summary>
    UnsupportedHdr
}

/// <summary>An asset's HDR classification: the conversion for <see cref="HdrKind.Hdr"/>, the reason for
/// <see cref="HdrKind.UnsupportedHdr"/>.</summary>
public sealed record HdrClassification(HdrKind Kind, HdrConversion? Conversion = null, string? Reason = null)
{
    public static readonly HdrClassification Sdr = new(HdrKind.Sdr);
}

/// <summary>
/// Which assets take the HDR path (D033 §3 "Which assets") and with what peak. Only what the metadata states counts:
/// missing metadata never makes a file HDR (D033 OQ1).
/// </summary>
public static class HdrColor
{
    public const string HlgTransfer = "arib-std-b67";
    public const string PqTransfer = "smpte2084";
    public const string Bt2020Primaries = "bt2020";
    public const string Bt2020NclMatrix = "bt2020nc";

    /// <summary>The fallback PQ peak when the file has neither MaxCLL nor a mastering maximum (D033 §3).</summary>
    public const int PqFallbackPeakNits = 1000;

    public static HdrClassification Classify(MediaKind kind, MediaMetadata? metadata)
    {
        if (kind != MediaKind.Video || metadata is null)
            return HdrClassification.Sdr;

        HdrTransfer transfer;
        if (Is(metadata.ColorTransfer, HlgTransfer)) transfer = HdrTransfer.Hlg;
        else if (Is(metadata.ColorTransfer, PqTransfer)) transfer = HdrTransfer.Pq;
        else return HdrClassification.Sdr;

        var name = transfer == HdrTransfer.Hlg ? "HLG" : "PQ";
        if (!Is(metadata.ColorPrimaries, Bt2020Primaries))
            return Unsupported($"{name} with {Describe(metadata.ColorPrimaries)} primaries (only BT.2020 is supported)");
        if (!Is(metadata.ColorMatrix, Bt2020NclMatrix))
            return Unsupported($"{name} with the {Describe(metadata.ColorMatrix)} matrix (only BT.2020 non-constant luminance is supported)");
        if (metadata.BitDepth is not >= 10)
            return Unsupported(metadata.BitDepth is { } bits
                ? $"{name} at {bits} bits (at least 10 are needed)"
                : $"{name} with an unknown bit depth");

        var fullRange = Is(metadata.ColorRange, "pc");
        var peak = transfer == HdrTransfer.Hlg ? HdrToSdr.HlgPeakNits : PqPeakNits(metadata);
        return new HdrClassification(HdrKind.Hdr, new HdrConversion(transfer, peak, fullRange));

        static HdrClassification Unsupported(string reason) => new(HdrKind.UnsupportedHdr, Reason: reason);
    }

    /// <summary>
    /// The PQ peak in whole nit (D033 §3): MaxCLL when above 0, else the mastering display's maximum when above 0, else
    /// <see cref="PqFallbackPeakNits"/>; limited to [203, 10 000].
    /// </summary>
    public static int PqPeakNits(MediaMetadata metadata)
    {
        double peak = metadata.MaxContentLightLevel > 0 ? metadata.MaxContentLightLevel.Value
            : metadata.MasteringMaxLuminance > 0 ? metadata.MasteringMaxLuminance.Value
            : PqFallbackPeakNits;
        return (int)Math.Round(Math.Clamp(peak, HdrToSdr.ReferenceWhiteNits, HdrToSdr.PqMaxNits), MidpointRounding.AwayFromZero);
    }

    /// <summary>A short text for the Inspector, e.g. "HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8 (not applied)"; null
    /// when the metadata carries no colour information at all.</summary>
    public static string? Describe(MediaKind kind, MediaMetadata? metadata)
    {
        if (metadata is null || kind != MediaKind.Video || metadata.PixelFormat is null)
            return null;
        var classification = Classify(kind, metadata);
        var parts = new List<string>();
        switch (classification.Kind)
        {
            case HdrKind.Hdr:
                parts.Add("HDR");
                parts.Add(classification.Conversion!.Transfer == HdrTransfer.Hlg ? "HLG" : $"PQ (peak {classification.Conversion.PeakNits} nit)");
                parts.Add("BT.2020");
                break;
            case HdrKind.UnsupportedHdr:
                parts.Add("HDR not converted: " + classification.Reason);
                break;
            default:
                parts.Add("SDR");
                if (metadata.ColorPrimaries is { } primaries) parts.Add(Describe(primaries));
                break;
        }
        if (metadata.BitDepth is { } depth) parts.Add($"{depth}-bit");
        if (metadata.DolbyVisionProfile is { } profile) parts.Add($"Dolby Vision {profile} (not applied)");
        if (metadata.HasHdrVivid) parts.Add("HDR Vivid (not applied)");
        return string.Join(" · ", parts);
    }

    /// <summary>Bits per component of an ffmpeg pixel format name (<c>yuv420p10le</c> → 10, <c>p010le</c> → 10,
    /// <c>yuv420p</c> → 8, <c>rgb48be</c> → 16); null when unknown.</summary>
    public static int? BitDepthOf(string? pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
            return null;
        var name = pixelFormat.ToLowerInvariant();
        if (name.EndsWith("le") || name.EndsWith("be"))
            name = name[..^2];
        switch (name)
        {
            case "p010": case "p210": case "p410": return 10;
            case "p012": return 12;
            case "p016": case "rgb48": case "bgr48": case "rgba64": case "bgra64": return 16;
            case "nv12": case "nv21": case "nv16": case "nv24": case "rgb24": case "bgr24": case "rgba": case "bgra": case "argb":
            case "abgr": case "rgb0": case "bgr0": case "gray": case "pal8": case "uyvy422": case "yuyv422": return 8;
        }
        // yuv420p10, yuva444p12, gbrp10, gray16 …: the digits after the last 'p' (or after "gray").
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length > 0 && (name.StartsWith("yuv") || name.StartsWith("gbr") || name.StartsWith("gray")) &&
            int.TryParse(digits, out var bits) && bits is >= 8 and <= 16)
            return bits;
        if (name.StartsWith("yuv") || name.StartsWith("yuvj") || name.StartsWith("gbrp"))
            return 8;
        return null;
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string? value) => value switch
    {
        null => "unknown",
        "bt709" => "BT.709",
        "bt2020" => "BT.2020",
        "bt2020nc" => "BT.2020 NCL",
        "bt2020c" => "BT.2020 CL",
        "smpte170m" => "BT.601",
        "bt470bg" => "BT.601",
        _ => value
    };
}
