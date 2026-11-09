using Avalonia;
using Avalonia.Input;

namespace AiVideoEditor.UI.Common;

public enum TimelineWheelAction
{
    None,
    Zoom,
    Horizontal,
    Vertical
}

/// <summary>
/// D057 §3.11: the mouse wheel over the timeline's tracks — the wheel scrolls horizontally, Ctrl+wheel zooms (unchanged),
///, Shift+wheel scrolls the tracks vertically; a touchpad's horizontal scroll scrolls horizontally. The track
/// headers keep their own vertical scroll (they have no horizontal extent).
/// </summary>
public static class TimelineWheel
{
    public static TimelineWheelAction ActionFor(KeyModifiers modifiers, Vector delta)
    {
        if (delta.X == 0 && delta.Y == 0) return TimelineWheelAction.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) return delta.Y == 0 ? TimelineWheelAction.None : TimelineWheelAction.Zoom;
        if (modifiers.HasFlag(KeyModifiers.Shift)) return TimelineWheelAction.Vertical;
        return TimelineWheelAction.Horizontal;
    }

    /// <summary>The notches to scroll horizontally: a touchpad's horizontal movement, else the wheel's turn.</summary>
    public static double HorizontalDelta(Vector delta) => delta.X != 0 ? delta.X : delta.Y;

    /// <summary>The notches to scroll vertically with Shift: the wheel's turn (some systems report Shift+wheel as
    /// horizontal).</summary>
    public static double VerticalDelta(Vector delta) => delta.Y != 0 ? delta.Y : delta.X;
}
