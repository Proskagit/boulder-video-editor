using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Export;

/// <summary>The built-in export presets (D076 Q2 A) — they replace D059's profiles.</summary>
public enum ExportPresetKind
{
    /// <summary>«Быстрый»: Full HD, H.264 (plays everywhere), NVENC p2 / libx264 veryfast at CRF 18's level, AAC 192.</summary>
    Fast,
    /// <summary>«Средний»: 2K, AV1, NVENC p5 / SVT-AV1 at libx264 medium CRF 18's quality, AAC 192 (D076 §3: AV1 saves ~30 %
    /// against H.264 NVENC at this quality).</summary>
    Medium,
    /// <summary>«Высокий»: 4K, H.264, NVENC p7 multipass / libx264 slow at slow CRF 16's quality, AAC 256.</summary>
    High,
    /// <summary>«Максимальный»: the project's own size, H.264, NVENC p7 multipass / libx264 slow at CRF 14's quality, AAC 320 — a
    /// new project's export (D074 §6, D076 Q6; at the highest quality HEVC / AV1 make no smaller file and no faster export).</summary>
    Maximum
}

/// <summary>
/// D076: the built-in export presets — each a complete <see cref="ExportEncoding"/> (codec, container, encoder, resolution,
/// quality, speed, rate control, audio), and the rule that tells which preset a project's settings are. A preset's resolution
/// is a ceiling (Q4 A): applied to a canvas it never enlarges the picture (<see cref="ExportResolutions.Ceiling"/>). Every
/// preset prefers NVENC; where the machine has no NVENC for the codec the processor encodes the same codec (D059 §3).
/// </summary>
public static class ExportPresets
{
    public static ExportEncoding Fast { get; } = new(ExportQuality.High, ExportSpeedPreset.VeryFast, 192)
    {
        Codec = ExportVideoCodec.H264, Encoder = ExportVideoEncoder.Nvenc, Resolution = ExportResolution.FullHd
    };

    public static ExportEncoding Medium { get; } = new(ExportQuality.High, ExportSpeedPreset.Medium, 192)
    {
        Codec = ExportVideoCodec.Av1, Encoder = ExportVideoEncoder.Nvenc, Resolution = ExportResolution.Qhd
    };

    public static ExportEncoding High { get; } = new(ExportQuality.VeryHigh, ExportSpeedPreset.Slow, 256)
    {
        Codec = ExportVideoCodec.H264, Encoder = ExportVideoEncoder.Nvenc, Resolution = ExportResolution.Uhd4K
    };

    public static ExportEncoding Maximum { get; } = new(ExportQuality.Maximum, ExportSpeedPreset.Slow, 320)
    {
        Codec = ExportVideoCodec.H264, Encoder = ExportVideoEncoder.Nvenc, Resolution = ExportResolution.Project
    };

    /// <summary>The built-in presets in the order offered.</summary>
    public static IReadOnlyList<ExportPresetKind> All { get; } =
        new[] { ExportPresetKind.Fast, ExportPresetKind.Medium, ExportPresetKind.High, ExportPresetKind.Maximum };

    public static ExportEncoding Of(ExportPresetKind kind) => kind switch
    {
        ExportPresetKind.Fast => Fast,
        ExportPresetKind.Medium => Medium,
        ExportPresetKind.High => High,
        ExportPresetKind.Maximum => Maximum,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown export preset.")
    };

    /// <summary><paramref name="preset"/> applied to <paramref name="canvas"/>: its resolution a ceiling (Q4 A), and
    /// <paramref name="container"/> kept when given and able to hold the preset's codec (the user's file type is not changed by
    /// choosing a preset; AV1 never goes into MOV — then MP4).</summary>
    public static ExportEncoding Apply(ExportEncoding preset, FrameSize canvas, ExportContainer? container = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var applied = ExportResolutions.Ceiling(preset, canvas);
        if (container is { } keep) applied = applied with { Container = keep };
        return ExportEncoding.Holds(applied.Container, applied.Codec) ? applied : applied with { Container = ExportContainer.Mp4 };
    }

    /// <summary>Whether <paramref name="settings"/> are <paramref name="preset"/> on <paramref name="canvas"/> — the same output:
    /// the preset applied with its ceiling, the settings by the size they really give (a size chosen by hand above the canvas is
    /// no preset); the container is the file's type and not part of a built-in preset; with constant quality the bitrate field
    /// does not count.</summary>
    public static bool Matches(ExportEncoding settings, ExportEncoding preset, FrameSize canvas, bool compareContainer = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preset);
        var a = Comparable(ExportResolutions.Normalize(settings, canvas));
        var b = Comparable(ExportResolutions.Ceiling(preset, canvas)) with { Container = compareContainer ? preset.Container : a.Container };
        return a == b;
    }

    private static ExportEncoding Comparable(ExportEncoding e) =>
        e.RateControl == ExportRateControl.ConstantQuality ? e with { VideoBitrateMbps = ExportEncoding.DefaultVideoBitrateMbps } : e;

    /// <summary>The built-in preset <paramref name="settings"/> are on <paramref name="canvas"/>, or null («Пользовательский»,
    /// or one of the user's own).</summary>
    public static ExportPresetKind? Match(ExportEncoding settings, FrameSize canvas) =>
        All.Cast<ExportPresetKind?>().FirstOrDefault(k => Matches(settings, Of(k!.Value), canvas));
}
