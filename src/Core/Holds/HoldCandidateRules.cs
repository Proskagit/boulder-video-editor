using System.Collections.Immutable;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// Which of the model's masks a candidate offers (D034 §1, §8): masks larger than 2 % of the frame are hidden (wall
/// panels, the whole wall); the rest in the order of their score (highest first, ties by the model's order). The first
/// one is shown; a single-point candidate cycles through them; a multi-point candidate shows only the first. When every
/// mask is hidden there is no candidate at all.
/// </summary>
public static class HoldCandidateRules
{
    public const double MaxAreaFraction = 0.02;

    /// <summary>The indices of the visible masks of <paramref name="set"/>, best first.</summary>
    public static ImmutableArray<int> Visible(HoldMaskSet set) =>
        Enumerable.Range(0, set.Masks.Length)
            .Where(i => set.Masks[i].Area > 0 && set.Masks[i].AreaFraction <= MaxAreaFraction)
            .OrderByDescending(i => set.Masks[i].Score).ThenBy(i => i)
            .ToImmutableArray();

    /// <summary>The mask a candidate shows first, or null when every mask is hidden.</summary>
    public static int? Choose(HoldMaskSet set) => Visible(set) is { Length: > 0 } visible ? visible[0] : null;
}
