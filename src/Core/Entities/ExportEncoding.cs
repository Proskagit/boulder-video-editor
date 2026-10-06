namespace AiVideoEditor.Core.Entities;

/// <summary>The quality of an export (D028 §7, refined in Step 13.3): a named level, stored by name; each level is one
/// libx264 constant-quality value (<see cref="ExportEncoding.Crf"/>).</summary>
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

/// <summary>The libx264 speed / efficiency preset of an export (D028 §7, refined in Step 13.3).</summary>
public enum ExportSpeedPreset
{
    Fast,
    /// <summary>The fixed preset of Phases 8–12 (D023).</summary>
    Medium,
    Slow
}

/// <summary>
/// How the project is encoded on export (D028, refined in Step 13.3): the quality level, the libx264 preset and the AAC
/// bitrate. Saved with the project (<c>settings.export</c> in <c>project.json</c> v3, written only when it differs
/// from <see cref="Default"/>; a file without it reads as <see cref="Default"/>). The container, the codecs, the pixel
/// format, the colour tags and the audio format stay fixed (<see cref="Export.ExportFormat"/>). The output size and
/// frame rate are always the project's.
/// </summary>
public sealed record ExportEncoding(ExportQuality Quality, ExportSpeedPreset Preset, int AudioBitrateKbps)
{
    /// <summary>Today's output (D023): CRF 18, preset medium, AAC 192 kbps.</summary>
    public static ExportEncoding Default { get; } = new(ExportQuality.High, ExportSpeedPreset.Medium, 192);

    /// <summary>The AAC bitrates offered, in kbps.</summary>
    public static IReadOnlyList<int> AudioBitratesKbps { get; } = new[] { 128, 160, 192, 256, 320 };

    /// <summary>The libx264 <c>-crf</c> value of <see cref="Quality"/>.</summary>
    public int Crf => CrfOf(Quality);

    /// <summary>The libx264 <c>-preset</c> name of <see cref="Preset"/>.</summary>
    public string PresetName => PresetNameOf(Preset);

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
        if (!Enum.IsDefined(Quality)) return "The export quality is unknown.";
        if (!Enum.IsDefined(Preset)) return "The export speed preset is unknown.";
        if (!AudioBitratesKbps.Contains(AudioBitrateKbps)) return $"The audio bitrate {AudioBitrateKbps} kbps is not supported.";
        return null;
    }
}
