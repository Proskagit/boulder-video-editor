using System.Collections.Immutable;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// Which of the model's masks a candidate offers (D034 §1 as amended 2026-10-15, §8): a mask up to 2 % of the frame is
/// offered as before; a larger one up to 20 % only when the model is confident (score ≥ 0.9) — a large hold in a
/// close-up; anything larger is hidden (the whole wall). Offered masks in the order of their score (highest first, ties by
/// the model's order). The first one is shown; a single-point candidate cycles through them; a multi-point candidate
/// shows only the first. When every mask is hidden there is no candidate at all.
/// <para>
/// Why: in a close-up a real hold can exceed 2 % of the frame, and the model's best mask for it (score 0.99) was hidden.
/// On E1 every mask over 2 % scores ≤ 0.58 (panels, walls, groups); in the close-up that showed the problem the whole
/// wall is ≈ 52 % (scores up to 0.97) and wall parts of 2–20 % score ≤ 0.58. Both bounds are provisional — from E1 and a
/// few close-ups, not universally confirmed.
/// </para>
/// </summary>
public static class HoldCandidateRules
{
    /// <summary>Masks up to this fraction of the frame are offered whatever their score (the original D034 bound).</summary>
    public const double MaxAreaFraction = 0.02;

    /// <summary>Masks over <see cref="MaxAreaFraction"/> and up to this fraction are offered when confident.</summary>
    public const double MaxConfidentAreaFraction = 0.20;

    /// <summary>The model score a mask over <see cref="MaxAreaFraction"/> needs. Compared in the model's single
    /// precision, so a score of exactly 0.9 passes.</summary>
    public const float MinConfidentScore = 0.9f;

    /// <summary>Whether <paramref name="mask"/> may be offered as a candidate.</summary>
    public static bool IsOffered(HoldMask mask) =>
        mask.Area > 0 && (mask.AreaFraction <= MaxAreaFraction
                          || (mask.AreaFraction <= MaxConfidentAreaFraction && (float)mask.Score >= MinConfidentScore));

    /// <summary>The indices of the visible masks of <paramref name="set"/>, best first.</summary>
    public static ImmutableArray<int> Visible(HoldMaskSet set) =>
        Enumerable.Range(0, set.Masks.Length)
            .Where(i => IsOffered(set.Masks[i]))
            .OrderByDescending(i => set.Masks[i].Score).ThenBy(i => i)
            .ToImmutableArray();

    /// <summary>The mask a candidate shows first, or null when every mask is hidden.</summary>
    public static int? Choose(HoldMaskSet set) => Visible(set) is { Length: > 0 } visible ? visible[0] : null;
}
