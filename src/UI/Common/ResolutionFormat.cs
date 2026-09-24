using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.UI.Common;

/// <summary>Picture size as the user sees it: the display size (after the file's orientation),
/// falling back to the coded size while the display size is not known yet.</summary>
public static class ResolutionFormat
{
    public static string? Display(MediaMetadata m) =>
        m is { DisplayWidth: { } w, DisplayHeight: { } h } ? $"{w}×{h}"
        : m is { Width: { } cw, Height: { } ch } ? $"{cw}×{ch}"
        : null;

    /// <summary>"Rotated 90°" etc. for a right-angle orientation; null for none or unknown.</summary>
    public static string? Orientation(MediaMetadata m) =>
        m.DisplayRotation is 90 or 180 or 270 ? $"Rotated {m.DisplayRotation}°" : null;
}
