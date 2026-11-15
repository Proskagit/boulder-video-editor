using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.ExportBench;

/// <summary>The parts of the harness that follow the app's export API (kept in one place, so the harness can be built
/// against an older commit for a baseline — the D059 E0 baseline used the version of commit 07252bf with only
/// <c>default</c> and <c>x264:QUALITY:PRESET</c>). <c>--encoding</c>:
/// <list type="bullet">
/// <item><c>default</c> — «Сбалансированный», libx264 (the D028 default output);</item>
/// <item><c>x264:QUALITY:PRESET</c> — «Пользовательский» with libx264 (D028 names, e.g. <c>x264:High:Medium</c>);</item>
/// <item><c>profile:PROFILE[:nvenc]</c> — <c>Fast</c>, <c>Balanced</c>, <c>HighQuality</c>, optionally with NVENC;</item>
/// <item><c>custom:ENCODER:QUALITY:PRESET</c>, <c>bitrate:ENCODER:MBPS</c> — «Пользовательский».</item>
/// </list></summary>
internal static class Encodings
{
#if BASELINE
    public static ExportEncoding Parse(string? spec)
    {
        if (spec is null or "default") return ExportEncoding.Default;
        var parts = spec.Split(':');
        if (parts[0] == "x264" && parts.Length == 3)
            return new ExportEncoding(Enum.Parse<ExportQuality>(parts[1]), Enum.Parse<ExportSpeedPreset>(parts[2]), 192);
        throw new ArgumentException($"Unknown encoding '{spec}' (a baseline build knows only default and x264:QUALITY:PRESET).");
    }

    public static IExportEncoder Encoder(IFfmpegLocator locator) => new FfmpegExportEncoder(locator, NullLogger<FfmpegExportEncoder>.Instance);

    public static ExportService Service(IFfmpegLocator locator, IVideoDecoder video, IAudioDecoder audio, IExportEncoder encoder,
        Func<ICompositionRasterizer> rasterizers, int? rasterizerCount = null) =>
        new(locator, video, audio, encoder, rasterizers, NullLogger<ExportService>.Instance);
#else
    public static ExportEncoding Parse(string? spec)
    {
        if (spec is null or "default") return ExportEncoding.Default;
        var p = spec.Split(':');
        ExportVideoEncoder Encoder(string s) => s == "nvenc" ? ExportVideoEncoder.Nvenc : ExportVideoEncoder.Libx264;
        return p[0] switch
        {
            "x264" when p.Length == 3 => new ExportEncoding(Enum.Parse<ExportQuality>(p[1]), Enum.Parse<ExportSpeedPreset>(p[2]), 192),
            "profile" => ExportEncoding.Default with { Profile = Enum.Parse<ExportProfile>(p[1]), Encoder = p.Length > 2 ? Encoder(p[2]) : ExportVideoEncoder.Libx264 },
            "custom" => new ExportEncoding(Enum.Parse<ExportQuality>(p[2]), Enum.Parse<ExportSpeedPreset>(p[3]), 192) { Encoder = Encoder(p[1]) },
            "bitrate" => new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192)
            {
                Encoder = Encoder(p[1]), RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = int.Parse(p[2])
            },
            _ => throw new ArgumentException($"Unknown encoding '{spec}'.")
        };
    }

    public static IExportEncoder Encoder(IFfmpegLocator locator) => new FfmpegExportEncoder(locator, NullLogger<FfmpegExportEncoder>.Instance,
        new FfmpegEncoderCapabilities(locator, NullLogger<FfmpegEncoderCapabilities>.Instance));

    public static ExportService Service(IFfmpegLocator locator, IVideoDecoder video, IAudioDecoder audio, IExportEncoder encoder,
        Func<ICompositionRasterizer> rasterizers, int? rasterizerCount = null) =>
        new(locator, video, audio, encoder, rasterizers, NullLogger<ExportService>.Instance,
            new ExportPipelineSettings { Rasterizers = rasterizerCount });
#endif
}
