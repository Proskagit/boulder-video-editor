namespace AiVideoEditor.UI.Services;

/// <summary>D078 amendments 1, 4: one background analysis in the toolbar's queue — what, of which file (<see cref="Detail"/>),
/// how far (0..1; null when it cannot be told) and whether it runs now (the others wait).</summary>
public readonly record struct BackgroundActivity(string Label, double? Progress, int Items = 1, string? Detail = null, bool Running = true);
