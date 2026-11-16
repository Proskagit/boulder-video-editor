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
/// <para>D061 amendment 1 (format v21): <see cref="From"/> / <see cref="To"/> — the followed part of the video, in its source
/// time (like the target, so Trim, Split, Slip and Speed keep it on the same frames); null — from the clip's start / to its
/// end. Outside it the clip keeps its own framing; the camera glides in and out at the edges (never a jump).</para>
/// </summary>
public sealed record FollowSettings(MediaTime? TargetTime = null, NormalizedPoint? TargetPoint = null,
    FollowSmoothness Smoothness = FollowSmoothness.Normal, MediaTime? From = null, MediaTime? To = null)
{
    public bool HasTarget => TargetTime is not null && TargetPoint is not null;

    /// <summary>Whether Follow covers the whole clip (no start, no end of its own).</summary>
    public bool IsWholeClip => From is null && To is null;

    /// <summary>The followed part of a clip showing <c>[sourceIn, sourceOut)</c> of the video; null when it lies outside the
    /// clip (a split half without it).</summary>
    public (MediaTime From, MediaTime To)? RangeIn(MediaTime sourceIn, MediaTime sourceOut)
    {
        var from = From is { } f && f > sourceIn ? f : sourceIn;
        var to = To is { } t && t < sourceOut ? t : sourceOut;
        return to > from ? (from, to) : null;
    }

    /// <summary>The smoothing's standard deviation.</summary>
    public static MediaTime SigmaOf(FollowSmoothness smoothness) => smoothness switch
    {
        FollowSmoothness.Soft => MediaTime.FromSeconds(1.0),
        FollowSmoothness.Fast => MediaTime.FromSeconds(0.25),
        _ => MediaTime.FromSeconds(0.5)
    };
}
