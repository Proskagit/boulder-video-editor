using AiVideoEditor.Core.Localization;
namespace AiVideoEditor.Core.Entities;

/// <summary>The quality of an export (D028 §7, refined in Step 13.3): a named level, stored by name; each level is one
/// libx264 constant-quality value (<see cref="ExportEncoding.Crf"/>) and, since D059, one calibrated NVENC value
/// (<see cref="Export.ExportVideoPlan"/>). Used by the «Пользовательский» profile.</summary>
public enum ExportQuality
{
    /// <summary>CRF 14.</summary>
    Maximum,
    /// <summary>CRF 18 — the fixed quality of Phases 8–12 (D023).</summary>
    High,
    /// <summary>CRF 23.</summary>
    Standard,
    /// <summary>CRF 28.</summary>
    Compact
}

/// <summary>The speed / efficiency preset of an export (D028 §7, refined in Step 13.3): libx264 fast / medium / slow;
/// NVENC p3 / p5 / p7 (D059). Used by the «Пользовательский» profile.</summary>
public enum ExportSpeedPreset
{
    Fast,
    /// <summary>The fixed preset of Phases 8–12 (D023).</summary>
    Medium,
    Slow
}

/// <summary>The export profile (D059 §4): a named balance of speed, quality and size, so the user need not know the
/// encoder's parameters. <see cref="Custom"/> uses the record's own quality, preset, rate control and bitrate.</summary>
public enum ExportProfile
{
    /// <summary>«Быстрый»: speed first (NVENC when chosen and available; otherwise a fast libx264 preset).</summary>
    Fast,
    /// <summary>«Сбалансированный» — the default: with libx264 exactly the Phase 8–39 output (CRF 18, medium).</summary>
    Balanced,
    /// <summary>«Высокое качество»: quality first; a longer export for fewer artefacts.</summary>
    HighQuality,
    /// <summary>«Пользовательский»: the quality level, the speed, the rate control and the bitrate by hand.</summary>
    Custom
}

/// <summary>The H.264 encoder of an export (D059 §3). Both run through FFmpeg.</summary>
public enum ExportVideoEncoder
{
    /// <summary>Software encoding on the processor — always available; the fallback.</summary>
    Libx264,
    /// <summary>NVIDIA NVENC hardware encoding on the graphics card — only where it is confirmed available.</summary>
    Nvenc
}

/// <summary>
/// The size of the exported video (D060 §3): the project's canvas, or a standard preset scaled to the canvas's aspect ratio
/// (<see cref="Export.ExportResolutions"/>). Only the output's pixel size — never the canvas, the composition's geometry or
/// the overlays' places, which stay in canvas coordinates.
/// </summary>
public enum ExportResolution
{
    /// <summary>The canvas itself (the output of Phases 8–40).</summary>
    Project,
    /// <summary>4K UHD: the short side 2160 (3840 × 2160 at 16:9).</summary>
    Uhd4K,
    /// <summary>Full HD: the short side 1080 (1920 × 1080; 1080 × 1920 vertical).</summary>
    FullHd,
    /// <summary>HD: the short side 720 (1280 × 720).</summary>
    Hd,
    /// <summary>SD: the short side 480 (854 × 480).</summary>
    Sd
}

/// <summary>How the «Пользовательский» profile spends bits (D059 §4).</summary>
public enum ExportRateControl
{
    /// <summary>Constant quality (libx264 CRF, NVENC VBR with a CQ target): the size follows the picture.</summary>
    ConstantQuality,
    /// <summary>An average video bitrate (<see cref="ExportEncoding.VideoBitrateMbps"/>): the size follows the length.</summary>
    Bitrate
}

/// <summary>
/// How the project is encoded on export (D028, refined in Step 13.3; profiles and the encoder since D059): the profile, the
/// chosen encoder, the AAC bitrate and the «Пользовательский» fields — the quality level, the preset, the rate control and
/// the video bitrate. Saved with the project (<c>settings.export</c> in <c>project.json</c>, written only when it differs
/// from <see cref="Default"/>; a file without it reads as <see cref="Default"/>; a file of D028 — quality, preset, bitrate
/// only — reads as <see cref="ExportProfile.Custom"/> with exactly those values, so it exports as before). The container,
/// the codec (H.264), the pixel format, the colour tags and the audio format stay fixed (<see cref="Export.ExportFormat"/>);
/// the frame rate is always the project's, the output size the canvas or a preset of its aspect ratio (D060 §3,
/// <see cref="Resolution"/>).
/// <para>
/// The positional constructor makes a <see cref="ExportProfile.Custom"/> encoding with libx264 (the D028 meaning of the
/// three values); <see cref="Default"/> is <see cref="ExportProfile.Balanced"/>, which with libx264 gives exactly the command
/// line of <c>Custom(High, Medium, 192)</c>.
/// </para>
/// </summary>
public sealed record ExportEncoding(ExportQuality Quality, ExportSpeedPreset Preset, int AudioBitrateKbps)
{
    /// <summary>Today's output (D023, D028): «Сбалансированный», libx264 — CRF 18, preset medium, AAC 192 kbps.</summary>
    public static ExportEncoding Default { get; } = new(ExportQuality.High, ExportSpeedPreset.Medium, 192) { Profile = ExportProfile.Balanced };

    /// <summary>The AAC bitrates offered, in kbps.</summary>
    public static IReadOnlyList<int> AudioBitratesKbps { get; } = new[] { 128, 160, 192, 256, 320 };

    /// <summary>The video bitrates offered for <see cref="ExportRateControl.Bitrate"/>, in Mbit/s.</summary>
    public static IReadOnlyList<int> VideoBitratesMbps { get; } = new[] { 8, 12, 16, 20, 25, 30, 40, 50, 60, 80, 100 };

    /// <summary>The default of <see cref="VideoBitrateMbps"/>.</summary>
    public const int DefaultVideoBitrateMbps = 40;

    /// <summary>The profile (D059 §4). The positional constructor gives <see cref="ExportProfile.Custom"/>.</summary>
    public ExportProfile Profile { get; init; } = ExportProfile.Custom;

    /// <summary>The encoder the user chose (D059 §3); NVENC falls back to libx264 where it is unavailable.</summary>
    public ExportVideoEncoder Encoder { get; init; } = ExportVideoEncoder.Libx264;

    /// <summary>«Пользовательский»: constant quality or an average bitrate.</summary>
    public ExportRateControl RateControl { get; init; } = ExportRateControl.ConstantQuality;

    /// <summary>«Пользовательский» with <see cref="ExportRateControl.Bitrate"/>: the average video bitrate, Mbit/s.</summary>
    public int VideoBitrateMbps { get; init; } = DefaultVideoBitrateMbps;

    /// <summary>The output's size (D060 §3): <see cref="ExportResolution.Project"/> — the canvas — unless a preset was chosen
    /// that differs from it. Independent of the profile; saved only when not <see cref="ExportResolution.Project"/>.</summary>
    public ExportResolution Resolution { get; init; } = ExportResolution.Project;

    /// <summary>The libx264 <c>-crf</c> value of <see cref="Quality"/> (the «Пользовательский» level).</summary>
    public int Crf => CrfOf(Quality);

    /// <summary>The libx264 <c>-preset</c> name of <see cref="Preset"/> (the «Пользовательский» speed).</summary>
    public string PresetName => PresetNameOf(Preset);

    /// <summary>This encoding with libx264 instead of the chosen encoder (the fallback, D059 §3): the same profile, or for
    /// «Пользовательский» the same quality level, speed and rate control.</summary>
    public ExportEncoding WithLibx264() => this with { Encoder = ExportVideoEncoder.Libx264 };

    public static int CrfOf(ExportQuality quality) => quality switch
    {
        ExportQuality.Maximum => 14,
        ExportQuality.High => 18,
        ExportQuality.Standard => 23,
        ExportQuality.Compact => 28,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown export quality.")
    };

    public static string PresetNameOf(ExportSpeedPreset preset) => preset switch
    {
        ExportSpeedPreset.Fast => "fast",
        ExportSpeedPreset.Medium => "medium",
        ExportSpeedPreset.Slow => "slow",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown export speed preset.")
    };

    /// <summary>A short user-facing reason when a value is not one of the offered ones, or null.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Quality)) return Messages.Encoding_Quality;
        if (!Enum.IsDefined(Preset)) return Messages.Encoding_Preset;
        if (!AudioBitratesKbps.Contains(AudioBitrateKbps)) return string.Format(Messages.Encoding_Bitrate, AudioBitrateKbps);
        if (!Enum.IsDefined(Profile)) return Messages.Encoding_Profile;
        if (!Enum.IsDefined(Encoder)) return Messages.Encoding_Encoder;
        if (!Enum.IsDefined(RateControl)) return Messages.Encoding_RateControl;
        if (!VideoBitratesMbps.Contains(VideoBitrateMbps)) return string.Format(Messages.Encoding_VideoBitrate, VideoBitrateMbps);
        if (!Enum.IsDefined(Resolution)) return Messages.Encoding_Resolution;
        return null;
    }
}
