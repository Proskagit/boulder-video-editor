using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// The values a user may give the project settings (D028, refined in Step 13.3). They apply to changes made by the
/// user; a loaded project keeps whatever valid values its file has (a positive canvas, any positive rational rate — e.g.
/// a rate fixed by the first video, D007, or a canvas written by hand).
/// </summary>
public static class ProjectSettingsRules
{
    /// <summary>The shortest canvas side, in pixels.</summary>
    public const int MinCanvasSide = 64;

    /// <summary>The longest canvas side, in pixels (UHD, DCI 4K and portrait 4K fit).</summary>
    public const int MaxCanvasSide = 4096;

    /// <summary>The largest canvas area, in pixels: 36 864 macroblocks of 16 × 16, the frame-size limit of H.264 level 5.1.</summary>
    public const long MaxCanvasArea = 36_864L * 16 * 16;

    /// <summary>The project frame rates a user may choose: 23.976, 24, 25, 29.97, 30, 50, 59.94, 60.</summary>
    public static IReadOnlyList<FrameRate> SelectableFrameRates { get; } = new[]
    {
        FrameRate.Ntsc24, FrameRate.Fps24, FrameRate.Fps25, FrameRate.Ntsc30,
        FrameRate.Fps30, new FrameRate(50, 1), FrameRate.Ntsc60, FrameRate.Fps60
    };

    /// <summary>A short user-facing reason why <paramref name="width"/> × <paramref name="height"/> can't be the canvas,
    /// or null when it can: both sides even (H.264 4:2:0), each within
    /// [<see cref="MinCanvasSide"/>, <see cref="MaxCanvasSide"/>], the area at most <see cref="MaxCanvasArea"/>.</summary>
    public static string? CanvasError(int width, int height)
    {
        if (width < MinCanvasSide || height < MinCanvasSide || width > MaxCanvasSide || height > MaxCanvasSide)
            return $"The frame size must be between {MinCanvasSide} and {MaxCanvasSide} pixels on each side.";
        if (width % 2 != 0 || height % 2 != 0)
            return "The frame width and height must be even.";
        if ((long)width * height > MaxCanvasArea)
            return $"The frame size {width} × {height} is too large (at most {MaxCanvasArea} pixels).";
        return null;
    }

    /// <summary>
    /// The factor a canvas change multiplies the values kept in canvas pixels by (D028 Step 13.4, CS-1 B):
    /// <c>min(newWidth / oldWidth, newHeight / oldHeight)</c> — how D018's "contain" fits the old canvas into the new one,
    /// the axis chosen by exact cross-multiplication as in <c>CompositionMath.Layout</c>.
    /// </summary>
    /// <summary>The font size of a newly created text clip on a canvas <paramref name="canvasHeight"/> pixels high (D029 §3 →
    /// D030 §9, Q14): <c>48 × canvasHeight / 1080</c>, exact in <c>double</c> — 48 at 1080, 32 at 720, 64 at 1440, 96 at 2160,
    /// 85.333… on a 1080 × 1920 portrait canvas. Only for new text: an existing clip's size follows a canvas change by
    /// <see cref="ContainFactor"/> (D028), a different operation.</summary>
    public static double NewTextFontSize(int canvasHeight) => 48.0 * canvasHeight / 1080;

    public static double ContainFactor(int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        if (oldWidth <= 0 || oldHeight <= 0 || newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(oldWidth), "Canvas sizes must be positive.");
        return (long)newWidth * oldHeight <= (long)newHeight * oldWidth
            ? (double)newWidth / oldWidth
            : (double)newHeight / oldHeight;
    }

    /// <summary>True when a user may choose <paramref name="rate"/> as the project frame rate.</summary>
    public static bool IsSelectableFrameRate(FrameRate rate) => SelectableFrameRates.Contains(rate);
}
