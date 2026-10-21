using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>One visible layer at a point in time (D018), as returned by
/// <see cref="PlaybackSnapshot.LayersAt"/>: bottom to top, renderer-neutral. <see cref="Motion"/> is a transition's
/// movement of the layer on the canvas (D044 §2: Zoom, Push) on top of its own transform — none outside a zone.</summary>
public abstract record CompositionLayer(Guid TrackId, Guid ClipId, double Opacity)
{
    public CanvasMotion Motion { get; init; } = CanvasMotion.None;
}

/// <summary>
/// A transition's movement of a layer in canvas pixels (D044 §2): a scale by <see cref="Scale"/> about the canvas centre
/// (Zoom), then a shift by <see cref="OffsetX"/> / <see cref="OffsetY"/> (Push, whole pixels). Applied after the layer's
/// own transform, so its position, scale, rotation and crop are kept.
/// </summary>
public readonly record struct CanvasMotion(double Scale, double OffsetX, double OffsetY)
{
    public static readonly CanvasMotion None = new(1, 0, 0);

    public bool IsNone => this == None;

    /// <summary>True when a picture that covered the canvas still covers it: no shift and a scale of at least 1 about
    /// the centre (D044 §2) — so occlusion stays valid.</summary>
    public bool KeepsCoverage => OffsetX == 0 && OffsetY == 0 && Scale >= 1;

    /// <summary>The motion as a canvas → canvas transform.</summary>
    public Affine2D ToAffine(FrameSize canvas)
    {
        var (cx, cy) = (canvas.Width / 2.0, canvas.Height / 2.0);
        return new Affine2D(Scale, 0, 0, Scale, cx - Scale * cx + OffsetX, cy - Scale * cy + OffsetY);
    }

    /// <summary><paramref name="transform"/> (layer → canvas) followed by the motion; exactly
    /// <paramref name="transform"/> without one.</summary>
    public Affine2D Apply(FrameSize canvas, Affine2D transform) => IsNone ? transform : ToAffine(canvas).Compose(transform);
}

/// <summary>
/// A video or image clip. <see cref="Geometry"/> is null when the source's pixel size is unknown
/// (no metadata yet); a renderer then lays the picture out with <see cref="CompositionMath.Layout"/>
/// from the decoded frame's size — the same rule, different input. <see cref="PictureSpan.Status"/>
/// tells whether a frame can be decoded or a placeholder is due (Offline / Unsupported). The layer's
/// <see cref="CompositionLayer.Opacity"/> is the clip's opacity × <see cref="FadeFactor"/> (D025 §2) — exactly the
/// clip's opacity without a fade.
/// </summary>
public sealed record PictureLayer(Guid TrackId, PictureSpan Span, LayerGeometry? Geometry, double FadeFactor = 1.0)
    : CompositionLayer(TrackId, Span.ClipId, Span.Visual.Opacity * FadeFactor)
{
    /// <summary>
    /// True when nothing below can be seen: a decodable video (no alpha) at full opacity whose
    /// transformed picture provably contains the whole canvas. Images may carry alpha and text never
    /// covers the canvas, so neither hides what is below; placeholders are decided by the renderer.
    /// </summary>
    public bool OccludesBelow => Span.Status == SpanStatus.Video && Opacity == 1 && Geometry is { CoversCanvas: true } && Motion.KeepsCoverage;

    /// <summary>The opacity of each contour of <see cref="PictureSpan.Outline"/> on this frame (D036), by hold index, on
    /// top of the layer's <see cref="CompositionLayer.Opacity"/>; default (not set): every contour fully shown — an outline
    /// without animation.</summary>
    public ImmutableArray<double> OutlineOpacities { get; init; }
}

/// <summary>
/// A text clip. The text itself is renderer-neutral (<see cref="Text"/>): the renderer lays it out at
/// <see cref="TextProperties.FontSize"/> canvas pixels, centres the resulting box on the local origin
/// and draws it through <see cref="Transform"/> with <see cref="CompositionLayer.Opacity"/> (the clip's opacity ×
/// <see cref="FadeFactor"/>, D025 §2).
/// </summary>
public sealed record TextLayer(Guid TrackId, TextSpan Span, Affine2D Transform, double FadeFactor = 1.0)
    : CompositionLayer(TrackId, Span.ClipId, Span.Visual.Opacity * FadeFactor)
{
    public TextProperties Text => Span.Text;
}

/// <summary>
/// A template (D045) on its frame: what it draws is a pure function of its kind, its fields and <see cref="Clock"/>, so the
/// Preview and the export draw the same. Never hides what is below it.
/// </summary>
public sealed record TemplateLayer(Guid TrackId, TemplateSpan Span, TemplateClock Clock) : CompositionLayer(TrackId, Span.ClipId, 1.0)
{
    public TemplateKind Kind => Span.Kind;
}

/// <summary>
/// A template's time on one timeline frame (D045 §3): seconds since its clip's start (<see cref="Elapsed"/>) and to its end
/// (<see cref="Remaining"/>, one frame on the last frame); the appearance / disappearance lengths (<see cref="Intro"/>,
/// <see cref="Outro"/>; 0: none — a split's inner edge); the counter (<see cref="Counter"/>, seconds, continuing over a
/// split) and its frame number (<see cref="CounterFrame"/>, the noise's seed); the seconds since «Боулдер»'s status moment
/// (<see cref="SinceStatus"/>, on the same clock; negative before it). All exact functions of whole frames.
/// </summary>
public readonly record struct TemplateClock(double Elapsed, double Remaining, double Intro, double Outro, double Counter, long CounterFrame,
    double SinceStatus = double.PositiveInfinity)
{
    public static TemplateClock At(TemplateSpan span, long frame, FrameRate rate)
    {
        var start = span.TimelineStart.ToFrameFloor(rate);
        var end = span.TimelineEnd.ToFrameFloor(rate);
        double Seconds(long frames) => MediaTime.FromFrame(frames, rate).TotalSeconds;
        var counterFrames = span.CounterStart.ToNearestFrame(rate) + (frame - start);
        // D045 §17: the status moment on the counter's clock, in whole frames — the frame its appearance starts on is the same
        // in the Preview and the export, and a split's parts agree on it.
        var sinceStatus = counterFrames - span.Fields.StatusAt.ToNearestFrame(rate);
        return new TemplateClock(Seconds(frame - start), Seconds(end - frame), Seconds(span.FadeInFrames), Seconds(span.FadeOutFrames),
            span.CounterStart.TotalSeconds + Seconds(frame - start), counterFrames, Seconds(sinceStatus));
    }

    /// <summary>0 → 1 over the appearance (1 without one).</summary>
    public double In => Intro > 0 ? Math.Clamp(Elapsed / Intro, 0, 1) : 1;

    /// <summary>1 → 0 over the disappearance (1 without one).</summary>
    public double Out => Outro > 0 ? Math.Clamp(Remaining / Outro, 0, 1) : 1;
}

/// <summary>
/// The colour of a fill transition (D044 §2: Dip to black, Flash) over its track's layer and everything below it, at
/// <see cref="CompositionLayer.Opacity"/>; the tracks above stay visible. Fully opaque, it hides everything below.
/// </summary>
public sealed record SolidLayer(Guid TrackId, string ColorHex, double Opacity) : CompositionLayer(TrackId, Guid.Empty, Opacity)
{
    public bool OccludesBelow => Opacity >= 1;
}
