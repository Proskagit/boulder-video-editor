using Avalonia.Input;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// What the modifier keys held when a clip is pressed on the timeline mean. Ctrl toggles the clip in the selection (no
/// drag, Phase 4); Shift on a clip's edge starts a ripple trim instead of the ordinary trim (Phase 15 Step 15.5, D030 §6,
/// Q3); Alt on a clip's body starts a slip (Step 15.6, D030 §7). Each is read at the press and fixed for the gesture.
/// </summary>
public static class TimelineGestureModifiers
{
    public static bool IsToggle(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control);

    /// <summary>Shift without Ctrl (Ctrl+Shift stays a toggle-select, which never trims).</summary>
    public static bool IsRippleTrim(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Control);

    /// <summary>Alt without Ctrl (Ctrl + Alt stays a toggle-select).</summary>
    public static bool IsSlip(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt) && !modifiers.HasFlag(KeyModifiers.Control);
}
