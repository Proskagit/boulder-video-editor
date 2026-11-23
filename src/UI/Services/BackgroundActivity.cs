namespace AiVideoEditor.UI.Services;

/// <summary>D078 amendment 1: a background analysis running now — what (for the toolbar's badge), how far (0..1; null when it
/// cannot be told) and how many items it covers (videos, clips, sounds).</summary>
public readonly record struct BackgroundActivity(string Label, double? Progress, int Items = 1);
