using AiVideoEditor.Core.Common;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Pixel ↔ time math for the timeline panel. <c>double</c> is used only for screen
/// coordinates; every time that goes back to the domain is converted to ticks and
/// then snapped to the exact frame grid (<see cref="XToFrameTime"/>).
/// </summary>
public static class TimelineCoordinateMapper
{
    public const double MinPixelsPerSecond = 2;

    /// <summary>Most zoomed-in view: one frame spans this many pixels.</summary>
    public const double MaxPixelsPerFrame = 40;

    /// <summary>Ruler labels need roughly this much room.</summary>
    public const double MinRulerSpacingPixels = 70;

    private static readonly long[] RulerIntervalsTicks =
    {
        TimeSpan.TicksPerSecond / 10, TimeSpan.TicksPerSecond / 4, TimeSpan.TicksPerSecond / 2,
        TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond,
        10 * TimeSpan.TicksPerSecond, 15 * TimeSpan.TicksPerSecond, 30 * TimeSpan.TicksPerSecond,
        60 * TimeSpan.TicksPerSecond, 120 * TimeSpan.TicksPerSecond, 300 * TimeSpan.TicksPerSecond,
        600 * TimeSpan.TicksPerSecond, 1800 * TimeSpan.TicksPerSecond, 3600 * TimeSpan.TicksPerSecond
    };

    public static double TimeToX(MediaTime time, double pixelsPerSecond) =>
        time.Ticks * pixelsPerSecond / TimeSpan.TicksPerSecond;

    /// <summary>Screen distance → time, rounded to the nearest tick (not yet on the frame grid).</summary>
    public static MediaTime XToTime(double x, double pixelsPerSecond) =>
        new((long)Math.Round(x / pixelsPerSecond * TimeSpan.TicksPerSecond));

    /// <summary>Screen position → nearest frame boundary, never before zero.</summary>
    public static MediaTime XToFrameTime(double x, double pixelsPerSecond, FrameRate rate)
    {
        var time = XToTime(Math.Max(0, x), pixelsPerSecond);
        return time.SnapToFrame(rate);
    }

    public static double MaxPixelsPerSecond(FrameRate rate) => rate.ToDouble() * MaxPixelsPerFrame;

    public static double ClampZoom(double pixelsPerSecond, FrameRate rate) =>
        Math.Clamp(pixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond(rate));

    /// <summary>Horizontal scroll offset that keeps <paramref name="anchor"/> at
    /// <paramref name="anchorViewportX"/> pixels from the left edge of the viewport.</summary>
    public static double ScrollOffsetForAnchor(MediaTime anchor, double anchorViewportX, double pixelsPerSecond) =>
        Math.Max(0, TimeToX(anchor, pixelsPerSecond) - anchorViewportX);

    /// <summary>Smallest "nice" ruler interval whose ticks are at least
    /// <see cref="MinRulerSpacingPixels"/> apart.</summary>
    public static long RulerIntervalTicks(double pixelsPerSecond)
    {
        foreach (var interval in RulerIntervalsTicks)
            if (interval * pixelsPerSecond / TimeSpan.TicksPerSecond >= MinRulerSpacingPixels)
                return interval;
        return RulerIntervalsTicks[^1];
    }
}
