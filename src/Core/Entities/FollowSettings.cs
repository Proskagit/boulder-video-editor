using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

/// <summary>How quickly the Follow camera reacts (D061 §3.5): the length of its non-causal smoothing.</summary>
public enum FollowSmoothness
{
    /// <summary>The calmest camera (σ = 1 s): small moves of the person are not followed.</summary>
    Soft,

    /// <summary>The default (σ = 0.5 s): measured best on the research footage — the camera does not lag and does not shake.</summary>
    Normal,

    /// <summary>A quick camera (σ = 0.25 s) for fast movement.</summary>
    Fast
}

/// <summary>
/// D061 (format v20): a video clip's automatic Follow — the visible part of the picture moves with the chosen person,
/// keeping the clip's own scale (no automatic zoom, no editable camera path). <see cref="TargetTime"/> /
/// <see cref="TargetPoint"/>: where the user picked the person (a source time of the clip's video and a point in normalized
/// coordinates of its display-oriented frame); null — not picked: the only person of the clip is followed, and with
/// several people Follow waits for a pick. Immutable; a clip without Follow has none.
/// </summary>
public sealed record FollowSettings(MediaTime? TargetTime = null, NormalizedPoint? TargetPoint = null,
    FollowSmoothness Smoothness = FollowSmoothness.Normal)
{
    public bool HasTarget => TargetTime is not null && TargetPoint is not null;

    /// <summary>The smoothing's standard deviation.</summary>
    public static MediaTime SigmaOf(FollowSmoothness smoothness) => smoothness switch
    {
        FollowSmoothness.Soft => MediaTime.FromSeconds(1.0),
        FollowSmoothness.Fast => MediaTime.FromSeconds(0.25),
        _ => MediaTime.FromSeconds(0.5)
    };
}
