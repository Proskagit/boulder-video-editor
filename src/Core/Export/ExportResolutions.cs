using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Export;

/// <summary>
/// The export resolution presets (D060 §3) — the one place their sizes are computed.
/// <para>
/// A preset fixes the output's short side (4K UHD 2160, Full HD 1080, HD 720, SD 480); the long side keeps the canvas's
/// aspect ratio, rounded to the nearest even number (H.264 4:2:0), a half up — the rule of
/// <see cref="ProjectSettingsRules.SuggestedCanvas"/>. 16:9 gives 3840 × 2160, 1920 × 1080, 1280 × 720 and 854 × 480;
/// 9:16 the same sizes standing (Full HD 1080 × 1920); a square 1080 × 1080. A long side beyond
/// <see cref="ProjectSettingsRules.MaxCanvasSide"/> or an area beyond <see cref="ProjectSettingsRules.MaxCanvasArea"/>
/// (H.264 level 5.1, which libx264 and NVENC both encode) is reduced, the aspect ratio kept, until the size is one
/// <see cref="ProjectSettingsRules.CanvasError"/> accepts — a wide 4K is then the largest such size, never a distorted one.
/// </para>
/// <para>
/// The rounding of a side to an even number changes the aspect ratio by less than one pixel in the long side (854 × 480 is
/// 16:9 within 0.08 %); the export maps the canvas onto the output exactly (<see cref="CompositionDrawPlan.Stretch"/>), so
/// nothing is cropped and no border appears, and the difference is invisible.
/// </para>
/// </summary>
public static class ExportResolutions
{
    /// <summary>The presets in the order offered, largest first.</summary>
    public static IReadOnlyList<ExportResolution> Presets { get; } =
        new[] { ExportResolution.Uhd4K, ExportResolution.FullHd, ExportResolution.Hd, ExportResolution.Sd };

    /// <summary>The short side of <paramref name="resolution"/>; null for <see cref="ExportResolution.Project"/>.</summary>
    public static int? ShortSide(ExportResolution resolution) => resolution switch
    {
        ExportResolution.Uhd4K => 2160,
        ExportResolution.FullHd => 1080,
        ExportResolution.Hd => 720,
        ExportResolution.Sd => 480,
        ExportResolution.Project => null,
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown export resolution.")
    };

    /// <summary>The output size of <paramref name="resolution"/> for <paramref name="canvas"/>: the canvas itself for
    /// <see cref="ExportResolution.Project"/>, otherwise the preset at the canvas's aspect ratio.</summary>
    public static FrameSize SizeFor(ExportResolution resolution, FrameSize canvas)
    {
        if (ShortSide(resolution) is not { } shortSide || !canvas.IsValid) return canvas;

        long shortCanvas = Math.Min(canvas.Width, canvas.Height), longCanvas = Math.Max(canvas.Width, canvas.Height);
        long shortOut = shortSide;
        var longOut = NearestEven(shortOut * longCanvas, shortCanvas);
        // Too large for H.264 level 5.1 / the canvas limits: the largest size of the same aspect ratio that fits.
        while (longOut > ProjectSettingsRules.MaxCanvasSide || longOut * shortOut > ProjectSettingsRules.MaxCanvasArea)
        {
            shortOut -= 2;
            longOut = NearestEven(shortOut * longCanvas, shortCanvas);
        }
        return canvas.Width >= canvas.Height
            ? new FrameSize((int)longOut, (int)shortOut)
            : new FrameSize((int)shortOut, (int)longOut);
    }

    /// <summary>The output size of <paramref name="encoding"/> for <paramref name="canvas"/>.</summary>
    public static FrameSize SizeFor(ExportEncoding encoding, FrameSize canvas) => SizeFor(encoding.Resolution, canvas);

    /// <summary>The preset whose size is exactly the canvas, or null when none is (e.g. 2560 × 1440).</summary>
    public static ExportResolution? PresetMatching(FrameSize canvas) =>
        Presets.Cast<ExportResolution?>().FirstOrDefault(p => SizeFor(p!.Value, canvas) == canvas);

    /// <summary><paramref name="encoding"/> with a preset of exactly the canvas's size stored as
    /// <see cref="ExportResolution.Project"/> — the same output; the project file then keeps no resolution, and a later
    /// change of the canvas is followed.</summary>
    public static ExportEncoding Normalize(ExportEncoding encoding, FrameSize canvas) =>
        encoding.Resolution != ExportResolution.Project && SizeFor(encoding.Resolution, canvas) == canvas
            ? encoding with { Resolution = ExportResolution.Project }
            : encoding;

    /// <summary>The even number nearest to <paramref name="numerator"/> / <paramref name="denominator"/>, a half up.</summary>
    private static long NearestEven(long numerator, long denominator) => 2 * ((numerator + denominator) / (2 * denominator));
}
