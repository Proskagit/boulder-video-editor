using AiVideoEditor.Core.Interfaces;
using Avalonia;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// D057 §3.4: the rules of restoring the user's working layout — a window that would open outside every screen opens at the
/// default place; a panel keeps a size between its limits.
/// </summary>
public static class WindowLayout
{
    public const double MinWindowWidth = 1024, MinWindowHeight = 640;
    public const double MinSidePanel = 180, MaxSidePanel = 900;
    public const double MinTimeline = 120, MaxTimeline = 1200;

    /// <summary>How much of the window's title bar must lie on a screen for the stored position to be used.</summary>
    public const int VisibleTitle = 80;

    /// <summary>Whether <paramref name="placement"/> may be used on <paramref name="screens"/> (their working areas, in pixels;
    /// <paramref name="scaling"/> the desktop's scale): its top strip must show on one of them.</summary>
    public static bool IsOnScreen(WindowPlacement placement, IReadOnlyList<PixelRect> screens, double scaling)
    {
        if (placement.Width < 1 || placement.Height < 1) return false;
        var width = (int)Math.Round(placement.Width * Math.Max(scaling, 0.1));
        var title = new PixelRect(placement.X, placement.Y, Math.Max(1, width), 30);
        foreach (var screen in screens)
        {
            var visible = screen.Intersect(title);
            if (visible.Width >= Math.Min(VisibleTitle, title.Width) && visible.Height > 0) return true;
        }
        return false;
    }

    public static double Window(double size, double minimum) => double.IsFinite(size) ? Math.Max(minimum, size) : minimum;

    public static double SidePanel(double? width, double fallback) =>
        width is { } w && double.IsFinite(w) ? Math.Clamp(w, MinSidePanel, MaxSidePanel) : fallback;

    public static double Timeline(double? height, double fallback) =>
        height is { } h && double.IsFinite(h) ? Math.Clamp(h, MinTimeline, MaxTimeline) : fallback;
}
