using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>The state of a clip's Follow once its video's people are known (D061 §3, §7).</summary>
public enum FollowState
{
    /// <summary>The camera follows the person (it may be lost on some stretches — <see cref="FollowResolution.Gaps"/>).</summary>
    Following,

    /// <summary>Several people and none picked: Follow waits for the user's pick (the clip keeps its own framing).</summary>
    NeedsTarget,

    /// <summary>The picked point holds nobody, or the person is never seen in the clip.</summary>
    TargetNotFound,

    /// <summary>The camera cannot move: <see cref="FollowResolution.Unavailable"/> says why.</summary>
    Unavailable
}

/// <summary>One followed part of a clip resolved (D061 amendment 1): its range in the clip (source time), its person, its
/// state; <see cref="AutoChosen"/> — nobody picked and several people: the most prominent one at the part's start.</summary>
public sealed record FollowPartResolution(int Index, MediaTime From, MediaTime To, FollowState State, FollowTrajectory? Trajectory,
    FollowUnavailable Unavailable)
{
    public bool AutoChosen { get; init; }
    public double SeenFraction { get; init; } = 1;
    public ImmutableArray<FollowGap> Gaps { get; init; } = ImmutableArray<FollowGap>.Empty;
}

/// <summary>
/// A clip's Follow resolved against its video's people (D061 §3; amendment 1: part by part, each with its own person): the
/// camera path for the clip's current framing (<see cref="Path"/>), the state and the stretches where a person is lost — the
/// one computation the snapshot (Preview and export), the export's preflight and the Inspector share.
/// </summary>
public sealed record FollowResolution(FollowState State, FollowTrajectory? Trajectory, FollowPath? Path, FollowUnavailable Unavailable)
{
    /// <summary>A loss shown to the user: at least half a second.</summary>
    public static readonly MediaTime ReportedGap = MediaTime.FromSeconds(0.5);

    /// <summary>The share of the followed parts' analysed frames where their person is seen.</summary>
    public double SeenFraction { get; init; } = 1;

    /// <summary>The stretches of the followed parts where their person is lost (at least <see cref="ReportedGap"/>), in source time.</summary>
    public ImmutableArray<FollowGap> Gaps { get; init; } = ImmutableArray<FollowGap>.Empty;

    /// <summary>Every part of the clip's Follow that lies in the clip, resolved.</summary>
    public ImmutableArray<FollowPartResolution> Parts { get; init; } = ImmutableArray<FollowPartResolution>.Empty;

    /// <summary>The first part of the clip that does not follow (no climber chosen / found, no room), or null.</summary>
    public FollowPartResolution? Problem => Parts.FirstOrDefault(p => p.State != FollowState.Following);

    /// <summary>The resolved part containing source time <paramref name="time"/>, or null.</summary>
    public FollowPartResolution? PartAt(MediaTime time) => Parts.FirstOrDefault(p => time >= p.From && time < p.To);

    /// <summary>The person followed at source time <paramref name="time"/> (null outside the parts or unresolved).</summary>
    public FollowTrajectory? TrajectoryAt(MediaTime time) => PartAt(time)?.Trajectory;

    /// <summary>Resolves <paramref name="clip"/>'s Follow over <paramref name="tracks"/> (its video's people) for a picture of
    /// <paramref name="source"/> pixels on <paramref name="canvas"/>.</summary>
    public static FollowResolution For(VideoClip clip, FollowSettings follow, PersonTracks tracks, FrameSize canvas, FrameSize source)
    {
        var visual = VisualProperties.Of(clip) ?? VisualProperties.Default;
        var resolved = ImmutableArray.CreateBuilder<FollowPartResolution>();
        var paths = new List<(FollowPath Path, MediaTime From, MediaTime To)>();
        var unavailable = FollowUnavailable.None;
        for (var i = 0; i < follow.Parts.Length; i++)
        {
            var part = follow.Parts[i];
            if (part.RangeIn(clip.SourceIn, clip.SourceOut) is not { } range) continue;
            var (from, to) = range;
            FollowTrajectory? trajectory = null;
            var auto = false;
            if (part.HasTarget) trajectory = FollowTarget.Resolve(tracks, part.TargetTime!.Value, part.TargetPoint!.Value);
            else if (FollowTarget.AutoTrack(tracks, from, to) is { } only) trajectory = FollowTarget.Resolve(tracks, only);
            else if (FollowTarget.Prominent(tracks, from, to) is { } prominent)
            {
                trajectory = FollowTarget.Resolve(tracks, prominent);
                auto = true;
            }
            if (trajectory is null)
            {
                resolved.Add(new FollowPartResolution(i, from, to, part.HasTarget ? FollowState.TargetNotFound : FollowState.NeedsTarget, null,
                    FollowUnavailable.NoTarget) { SeenFraction = 0 });
                continue;
            }
            var gaps = trajectory.Gaps(from, to, ReportedGap);
            if (!trajectory.Points.Any(p => p.IsSeen && p.Time >= from && p.Time <= to))
            {
                resolved.Add(new FollowPartResolution(i, from, to, FollowState.TargetNotFound, trajectory, FollowUnavailable.NoTarget)
                    { SeenFraction = 0, Gaps = gaps, AutoChosen = auto });
                continue;
            }
            var (path, reason) = FollowCamera.Solve(trajectory, canvas, source, visual, follow.Smoothness);
            if (path is null) unavailable = reason;
            else paths.Add((path, from, to));
            resolved.Add(new FollowPartResolution(i, from, to, path is null ? FollowState.Unavailable : FollowState.Following, trajectory, reason)
                { SeenFraction = trajectory.SeenFraction(from, to), Gaps = gaps, AutoChosen = auto });
        }
        var parts = resolved.ToImmutable();
        if (parts.IsEmpty)
            return new FollowResolution(FollowState.Unavailable, null, null, FollowUnavailable.OutsideRange) { SeenFraction = 0 };

        var following = parts.Where(p => p.State == FollowState.Following).ToList();
        var length = following.Sum(p => (double)(p.To - p.From).Ticks);
        var seen = length > 0 ? following.Sum(p => p.SeenFraction * (p.To - p.From).Ticks) / length : 0;
        var allGaps = following.SelectMany(p => p.Gaps).OrderBy(g => g.Start).ToImmutableArray();
        if (following.Count == 0)
        {
            var first = parts[0];
            return new FollowResolution(first.State, first.Trajectory, null, first.State == FollowState.Unavailable ? unavailable : first.Unavailable)
                { SeenFraction = 0, Gaps = first.Gaps, Parts = parts };
        }
        var combined = FollowCamera.Combine(paths, clip.SourceIn, clip.SourceOut, visual.PositionX, visual.PositionY);
        return new FollowResolution(FollowState.Following, following[0].Trajectory, combined, FollowUnavailable.None)
        {
            SeenFraction = seen,
            Gaps = allGaps,
            Parts = parts
        };
    }
}
