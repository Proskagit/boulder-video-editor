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

    /// <summary>Every (candidate, target) pair within tolerance, nearest first — ties ordered as <see cref="Find"/> breaks
    /// them —, one per distinct offset: for a caller that must skip a target (D037 §2: a snap that would make an overlap).</summary>
    public static IEnumerable<SnapResult> FindAll(IReadOnlyList<MediaTime> candidates, IEnumerable<MediaTime> targets, MediaTime tolerance)
    {
        if (tolerance < MediaTime.Zero) return Enumerable.Empty<SnapResult>();
        var targetList = targets.Distinct().OrderBy(t => t.Ticks).ToList();
        return candidates
            .SelectMany((candidate, ci) => targetList.Select((target, ti) => (Distance: Math.Abs(target.Ticks - candidate.Ticks), ci, ti,
                Result: new SnapResult(true, target - candidate, target))))
            .Where(x => x.Distance <= tolerance.Ticks)
            .OrderBy(x => x.Distance).ThenBy(x => x.ci).ThenBy(x => x.ti)
            .Select(x => x.Result)
            .DistinctBy(r => r.Offset.Ticks)
            .ToList();
    }
}
