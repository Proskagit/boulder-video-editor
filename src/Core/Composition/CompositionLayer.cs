using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>One visible layer at a point in time (D018), as returned by
/// <see cref="PlaybackSnapshot.LayersAt"/>: bottom to top, renderer-neutral.</summary>
public abstract record CompositionLayer(Guid TrackId, Guid ClipId, double Opacity);

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
    public bool OccludesBelow => Span.Status == SpanStatus.Video && Opacity == 1 && Geometry is { CoversCanvas: true };
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
