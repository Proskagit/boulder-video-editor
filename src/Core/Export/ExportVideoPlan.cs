using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Export;

/// <summary>What the encoders of this machine can do (D059 §3), as the app found out (<see cref="IExportEncoderCapabilities"/>).</summary>
public sealed record ExportEncoderAvailability(bool Nvenc, string? NvencUnavailableReason = null)
{
    /// <summary>libx264 only (no NVIDIA encoder, not checked yet, or the check failed).</summary>
    public static ExportEncoderAvailability SoftwareOnly { get; } = new(false);

    /// <summary>The largest frame NVENC H.264 encodes (both sides; NVIDIA's limit for H.264 on every supported GPU).</summary>
    public const int NvencMaxSide = 4096;

    /// <summary>Whether NVENC can encode <paramref name="size"/>.</summary>
    public bool NvencCan(FrameSize size) => Nvenc && size.Width <= NvencMaxSide && size.Height <= NvencMaxSide;
}

/// <summary>Finds out once which encoders work here (D059 §3): libx264 always; NVENC only when ffmpeg has
/// <c>h264_nvenc</c> and a short trial encoding on the graphics card succeeds.</summary>
public interface IExportEncoderCapabilities
{
    /// <summary>The availability (checked once, then cached; never throws).</summary>
    Task<ExportEncoderAvailability> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// The concrete H.264 encoding of an export (D059 §3–§4): the encoder actually used and its parameters, resolved from the
/// job's <see cref="ExportEncoding"/> and what the machine can do. Pure: the size estimate and the ffmpeg command line read
/// the same plan.
/// <list type="bullet">
/// <item>libx264: <see cref="Preset"/> is the x264 preset, <see cref="Quality"/> the CRF.</item>
/// <item>NVENC: <see cref="Preset"/> is p1…p7, <see cref="Quality"/> the CQ target of VBR (<c>-rc vbr -cq N -b:v 0</c>).
/// CQ and CRF are different scales — never equal numbers (at CQ 19 h264_nvenc writes about twice the bits of x264 CRF 18,
/// the research §3.4); every CQ here comes from the calibration against libx264 by VMAF (D059 §5).</item>
/// <item><see cref="ExportRateControl.Bitrate"/>: an average bitrate instead of a quality target, either encoder.</item>
/// </list>
/// </summary>
public sealed record ExportVideoPlan(ExportVideoEncoder Encoder, string Preset, int Quality, ExportRateControl RateControl, int VideoBitrateKbps,
    bool Multipass, bool FellBack)
{
    /// <summary>NVENC spatial adaptive quantization (on in every plan; off only for the calibration's comparison).</summary>
    public bool SpatialAq { get; init; } = true;

    /// <summary>The plan for <paramref name="encoding"/> at <paramref name="size"/> with <paramref name="available"/>:
    /// NVENC only where it is available and can encode the size — otherwise libx264 of the same profile
    /// (<see cref="FellBack"/> when NVENC was chosen).</summary>
    public static ExportVideoPlan For(ExportEncoding encoding, FrameSize size, ExportEncoderAvailability available)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var nvenc = encoding.Encoder == ExportVideoEncoder.Nvenc && available.NvencCan(size);
        var fellBack = encoding.Encoder == ExportVideoEncoder.Nvenc && !nvenc;
        var bitrate = encoding.Profile == ExportProfile.Custom && encoding.RateControl == ExportRateControl.Bitrate
            ? ExportRateControl.Bitrate : ExportRateControl.ConstantQuality;
        var kbps = bitrate == ExportRateControl.Bitrate ? encoding.VideoBitrateMbps * 1000 : 0;
        if (!nvenc)
        {
            var (preset, crf) = encoding.Profile switch
            {
                ExportProfile.Fast => (X264FastPreset, X264FastCrf),
                ExportProfile.Balanced => ("medium", 18),
                ExportProfile.HighQuality => (X264HighQualityPreset, X264HighQualityCrf),
                _ => (encoding.PresetName, encoding.Crf)
            };
            return new ExportVideoPlan(ExportVideoEncoder.Libx264, preset, crf, bitrate, kbps, false, fellBack);
        }
        var (p, cq, multipass) = encoding.Profile switch
        {
            ExportProfile.Fast => (NvencFastPreset, NvencFastCq, false),
            ExportProfile.Balanced => (NvencBalancedPreset, NvencBalancedCq, false),
            ExportProfile.HighQuality => (NvencHighQualityPreset, NvencHighQualityCq, true),
            _ => (NvencPresetOf(encoding.Preset), NvencCqOf(encoding.Quality), encoding.Preset == ExportSpeedPreset.Slow)
        };
        return new ExportVideoPlan(ExportVideoEncoder.Nvenc, p, cq, bitrate, kbps, multipass, false);
    }

    // --- The profiles (D059 §4; the values from the calibration of §5) ---------------------------------------------------

    /// <summary>«Быстрый» with libx264.</summary>
    public const string X264FastPreset = "veryfast";
    public const int X264FastCrf = 18;

    /// <summary>«Высокое качество» with libx264.</summary>
    public const string X264HighQualityPreset = "slow";
    public const int X264HighQualityCrf = 16;

    public const string NvencFastPreset = "p2";
    public const int NvencFastCq = 22;
    public const string NvencBalancedPreset = "p5";
    public const int NvencBalancedCq = 22;
    public const string NvencHighQualityPreset = "p7";
    public const int NvencHighQualityCq = 20;

    /// <summary>«Пользовательский» speed → NVENC preset.</summary>
    public static string NvencPresetOf(ExportSpeedPreset preset) => preset switch
    {
        ExportSpeedPreset.Fast => "p3",
        ExportSpeedPreset.Medium => "p5",
        ExportSpeedPreset.Slow => "p7",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown export speed preset.")
    };

    /// <summary>«Пользовательский» quality level → NVENC CQ: calibrated so that NVENC is not below libx264 at the level's
    /// CRF by VMAF (D059 §5) — not the CRF number itself.</summary>
    public static int NvencCqOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 18,
        ExportQuality.High => 22,
        ExportQuality.Standard => 27,
        ExportQuality.Compact => 31,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>"libx264 medium CRF 18", "NVENC p5 CQ 22", "libx264 medium 40 Mbit/s" — for the log and the UI.</summary>
    public string Description => (Encoder == ExportVideoEncoder.Nvenc ? $"NVENC {Preset}" : $"libx264 {Preset}") +
        (RateControl == ExportRateControl.Bitrate ? $" {VideoBitrateKbps / 1000} Mbit/s" : Encoder == ExportVideoEncoder.Nvenc ? $" CQ {Quality}" : $" CRF {Quality}");
}
