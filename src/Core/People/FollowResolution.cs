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

/// <summary>
/// A clip's Follow resolved against its video's people (D061 §3): the followed person (<see cref="Trajectory"/>), the camera
/// path for the clip's current framing (<see cref="Path"/>), the state and the stretches where the person is lost — the one
/// computation the snapshot (Preview and export), the export's preflight and the Inspector share.
/// </summary>
public sealed record FollowResolution(FollowState State, FollowTrajectory? Trajectory, FollowPath? Path, FollowUnavailable Unavailable)
{
    /// <summary>A loss shown to the user: at least half a second.</summary>
    public static readonly MediaTime ReportedGap = MediaTime.FromSeconds(0.5);

    /// <summary>The share of the clip's analysed frames where the person is seen.</summary>
    public double SeenFraction { get; init; } = 1;

    /// <summary>The stretches of the clip where the person is lost (at least <see cref="ReportedGap"/>), in source time.</summary>
    public System.Collections.Immutable.ImmutableArray<FollowGap> Gaps { get; init; } = System.Collections.Immutable.ImmutableArray<FollowGap>.Empty;

    /// <summary>Resolves <paramref name="clip"/>'s Follow over <paramref name="tracks"/> (its video's people) for a picture of
    /// <paramref name="source"/> pixels on <paramref name="canvas"/>.</summary>
    public static FollowResolution For(VideoClip clip, FollowSettings follow, PersonTracks tracks, FrameSize canvas, FrameSize source)
    {
        var (from, to) = (clip.SourceIn, clip.SourceOut);
        FollowTrajectory? trajectory;
        if (follow.HasTarget)
        {
            trajectory = FollowTarget.Resolve(tracks, follow.TargetTime!.Value, follow.TargetPoint!.Value);
            if (trajectory is null) return new FollowResolution(FollowState.TargetNotFound, null, null, FollowUnavailable.NoTarget);
        }
        else if (FollowTarget.AutoTrack(tracks, from, to) is { } only)
            trajectory = FollowTarget.Resolve(tracks, only);
        else
            return new FollowResolution(FollowState.NeedsTarget, null, null, FollowUnavailable.NoTarget);

        var seen = trajectory.SeenFraction(from, to);
        var gaps = trajectory.Gaps(from, to, ReportedGap);
        if (!trajectory.Points.Any(p => p.IsSeen && p.Time >= from && p.Time <= to))
            return new FollowResolution(FollowState.TargetNotFound, trajectory, null, FollowUnavailable.NoTarget) { SeenFraction = 0, Gaps = gaps };

        var visual = VisualProperties.Of(clip) ?? VisualProperties.Default;
        var (path, reason) = FollowCamera.Solve(trajectory, canvas, source, visual, follow.Smoothness);
        return new FollowResolution(path is null ? FollowState.Unavailable : FollowState.Following, trajectory, path, reason)
        {
            SeenFraction = seen,
            Gaps = gaps
        };
    }
}
