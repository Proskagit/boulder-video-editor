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
/// <see cref="X264CrfHalving"/> CRF / <see cref="NvencCqHalving"/> CQ steps), by the canvas (a smaller frame needs more
/// bits per pixel) and by the frame rate (a higher rate fewer per frame). The range is the spread the calibration measured
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

    public const double X264CrfHalving = 6.0;
    public const double NvencCqHalving = 6.0;

    /// <summary>bpp ∝ (pixels / 4K)^−<see cref="ResolutionExponent"/>.</summary>
    public const double ResolutionExponent = 0.25;

    /// <summary>bpp ∝ (rate / 60)^−<see cref="FrameRateExponent"/>.</summary>
    public const double FrameRateExponent = 0.4;

    /// <summary>The calibration's spread around the median scene: the calm end and the busy end.</summary>
    public const double LowFactor = 0.6, HighFactor = 1.5;

    private const double ReferencePixels = 3840.0 * 2160;

    /// <summary>Median bpp at 4K60 of libx264 at CRF 18, per preset (D059 §5).</summary>
    public static double X264Bpp(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" => 0.0450,
        "faster" => 0.0560,
        "fast" => 0.0640,
        "slow" or "slower" or "veryslow" => 0.0660,
        _ => 0.0650   // medium
    };

    /// <summary>Median bpp at 4K60 of NVENC at CQ 22, per preset (D059 §5).</summary>
    public static double NvencBpp(string preset) => preset switch
    {
        "p1" or "p2" => 0.110,
        "p3" or "p4" => 0.100,
        "p6" or "p7" => 0.095,
        _ => 0.098    // p5
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

    /// <summary>The calibrated median bits per pixel and frame of <paramref name="plan"/> at the output's size and rate.</summary>
    public static double BitsPerPixel(ExportVideoPlan plan, ExportOutput output)
    {
        var at4K60 = plan.Encoder == ExportVideoEncoder.Nvenc
            ? NvencBpp(plan.Preset) * Math.Pow(2, (22 - plan.Quality) / NvencCqHalving)
            : X264Bpp(plan.Preset) * Math.Pow(2, (18 - plan.Quality) / X264CrfHalving);
        var pixels = Math.Max(1.0, (double)output.Size.Width * output.Size.Height);
        var rate = output.FrameRate.IsValid ? output.FrameRate.ToDouble() : 60;
        return at4K60 * Math.Pow(pixels / ReferencePixels, -ResolutionExponent) * Math.Pow(Math.Max(1, rate) / 60, -FrameRateExponent);
    }

    private static ExportSizeEstimate Make(double videoLow, double videoHigh, double audio, bool fromBitrate) =>
        new((long)((videoLow + audio) * (1 + ContainerOverhead)), (long)((videoHigh + audio) * (1 + ContainerOverhead)), fromBitrate);
}
