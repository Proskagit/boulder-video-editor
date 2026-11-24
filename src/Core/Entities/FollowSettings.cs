using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;

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
/// D061 amendment 1 (format v22): one followed part of a video clip — <see cref="From"/> / <see cref="To"/> in the video's
/// source time (null: from the clip's start / to its end; so Trim, Split, Slip and Speed keep it on the same frames) and the
/// person it follows: <see cref="TargetTime"/> / <see cref="TargetPoint"/>, where the user picked them (a source time and a
/// point in normalized coordinates of the display-oriented frame); null — not picked: the only person of the part, or with
/// several the most prominent one at its start. Each part is independent (its own person: two routes, two climbers).
/// <para><see cref="OffsetX"/> / <see cref="OffsetY"/> (format v23): where the person stands in the frame — canvas pixels from
/// the canvas centre (+X right, +Y down; 0, 0 — the centre), like the clip's Position. The camera keeps covering the canvas,
/// so near the picture's edge the person can stand nearer the centre than asked.</para>
/// <para><see cref="Keyframes"/> (format v24, D063 §4): the part's own transform keyframes — the offset and the scale per
/// source time; independent of the clip's keyframes.</para>
/// <para><see cref="DynamicScale"/> / <see cref="ScaleShare"/> (format v30, D080): «Динамический масштаб» — the picture's scale
/// follows the climber so he keeps <see cref="ScaleShare"/> of the frame's height (null: the automatic share,
/// <see cref="People.FollowZoom"/>); the part's own scale, independent of the clip's (the clip's scale and keyframes are not
/// used for the picture inside the part, a part's keyframes keep their place X / Y). Off — the scale as before D080.</para>
/// </summary>
public sealed record FollowPart(MediaTime? From = null, MediaTime? To = null, MediaTime? TargetTime = null, NormalizedPoint? TargetPoint = null,
    double OffsetX = 0, double OffsetY = 0, TransformKeyframes? Keyframes = null, bool DynamicScale = false, double? ScaleShare = null)
{
    /// <summary>Whether <see cref="ScaleShare"/> is within the limits (null — automatic).</summary>
    public bool HasValidScaleShare => ScaleShare is not { } s || (double.IsFinite(s) && s >= People.FollowZoom.MinShare && s <= People.FollowZoom.MaxShare);

    /// <summary>D063 §4 (format v24): the part's framing per source time — the climber's place in the frame
    /// (<see cref="TransformValue.X"/> / <see cref="TransformValue.Y"/>, as <see cref="OffsetX"/> / <see cref="OffsetY"/>) and
    /// the picture's scale while the part follows; null — the static offset and the clip's own scale.</summary>
    public TransformValue FramingAt(MediaTime time, double clipScale) =>
        Keyframes is { } keys ? keys.At(time) : new TransformValue(OffsetX, OffsetY, clipScale);

    public bool HasTarget => TargetTime is not null && TargetPoint is not null;

    /// <summary>The part within a clip showing <c>[sourceIn, sourceOut)</c> of the video; null when it lies outside the clip.</summary>
    public (MediaTime From, MediaTime To)? RangeIn(MediaTime sourceIn, MediaTime sourceOut)
    {
        var from = From is { } f && f > sourceIn ? f : sourceIn;
        var to = To is { } t && t < sourceOut ? t : sourceOut;
        return to > from ? (from, to) : null;
    }

    /// <summary>Whether source time <paramref name="time"/> lies in the part (an open side reaches any time).</summary>
    public bool Contains(MediaTime time) => (From is not { } f || time >= f) && (To is not { } t || time < t);
}

/// <summary>
/// D061: a video clip's automatic Follow — the visible part of the picture moves with the chosen person, keeping the clip's
/// own scale (no automatic zoom, no editable camera path). D061 amendment 1: Follow is made of independent
/// <see cref="Parts"/> (sorted by start, never overlapping; one part with no start and no end is Follow on the whole clip);
/// outside them the clip keeps its own framing, at their edges the camera glides (never a jump), between two parts that
/// touch it passes from one person to the other. <see cref="Smoothness"/> is the clip's. Immutable; equal by value.
/// </summary>
public sealed record FollowSettings
{
    public FollowSettings(ImmutableArray<FollowPart> parts, FollowSmoothness smoothness = FollowSmoothness.Normal)
    {
        Parts = parts.IsDefault ? ImmutableArray<FollowPart>.Empty : parts;
        Smoothness = smoothness;
    }

    public ImmutableArray<FollowPart> Parts { get; init; }

    public FollowSmoothness Smoothness { get; init; }

    /// <summary>Follow on the whole clip (one open part), optionally with the person picked.</summary>
    public static FollowSettings Whole(FollowSmoothness smoothness = FollowSmoothness.Normal, MediaTime? targetTime = null,
        NormalizedPoint? targetPoint = null, bool dynamicScale = false) =>
        new(ImmutableArray.Create(new FollowPart(TargetTime: targetTime, TargetPoint: targetPoint, DynamicScale: dynamicScale)), smoothness);

    /// <summary>Follow made of one part.</summary>
    public static FollowSettings Of(FollowPart part, FollowSmoothness smoothness = FollowSmoothness.Normal) =>
        new(ImmutableArray.Create(part), smoothness);

    /// <summary>Whether Follow covers the whole clip (one part, no start, no end of its own).</summary>
    public bool IsWholeClip => Parts.Length == 1 && Parts[0].From is null && Parts[0].To is null;

    /// <summary>The index of the part containing source time <paramref name="time"/>, or -1.</summary>
    public int IndexAt(MediaTime time)
    {
        for (var i = 0; i < Parts.Length; i++)
            if (Parts[i].Contains(time)) return i;
        return -1;
    }

    /// <summary>The start part <paramref name="index"/> may take when it opens to the clip's start: the previous part's end
    /// (a part beyond the clip's start — after a Split or a Trim — still bounds it), or null (open).</summary>
    public MediaTime? OpenStart(int index) => index > 0 ? Parts[index - 1].To : null;

    /// <summary>The end part <paramref name="index"/> may take when it opens to the clip's end: the next part's start, or null.</summary>
    public MediaTime? OpenEnd(int index) => index + 1 < Parts.Length ? Parts[index + 1].From : null;

    /// <summary>These parts with <paramref name="index"/> replaced (null removes it); null when no part is left.</summary>
    public FollowSettings? WithPart(int index, FollowPart? part)
    {
        var parts = part is null ? Parts.RemoveAt(index) : Parts.SetItem(index, part);
        return parts.IsEmpty ? null : this with { Parts = Sorted(parts) };
    }

    /// <summary>These parts and <paramref name="part"/>, sorted.</summary>
    public FollowSettings WithAdded(FollowPart part) => this with { Parts = Sorted(Parts.Add(part)) };

    /// <summary>Parts sorted by start (an open start first).</summary>
    public static ImmutableArray<FollowPart> Sorted(ImmutableArray<FollowPart> parts) =>
        parts.OrderBy(p => p.From?.Ticks ?? long.MinValue).ToImmutableArray();

    /// <summary>Whether <paramref name="parts"/> are sorted, non-empty each and never overlap (an open end only on the last,
    /// an open start only on the first).</summary>
    public static bool AreValid(ImmutableArray<FollowPart> parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (!p.HasValidScaleShare) return false;
            if (p.From is { } f && p.To is { } t && f >= t) return false;
            if (i == 0) continue;
            var before = parts[i - 1];
            if (before.To is not { } end || p.From is not { } start || start < end) return false;
        }
        return true;
    }

    public bool Equals(FollowSettings? other) =>
        other is not null && Smoothness == other.Smoothness && Parts.SequenceEqual(other.Parts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Smoothness);
        foreach (var part in Parts) hash.Add(part);
        return hash.ToHashCode();
    }

    /// <summary>The smoothing's standard deviation.</summary>
    public static MediaTime SigmaOf(FollowSmoothness smoothness) => smoothness switch
    {
        FollowSmoothness.Soft => MediaTime.FromSeconds(1.0),
        FollowSmoothness.Fast => MediaTime.FromSeconds(0.25),
        _ => MediaTime.FromSeconds(0.5)
    };
}
