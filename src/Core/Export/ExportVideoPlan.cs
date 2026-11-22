using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Export;

/// <summary>What the encoders of this machine can do (D059 §3, D076), as the app found out (<see cref="IExportEncoderCapabilities"/>):
/// NVENC per codec (each confirmed by a trial encoding) and whether the FFmpeg found has the software HEVC / AV1 encoders
/// (libx265, SVT-AV1; libx264 is always there).</summary>
/// <param name="Nvenc">NVENC H.264 (<c>h264_nvenc</c>) confirmed.</param>
/// <param name="NvencUnavailableReason">Why NVENC (H.264) is not available, for the dialog.</param>
public sealed record ExportEncoderAvailability(bool Nvenc, string? NvencUnavailableReason = null)
{
    /// <summary>The processor only, every codec assumed (not checked yet, or no NVIDIA encoder).</summary>
    public static ExportEncoderAvailability SoftwareOnly { get; } = new(false);

    /// <summary>The largest frame NVENC H.264 encodes (both sides; NVIDIA's limit for H.264 on every supported GPU).</summary>
    public const int NvencMaxSide = 4096;

    /// <summary>The largest frame NVENC HEVC / AV1 encode (both sides, Turing and later).</summary>
    public const int NvencHevcAv1MaxSide = 8192;

    /// <summary>D076: NVENC HEVC (<c>hevc_nvenc</c>) confirmed.</summary>
    public bool NvencHevc { get; init; }

    /// <summary>D076: NVENC AV1 (<c>av1_nvenc</c>, Ada and later) confirmed.</summary>
    public bool NvencAv1 { get; init; }

    /// <summary>D076: the FFmpeg found has libx265.</summary>
    public bool SoftwareHevc { get; init; } = true;

    /// <summary>D076: the FFmpeg found has SVT-AV1 (<c>libsvtav1</c>).</summary>
    public bool SoftwareAv1 { get; init; } = true;

    /// <summary>Whether NVENC encodes <paramref name="codec"/> here.</summary>
    public bool NvencFor(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => Nvenc,
        ExportVideoCodec.Hevc => NvencHevc,
        ExportVideoCodec.Av1 => NvencAv1,
        _ => false
    };

    /// <summary>Whether the processor encodes <paramref name="codec"/> here (H.264 always).</summary>
    public bool SoftwareFor(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => true,
        ExportVideoCodec.Hevc => SoftwareHevc,
        ExportVideoCodec.Av1 => SoftwareAv1,
        _ => false
    };

    /// <summary>The largest side NVENC encodes for <paramref name="codec"/>.</summary>
    public static int NvencMaxSideOf(ExportVideoCodec codec) => codec == ExportVideoCodec.H264 ? NvencMaxSide : NvencHevcAv1MaxSide;

    /// <summary>Whether NVENC H.264 can encode <paramref name="size"/>.</summary>
    public bool NvencCan(FrameSize size) => NvencCan(ExportVideoCodec.H264, size);

    /// <summary>Whether NVENC can encode <paramref name="codec"/> at <paramref name="size"/>.</summary>
    public bool NvencCan(ExportVideoCodec codec, FrameSize size) =>
        NvencFor(codec) && size.Width <= NvencMaxSideOf(codec) && size.Height <= NvencMaxSideOf(codec);
}

/// <summary>Finds out once which encoders work here (D059 §3, D076): libx264 always; libx265 / SVT-AV1 when the FFmpeg found
/// has them; NVENC per codec only when ffmpeg has the encoder and a short trial encoding on the graphics card succeeds.</summary>
public interface IExportEncoderCapabilities
{
    /// <summary>The availability (checked once, then cached; never throws).</summary>
    Task<ExportEncoderAvailability> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// The concrete video encoding of an export (D059 §3–§4, D076): the codec, the encoder actually used and its parameters,
/// resolved from the job's <see cref="ExportEncoding"/> and what the machine can do. Pure: the size estimate and the ffmpeg
/// command line read the same plan.
/// <list type="bullet">
/// <item>The processor: <see cref="Preset"/> is the x264 / x265 preset or the SVT-AV1 preset number, <see cref="Quality"/>
/// the CRF of that encoder.</item>
/// <item>NVENC: <see cref="Preset"/> is p1…p7, <see cref="Quality"/> the CQ target of VBR (<c>-rc vbr -cq N -b:v 0</c>).</item>
/// <item>Every encoder's value of a quality level comes from the calibration against libx264 by VMAF (D059 §5, D076 §5):
/// the same picture quality, never the same number.</item>
/// <item><see cref="ExportRateControl.Bitrate"/>: an average bitrate instead of a quality target, any encoder.</item>
/// </list>
/// </summary>
public sealed record ExportVideoPlan(ExportVideoEncoder Encoder, string Preset, int Quality, ExportRateControl RateControl, int VideoBitrateKbps,
    bool Multipass, bool FellBack)
{
    /// <summary>D076: the codec (H.264 unless stated).</summary>
    public ExportVideoCodec Codec { get; init; } = ExportVideoCodec.H264;

    /// <summary>D076: neither NVENC nor the processor can encode the codec with this FFmpeg — the export can't start.</summary>
    public bool Unavailable { get; init; }

    /// <summary>NVENC spatial adaptive quantization (on in every plan; off only for the calibration's comparison).</summary>
    public bool SpatialAq { get; init; } = true;

    /// <summary>The plan for <paramref name="encoding"/> at <paramref name="size"/> with <paramref name="available"/>:
    /// NVENC only where it is available for the codec and can encode the size — otherwise the processor, the same codec
    /// (<see cref="FellBack"/> when NVENC was chosen).</summary>
    public static ExportVideoPlan For(ExportEncoding encoding, FrameSize size, ExportEncoderAvailability available)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(available);
        var codec = encoding.Codec;
        var nvenc = encoding.Encoder == ExportVideoEncoder.Nvenc && available.NvencCan(codec, size);
        var fellBack = encoding.Encoder == ExportVideoEncoder.Nvenc && !nvenc;
        var rate = encoding.RateControl;
        var kbps = rate == ExportRateControl.Bitrate ? encoding.VideoBitrateMbps * 1000 : 0;
        if (!nvenc)
            return new ExportVideoPlan(ExportVideoEncoder.Software, SoftwarePreset(codec, encoding.Preset), SoftwareQuality(codec, encoding.Preset, encoding.Quality),
                rate, kbps, false, fellBack) { Codec = codec, Unavailable = !available.SoftwareFor(codec) };
        return new ExportVideoPlan(ExportVideoEncoder.Nvenc, NvencPresetOf(encoding.Preset), NvencQuality(codec, encoding.Preset, encoding.Quality),
            rate, kbps, encoding.Preset == ExportSpeedPreset.Slow, false) { Codec = codec };
    }

    // --- The calibration (D059 §5 for H.264; D076 §5 for HEVC / AV1; docs/EXPORT_PERFORMANCE.md) ----------------------------

    /// <summary>The speed → the NVENC preset: p2 / p3 / p5 / p7 (p7 with multipass, <see cref="Multipass"/>).</summary>
    public static string NvencPresetOf(ExportSpeedPreset preset) => preset switch
    {
        ExportSpeedPreset.VeryFast => "p2",
        ExportSpeedPreset.Fast => "p3",
        ExportSpeedPreset.Medium => "p5",
        ExportSpeedPreset.Slow => "p7",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown export speed preset.")
    };

    /// <summary>NVENC H.264's CQ of a quality level (D059 §5): the largest CQ whose VMAF is not below libx264 at the level's
    /// CRF on every reference — Maximum ↔ CRF 14, VeryHigh ↔ slow CRF 16 (p7 multipass), High ↔ medium CRF 18, Standard ↔ 23,
    /// Compact ↔ 28.</summary>
    public static int NvencCqOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 19,
        ExportQuality.VeryHigh => 20,
        ExportQuality.High => 22,
        ExportQuality.Standard => 27,
        ExportQuality.Compact => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>At «Очень быстро» the counterpart is libx264 veryfast, whose VMAF is lower: NVENC p2 holds it at 3 CQ more
    /// (measured at CRF 18: CQ 25 against p5's 22 — D059's «Быстрый»).</summary>
    public const int NvencVeryFastCqOffset = 3;

    /// <summary>The NVENC CQ of <paramref name="codec"/> at <paramref name="speed"/> and <paramref name="quality"/>.</summary>
    public static int NvencQuality(ExportVideoCodec codec, ExportSpeedPreset speed, ExportQuality quality)
    {
        var level = codec switch
        {
            ExportVideoCodec.H264 => NvencCqOf(quality),
            ExportVideoCodec.Hevc => HevcNvencCqOf(quality),
            ExportVideoCodec.Av1 => Av1NvencCqOf(quality),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown export codec.")
        };
        return level + (speed == ExportSpeedPreset.VeryFast ? NvencVeryFastCqOffset : 0);
    }

    /// <summary>D076 §5: NVENC HEVC's CQ of a quality level (the rule of D059 §5).</summary>
    public static int HevcNvencCqOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 21,
        ExportQuality.VeryHigh => 22,
        ExportQuality.High => 24,
        ExportQuality.Standard => 29,
        ExportQuality.Compact => 34,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>D076 §5: NVENC AV1's CQ (0–63) of a quality level.</summary>
    public static int Av1NvencCqOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 24,
        ExportQuality.VeryHigh => 27,
        ExportQuality.High => 30,
        ExportQuality.Standard => 36,
        ExportQuality.Compact => 42,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>The processor's preset of <paramref name="codec"/>: x264 / x265 by name, SVT-AV1 by number (higher faster).</summary>
    public static string SoftwarePreset(ExportVideoCodec codec, ExportSpeedPreset speed) => codec == ExportVideoCodec.Av1
        ? speed switch
        {
            ExportSpeedPreset.VeryFast => "11",
            ExportSpeedPreset.Fast => "9",
            ExportSpeedPreset.Medium => "7",
            ExportSpeedPreset.Slow => "5",
            _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "Unknown export speed preset.")
        }
        : ExportEncoding.PresetNameOf(speed);

    /// <summary>The processor's CRF of <paramref name="codec"/> for a quality level: libx264's own number (D028); libx265 and
    /// SVT-AV1 calibrated to libx264 at the same speed (D076 §5).</summary>
    public static int SoftwareQuality(ExportVideoCodec codec, ExportSpeedPreset speed, ExportQuality quality) => codec switch
    {
        ExportVideoCodec.H264 => ExportEncoding.CrfOf(quality),
        ExportVideoCodec.Hevc => X265CrfOf(quality),
        ExportVideoCodec.Av1 => SvtAv1CrfOf(quality),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown export codec.")
    };

    /// <summary>D076 §5: libx265's CRF of a quality level.</summary>
    public static int X265CrfOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 16,
        ExportQuality.VeryHigh => 18,
        ExportQuality.High => 20,
        ExportQuality.Standard => 25,
        ExportQuality.Compact => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>D076 §5: SVT-AV1's CRF (0–63) of a quality level.</summary>
    public static int SvtAv1CrfOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 22,
        ExportQuality.VeryHigh => 25,
        ExportQuality.High => 28,
        ExportQuality.Standard => 35,
        ExportQuality.Compact => 42,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    /// <summary>The encoder's name: "libx264", "libx265", "SVT-AV1", "NVENC" (H.264), "NVENC HEVC", "NVENC AV1".</summary>
    public string EncoderName => (Encoder, Codec) switch
    {
        (ExportVideoEncoder.Nvenc, ExportVideoCodec.H264) => "NVENC",
        (ExportVideoEncoder.Nvenc, ExportVideoCodec.Hevc) => "NVENC HEVC",
        (ExportVideoEncoder.Nvenc, _) => "NVENC AV1",
        (_, ExportVideoCodec.Hevc) => "libx265",
        (_, ExportVideoCodec.Av1) => "SVT-AV1",
        _ => "libx264"
    };

    /// <summary>"libx264 medium CRF 18", "NVENC p5 CQ 22", "NVENC HEVC p7 CQ 21", "SVT-AV1 preset 7 CRF 28",
    /// "libx264 medium 40 Mbit/s" — for the log and the UI.</summary>
    public string Description => $"{EncoderName} {(Codec == ExportVideoCodec.Av1 && Encoder == ExportVideoEncoder.Software ? "preset " : "")}{Preset}" +
        (RateControl == ExportRateControl.Bitrate ? $" {VideoBitrateKbps / 1000} Mbit/s" : Encoder == ExportVideoEncoder.Nvenc ? $" CQ {Quality}" : $" CRF {Quality}");
}
