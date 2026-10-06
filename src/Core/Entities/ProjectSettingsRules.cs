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

    /// <summary>True when a user may choose <paramref name="rate"/> as the project frame rate.</summary>
    public static bool IsSelectableFrameRate(FrameRate rate) => SelectableFrameRates.Contains(rate);
}
