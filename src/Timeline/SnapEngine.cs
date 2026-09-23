using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Timeline;

/// <summary>Pure snapping math: pick the (candidate, target) pair with the smallest
/// distance within tolerance. Ties go to the earlier candidate, then the earlier target,
/// so results are deterministic.</summary>
public static class SnapEngine
{
    public static SnapResult Find(IReadOnlyList<MediaTime> candidates, IEnumerable<MediaTime> targets, MediaTime tolerance)
    {
        if (tolerance < MediaTime.Zero) return SnapResult.None;

        var targetList = targets.Distinct().OrderBy(t => t.Ticks).ToList();
        var best = SnapResult.None;
        var bestDistance = long.MaxValue;

        foreach (var candidate in candidates)
        {
            foreach (var target in targetList)
            {
                var distance = Math.Abs(target.Ticks - candidate.Ticks);
                if (distance <= tolerance.Ticks && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new SnapResult(true, target - candidate, target);
                }
            }
        }

        return best;
    }
}
