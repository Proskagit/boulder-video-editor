using System.Text.Json.Serialization;
using AiVideoEditor.Core.Localization;
namespace AiVideoEditor.Core.Entities;

/// <summary>The quality of an export (D028 §7, refined in Step 13.3; D076): a named level, stored by name; each level is one
/// libx264 constant-quality value (<see cref="ExportEncoding.Crf"/>) and, since D059 / D076, one calibrated value of every other
/// encoder (<see cref="Export.ExportVideoPlan"/>) — the same picture quality by VMAF, not the same number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportQuality>))]
public enum ExportQuality
{
    /// <summary>CRF 14 — «Максимальное».</summary>
    Maximum,
    /// <summary>CRF 16 — «Очень высокое» (D076: the «Высокий» preset; D059's «Высокое качество» level).</summary>
    VeryHigh,
    /// <summary>CRF 18 — the fixed quality of Phases 8–12 (D023).</summary>
    High,
    /// <summary>CRF 23.</summary>
    Standard,
    /// <summary>CRF 28.</summary>
    Compact
}

/// <summary>The speed / efficiency of an export (D028 §7; D076): libx264 veryfast / fast / medium / slow, NVENC p2 / p3 / p5 /
/// p7 (slow with multipass), and the same steps of libx265 and SVT-AV1.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportSpeedPreset>))]
public enum ExportSpeedPreset
{
    /// <summary>D076: «Очень быстро» — D059's «Быстрый» profile's speed (libx264 veryfast, NVENC p2).</summary>
    VeryFast,
    Fast,
    /// <summary>The fixed preset of Phases 8–12 (D023).</summary>
    Medium,
    Slow
}

/// <summary>Where the video is encoded (D059 §3, D076): on the processor (libx264 / libx265 / SVT-AV1 — the fallback) or on an
/// NVIDIA graphics card (NVENC, only where the check confirmed the codec's encoder). Both run through FFmpeg.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportVideoEncoder>))]
public enum ExportVideoEncoder
{
    /// <summary>Software encoding on the processor — the fallback (H.264 always available).</summary>
    Software,
    /// <summary>NVIDIA NVENC hardware encoding on the graphics card — only where it is confirmed available.</summary>
    Nvenc
}

/// <summary>
/// The size of the exported video (D060 §3): the project's canvas, or a standard preset scaled to the canvas's aspect ratio
/// (<see cref="Export.ExportResolutions"/>). Only the output's pixel size — never the canvas, the composition's geometry or
/// the overlays' places, which stay in canvas coordinates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportResolution>))]
public enum ExportResolution
{
    /// <summary>The canvas itself (the output of Phases 8–40).</summary>
    Project,
    /// <summary>4K UHD: the short side 2160 (3840 × 2160 at 16:9).</summary>
    Uhd4K,
    /// <summary>D076: 2K (QHD): the short side 1440 (2560 × 1440 at 16:9).</summary>
    Qhd,
    /// <summary>Full HD: the short side 1080 (1920 × 1080; 1080 × 1920 vertical).</summary>
    FullHd,
    /// <summary>HD: the short side 720 (1280 × 720).</summary>
    Hd,
    /// <summary>SD: the short side 480 (854 × 480).</summary>
    Sd
}

/// <summary>How an export spends bits (D059 §4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportRateControl>))]
public enum ExportRateControl
{
    /// <summary>Constant quality (CRF, NVENC VBR with a CQ target): the size follows the picture.</summary>
    ConstantQuality,
    /// <summary>An average video bitrate (<see cref="ExportEncoding.VideoBitrateMbps"/>): the size follows the length.</summary>
    Bitrate
}

/// <summary>
/// How the project is encoded on export (D028; the encoder since D059; the codec, the container and the presets since D076):
/// every field explicit — the codec, the container, the encoder, the output's resolution, the quality level, the speed, the
/// rate control, the video bitrate and the AAC bitrate. A preset (<see cref="Export.ExportPresets"/>, or the user's own
/// <see cref="SavedExportPreset"/>) is a named value of this record; «Пользовательский» is any other. Saved with the project
/// (<c>settings.export</c> in <c>project.json</c>, written only when it differs from <see cref="Default"/>; a file without it
/// reads as <see cref="Default"/>; a D028 / D059 file reads as exactly its old output, D076 Q7). The pixel format (8-bit
/// 4:2:0), the colour tags and the audio format (AAC-LC 48 kHz stereo) stay fixed (<see cref="Export.ExportFormat"/>); the
/// frame rate is always the project's.
/// <para>
/// The positional constructor makes an H.264 / MP4 encoding on the processor at the canvas's size with constant quality
/// (the D028 meaning of the three values); <see cref="Default"/> is <c>(High, Medium, 192)</c> — libx264 medium CRF 18, the
/// output of Phases 8–56 for a project without export settings.
/// </para>
/// </summary>
public sealed record ExportEncoding(ExportQuality Quality, ExportSpeedPreset Preset, int AudioBitrateKbps)
{
    /// <summary>Today's output for a project without export settings (D023, D028): libx264 CRF 18, medium, AAC 192 kbps.</summary>
    public static ExportEncoding Default { get; } = new(ExportQuality.High, ExportSpeedPreset.Medium, 192);

    /// <summary>D074 §6, D076 Q6: a new project's export — the «Максимальный» preset (HEVC on NVENC; the processor where NVENC
    /// is unavailable). <see cref="Default"/> stays the meaning of a file without export settings.</summary>
    public static ExportEncoding NewProject => Export.ExportPresets.Maximum;

    /// <summary>The AAC bitrates offered, in kbps.</summary>
    public static IReadOnlyList<int> AudioBitratesKbps { get; } = new[] { 128, 160, 192, 256, 320 };

    /// <summary>The video bitrates offered for <see cref="ExportRateControl.Bitrate"/>, in Mbit/s.</summary>
    public static IReadOnlyList<int> VideoBitratesMbps { get; } = new[] { 8, 12, 16, 20, 25, 30, 40, 50, 60, 80, 100 };

    /// <summary>The default of <see cref="VideoBitrateMbps"/>.</summary>
    public const int DefaultVideoBitrateMbps = 40;

    /// <summary>D076: the video codec (H.264 — every file before D076).</summary>
    public ExportVideoCodec Codec { get; init; } = ExportVideoCodec.H264;

    /// <summary>D076: the file's container (MP4 — every file before D076). AV1 is never in MOV (<see cref="Validate"/>).</summary>
    public ExportContainer Container { get; init; } = ExportContainer.Mp4;

    /// <summary>The encoder the user chose (D059 §3); NVENC falls back to the processor where it is unavailable — the same
    /// codec, never another one.</summary>
    public ExportVideoEncoder Encoder { get; init; } = ExportVideoEncoder.Software;

    /// <summary>Constant quality or an average bitrate.</summary>
    public ExportRateControl RateControl { get; init; } = ExportRateControl.ConstantQuality;

    /// <summary>With <see cref="ExportRateControl.Bitrate"/>: the average video bitrate, Mbit/s.</summary>
    public int VideoBitrateMbps { get; init; } = DefaultVideoBitrateMbps;

    /// <summary>The output's size (D060 §3): <see cref="ExportResolution.Project"/> — the canvas — unless a preset was chosen
    /// that differs from it; saved only when not <see cref="ExportResolution.Project"/>.</summary>
    public ExportResolution Resolution { get; init; } = ExportResolution.Project;

    /// <summary>The libx264 <c>-crf</c> value of <see cref="Quality"/>.</summary>
    [JsonIgnore] public int Crf => CrfOf(Quality);

    /// <summary>The libx264 <c>-preset</c> name of <see cref="Preset"/>.</summary>
    [JsonIgnore] public string PresetName => PresetNameOf(Preset);

    /// <summary>This encoding on the processor instead of the chosen encoder (the fallback, D059 §3): the same codec, quality
    /// level, speed and rate control.</summary>
    public ExportEncoding WithSoftware() => this with { Encoder = ExportVideoEncoder.Software };

    public static int CrfOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 14,
        ExportQuality.VeryHigh => 16,
        ExportQuality.High => 18,
        ExportQuality.Standard => 23,
        ExportQuality.Compact => 28,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    public static string PresetNameOf(ExportSpeedPreset preset) => preset switch
    {
        ExportSpeedPreset.VeryFast => "veryfast",
        ExportSpeedPreset.Fast => "fast",
        ExportSpeedPreset.Medium => "medium",
        ExportSpeedPreset.Slow => "slow",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown export speed preset.")
    };

    /// <summary>Whether <paramref name="container"/> can hold <paramref name="codec"/> (D076: FFmpeg's MOV muxer refuses AV1).</summary>
    public static bool Holds(ExportContainer container, ExportVideoCodec codec) =>
        !(container == ExportContainer.Mov && codec == ExportVideoCodec.Av1);

    /// <summary>A short user-facing reason when a value is not one of the offered ones, or null.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Quality)) return Messages.Encoding_Quality;
        if (!Enum.IsDefined(Preset)) return Messages.Encoding_Preset;
        if (!AudioBitratesKbps.Contains(AudioBitrateKbps)) return string.Format(Messages.Encoding_Bitrate, AudioBitrateKbps);
        if (!Enum.IsDefined(Encoder)) return Messages.Encoding_Encoder;
        if (!Enum.IsDefined(RateControl)) return Messages.Encoding_RateControl;
        if (!VideoBitratesMbps.Contains(VideoBitrateMbps)) return string.Format(Messages.Encoding_VideoBitrate, VideoBitrateMbps);
        if (!Enum.IsDefined(Resolution)) return Messages.Encoding_Resolution;
        if (!Enum.IsDefined(Codec)) return Messages.Encoding_Codec;
        if (!Enum.IsDefined(Container)) return Messages.Encoding_Container;
        if (!Holds(Container, Codec)) return Messages.Encoding_Av1Mov;
        return null;
    }
}

/// <summary>
/// D076: an export preset of the user's own — kept in the global settings (<see cref="Interfaces.UserSettings.ExportPresets"/>)
/// and offered in every project, as the saved text styles (D075). It states every field of the export (the codec, the
/// container, the encoder, the resolution — a ceiling at the canvas, Q4 —, the quality, the speed, the rate control and the
/// audio); applying it never changes the project's canvas or frame rate.
/// </summary>
public sealed record SavedExportPreset(string Name, ExportEncoding Settings);
