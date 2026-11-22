using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Export;

/// <summary>
/// The expected size of an export's file before it starts (D059 §4) — an estimate, never a promise: a range from
/// <see cref="LowBytes"/> to <see cref="HighBytes"/>.
/// <list type="bullet">
/// <item>Average bitrate (<see cref="ExportRateControl.Bitrate"/>): the bitrate × the length, ±<see cref="BitrateTolerance"/>
/// (the encoder holds the average closely over a long export; a short one may miss it more).</item>
/// <item>Constant quality: the size follows the picture, so it can only be estimated from measurements — bits per pixel and
/// frame (bpp) measured by the calibration of D059 §5 on the owner's climbing videos (4K60 and 1080p60, a 30 fps source,
/// a vertical HDR → SDR one) for each encoder and preset, scaled by the quality value (the bits halve every
/// <see cref="X264CrfHalving"/> CRF / <see cref="NvencCqHalving"/> CQ steps), by the canvas and by the frame rate (a higher
/// rate fewer per frame). The range is the spread the calibration measured
/// between calm and busy scenes; a scene outside it (a static title card, heavy grain) can lie outside the range.</item>
/// <item>Audio: the AAC bitrate × the length; the container: <see cref="ContainerOverhead"/>.</item>
/// </list>
/// </summary>
public sealed record ExportSizeEstimate(long LowBytes, long HighBytes, bool FromBitrate)
{
    /// <summary>± of an average-bitrate estimate.</summary>
    public const double BitrateTolerance = 0.07;

    /// <summary>MP4 boxes and the index (+faststart), measured ≈ 0.3–0.6 %.</summary>
    public const double ContainerOverhead = 0.005;

    // The calibration of 2026-11-11 (D059 §5; docs/EXPORT_PERFORMANCE.md §5): bits per pixel and frame measured on the owner's
    // footage — two 4K60 routes, a 4K30 one, a 1080p60 one and the vertical HDR → SDR one.

    /// <summary>libx264: the bits halve about every 4 CRF steps (measured 14 → 18 → 23 → 28: 1.7×, 2.5×, 2.7×).</summary>
    public const double X264CrfHalving = 4.0;

    /// <summary>NVENC: about every 4.5 CQ steps (measured p5 18 → 22 → 28: 1.6×, 2.6×).</summary>
    public const double NvencCqHalving = 4.5;

    /// <summary>bpp ∝ (pixels / 4K)^<see cref="X264ResolutionExponent"/>: smaller canvases needed fewer bits per pixel here
    /// (1080p ≈ 0.75 × the 4K bpp with libx264, about the same with NVENC).</summary>
    public const double X264ResolutionExponent = 0.2, NvencResolutionExponent = 0.0;

    /// <summary>bpp ∝ (rate / 60)^−exponent: a 30 fps 4K source needed 1.53× (libx264) / 1.22× (NVENC) the bits per frame.</summary>
    public const double X264FrameRateExponent = 0.6, NvencFrameRateExponent = 0.3;

    /// <summary>The range around the median: the calibration footage (a steady camera in a gym) spread ±15 %; handheld and
    /// busy outdoor scenes are not in it, so the range is wider — honest about what is not measured.</summary>
    public const double LowFactor = 0.6, HighFactor = 1.6;

    private const double ReferencePixels = 3840.0 * 2160;

    /// <summary>Median bpp at 4K60 of libx264 at CRF 18, per preset (measured: veryfast 0.046, medium 0.067; slow from
    /// slow CRF 16 0.087 scaled to CRF 18; fast between veryfast and medium — not measured).</summary>
    public static double X264Bpp(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" => 0.046,
        "faster" or "fast" => 0.056,
        "slow" or "slower" or "veryslow" => 0.061,
        _ => 0.067   // medium
    };

    /// <summary>Median bpp at 4K60 of NVENC at CQ 22, per preset (measured: p2 0.118, p5 0.106; p7 multipass from CQ 20
    /// 0.133 scaled to CQ 22; p3 / p4 / p6 between — not measured).</summary>
    public static double NvencBpp(string preset) => preset switch
    {
        "p1" or "p2" => 0.118,
        "p3" or "p4" => 0.112,
        "p6" or "p7" => 0.096,
        _ => 0.106    // p5
    };

    /// <summary>The estimate for <paramref name="plan"/> over <paramref name="output"/> with AAC at <paramref name="audioKbps"/>.</summary>
    public static ExportSizeEstimate For(ExportVideoPlan plan, ExportOutput output, int audioKbps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        var seconds = output.Duration.TotalSeconds;
        var audio = audioKbps * 1000.0 / 8 * seconds;
        if (plan.RateControl == ExportRateControl.Bitrate)
        {
            var video = plan.VideoBitrateKbps * 1000.0 / 8 * seconds;
            return Make(video * (1 - BitrateTolerance), video * (1 + BitrateTolerance), audio, fromBitrate: true);
        }
        var bpp = BitsPerPixel(plan, output);
        var median = bpp * output.Size.Width * output.Size.Height * output.FrameCount / 8;
        return Make(median * LowFactor, median * HighFactor, audio, fromBitrate: false);
    }

    /// <summary>D076 §5: the median bpp at 4K60 of the HEVC / AV1 encoders at their reference value (<see cref="Reference"/>),
    /// per preset — measured by the calibration.</summary>
    public static double HevcAv1Bpp(ExportVideoPlan plan) => (plan.Encoder, plan.Codec) switch
    {
        (ExportVideoEncoder.Software, ExportVideoCodec.Hevc) => plan.Preset switch { "veryfast" => 0.040, "fast" => 0.038, "slow" => 0.034, _ => 0.036 },
        (ExportVideoEncoder.Nvenc, ExportVideoCodec.Hevc) => plan.Preset switch { "p2" => 0.070, "p3" => 0.066, "p7" => 0.058, _ => 0.062 },
        (ExportVideoEncoder.Software, _) => plan.Preset switch { "11" => 0.036, "9" => 0.033, "5" => 0.028, _ => 0.030 },
        _ => plan.Preset switch { "p2" => 0.060, "p3" => 0.056, "p7" => 0.050, _ => 0.053 }
    };

    /// <summary>The quality value each encoder's bpp is measured at, and how many steps halve the bits.</summary>
    public static (int Reference, double Halving) Scale(ExportVideoPlan plan) => (plan.Encoder, plan.Codec) switch
    {
        (ExportVideoEncoder.Software, ExportVideoCodec.H264) => (18, X264CrfHalving),
        (ExportVideoEncoder.Nvenc, ExportVideoCodec.H264) => (22, NvencCqHalving),
        (ExportVideoEncoder.Software, ExportVideoCodec.Hevc) => (20, HevcCrfHalving),
        (ExportVideoEncoder.Nvenc, ExportVideoCodec.Hevc) => (24, NvencCqHalving),
        (ExportVideoEncoder.Software, _) => (28, Av1CrfHalving),
        _ => (30, Av1CrfHalving)
    };

    /// <summary>D076 §5: libx265 halves the bits about every 5 CRF steps; AV1 (0–63 scale) about every 8.</summary>
    public const double HevcCrfHalving = 5.0, Av1CrfHalving = 8.0;

    /// <summary>The calibrated median bits per pixel and frame of <paramref name="plan"/> at the output's size and rate.</summary>
    public static double BitsPerPixel(ExportVideoPlan plan, ExportOutput output)
    {
        var nvenc = plan.Encoder == ExportVideoEncoder.Nvenc;
        var (reference, halving) = Scale(plan);
        var bpp = plan.Codec == ExportVideoCodec.H264 ? (nvenc ? NvencBpp(plan.Preset) : X264Bpp(plan.Preset)) : HevcAv1Bpp(plan);
        var at4K60 = bpp * Math.Pow(2, (reference - plan.Quality) / halving);
        var pixels = Math.Max(1.0, (double)output.Size.Width * output.Size.Height);
        var rate = output.FrameRate.IsValid ? output.FrameRate.ToDouble() : 60;
        return at4K60 * Math.Pow(pixels / ReferencePixels, nvenc ? NvencResolutionExponent : X264ResolutionExponent)
                      * Math.Pow(Math.Max(1, rate) / 60, -(nvenc ? NvencFrameRateExponent : X264FrameRateExponent));
    }

    private static ExportSizeEstimate Make(double videoLow, double videoHigh, double audio, bool fromBitrate) =>
        new((long)((videoLow + audio) * (1 + ContainerOverhead)), (long)((videoHigh + audio) * (1 + ContainerOverhead)), fromBitrate);
}
