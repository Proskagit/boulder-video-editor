namespace AiVideoEditor.Core.Entities;

/// <summary>Timeline frames <c>[Start, End)</c> of one segment of the overlay strip.</summary>
public readonly record struct FrameSpan(long Start, long End)
{
    public long Length => End - Start;
}

/// <summary>
/// The overlay strip of a video clip (D048 §4.1) — a view of existing entities, not a model track: the green
/// <c>[Ps, S)</c> (<c>[Ps, Pe)</c> without a status) and the lilac <c>[S, Pe)</c> of its anchored «Боулдер», the orange
/// <c>[As, Ae)</c> of its Duration-mode hold animation; in timeline frames. Null segments are not shown; no anchored
/// template and no animation → no strip.
/// </summary>
public sealed record OverlayStripState(FrameSpan Clip, Guid? TemplateId, FrameSpan? Green, FrameSpan? Orange, FrameSpan? Lilac);

/// <summary>A strip gesture planned, nothing applied (the drag's preview): the segments it leads to and, when the release
/// would be refused, why (the preview marks it invalid).</summary>
public sealed record OverlayStripPreview(OverlayStripState Strip, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>The strip's handles (D048 §4.2). The green body is not movable, so it has none.</summary>
public enum OverlayStripHandle
{
    /// <summary><c>Ps</c> — the template's head; <c>S</c> and <c>Pe</c> kept.</summary>
    GreenStart,

    /// <summary><c>S</c> — the green ↔ lilac boundary (the status moment); <c>Ps</c> and <c>Pe</c> kept.</summary>
    Status,

    /// <summary><c>Pe</c> — the template's tail; <c>Ps</c> and <c>S</c> kept.</summary>
    LilacEnd,

    /// <summary><c>S</c> and <c>Pe</c> by the same δ; <c>Ps</c> kept.</summary>
    LilacBody,

    /// <summary><c>As</c> — the animation's start (its duration grows / shrinks); <c>Ae</c> (<c>T</c>) kept.</summary>
    OrangeStart,

    /// <summary><c>Ae</c> (<c>T</c>) — the animation's end; <c>As</c> kept.</summary>
    OrangeEnd,

    /// <summary><c>As</c> and <c>Ae</c> by the same δ (the duration kept).</summary>
    OrangeBody
}
