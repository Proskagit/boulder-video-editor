using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// A layer of <see cref="PlaybackSnapshot.LayersAt"/> with what is drawn for it: the decoded source frame
/// of a <see cref="PictureLayer"/> (required), nothing for a <see cref="TextLayer"/>.
/// </summary>
public readonly record struct ResolvedLayer(CompositionLayer Layer, DecodedFrame? Frame);

/// <summary>
/// One thing to draw (D018/D023). The renderer pushes <see cref="Transform"/> (layer-local → target) and
/// <see cref="Opacity"/>, then draws the operation in local coordinates. Renderers add their own kinds on
/// top of these two (the Preview's placeholders); the shared composition contract is only
/// <see cref="FrameDraw"/> and <see cref="TextDraw"/>.
/// </summary>
public abstract record DrawOperation(Guid ClipId, Affine2D Transform, double Opacity);

/// <summary>
/// Part of a decoded frame: <see cref="FrameSourceRect"/> (in the frame's own pixels — the layer's
/// <c>NormalizedSourceRect</c>, i.e. the crop) drawn into <see cref="LocalRect"/> = the crop-local rectangle
/// <c>[0, cropW] × [0, cropH]</c> in source pixels. The frame's pixels are BGRA with straight (not
/// premultiplied) alpha — video is opaque, images may carry alpha.
/// </summary>
public sealed record FrameDraw(Guid ClipId, Affine2D Transform, double Opacity, RectD LocalRect, DecodedFrame Frame, RectD FrameSourceRect)
    : DrawOperation(ClipId, Transform, Opacity);

/// <summary>
/// A text layer. Text box geometry (the rule every renderer follows, D018/D021): the text is laid out at
/// <see cref="TextProperties.FontSize"/> local units without wrapping, one line per line break; the box is
/// as wide as its widest line (trailing spaces included) and as high as the laid-out lines; each line is
/// aligned inside the box by <see cref="TextProperties.Alignment"/>; the box is centred on the local origin.
/// Glyph shapes, line height and font fallback (a family that isn't installed) belong to the text engine.
/// </summary>
public sealed record TextDraw(Guid ClipId, Affine2D Transform, double Opacity, TextProperties Text)
    : DrawOperation(ClipId, Transform, Opacity);

/// <summary>
/// What is drawn for one composition — shared by the Preview and the export (D018, D023). The target
/// is first filled with <see cref="Background"/> inside <see cref="CanvasBounds"/> (the canvas mapped by
/// <see cref="CanvasToTarget"/>); every operation is then drawn bottom to top, clipped to
/// <see cref="CanvasBounds"/>. The Preview maps the canvas into its control ("contain" viewport); the export
/// draws at the canvas size (<see cref="Affine2D.Identity"/>).
/// </summary>
public sealed record CompositionDrawPlan(FrameSize Canvas, Affine2D CanvasToTarget, RectD CanvasBounds, ImmutableArray<DrawOperation> Operations)
{
    /// <summary>The canvas background (gaps, letterboxing of the pictures): opaque black, BGRA.</summary>
    public static readonly (byte B, byte G, byte R, byte A) Background = (0, 0, 0, 255);

    public static readonly CompositionDrawPlan Empty =
        new(default, Affine2D.Identity, new RectD(0, 0, 0, 0), ImmutableArray<DrawOperation>.Empty);

    /// <summary>Uniform "contain" mapping of the canvas into a target of the given size, centred.</summary>
    public static Affine2D Viewport(FrameSize canvas, double targetWidth, double targetHeight)
    {
        var scale = Math.Min(targetWidth / canvas.Width, targetHeight / canvas.Height);
        return new Affine2D(scale, 0, 0, scale,
            (targetWidth - canvas.Width * scale) / 2, (targetHeight - canvas.Height * scale) / 2);
    }

    /// <summary>The plan for <paramref name="layers"/> (bottom to top, as <see cref="PlaybackSnapshot.LayersAt"/>
    /// returns them) drawn through <paramref name="canvasToTarget"/>.</summary>
    public static CompositionDrawPlan Build(FrameSize canvas, Affine2D canvasToTarget, IEnumerable<ResolvedLayer> layers)
    {
        if (!canvas.IsValid) throw new ArgumentOutOfRangeException(nameof(canvas), "The canvas must have a positive size.");
        return new CompositionDrawPlan(canvas, canvasToTarget, Bounds(canvas, canvasToTarget),
            layers.Select(layer => Operation(canvas, canvasToTarget, layer)).ToImmutableArray());
    }

    /// <summary>The target rectangle of the canvas (for a scale-and-translate <paramref name="canvasToTarget"/>).</summary>
    public static RectD Bounds(FrameSize canvas, Affine2D canvasToTarget) =>
        new(canvasToTarget.Tx, canvasToTarget.Ty, canvas.Width * canvasToTarget.A, canvas.Height * canvasToTarget.D);

    /// <summary>
    /// The operation for one layer. A picture is laid out with its D018 geometry, or — when the source size
    /// is unknown — with the same <see cref="CompositionMath.Layout"/> applied to the decoded frame's size;
    /// the crop is taken from the decoded frame through <c>NormalizedSourceRect</c>, so a frame decoded at
    /// another resolution than the source (the Preview's ≤ 1280 × 720) lands in the same place.
    /// </summary>
    public static DrawOperation Operation(FrameSize canvas, Affine2D canvasToTarget, ResolvedLayer resolved)
    {
        switch (resolved.Layer)
        {
            case TextLayer text:
                return new TextDraw(text.ClipId, canvasToTarget.Compose(text.Transform), text.Opacity, text.Text);

            case PictureLayer picture:
            {
                var frame = resolved.Frame ?? throw new ArgumentException("A picture layer needs its decoded frame.", nameof(resolved));
                var geometry = picture.Geometry
                    ?? CompositionMath.Layout(canvas, new FrameSize(frame.Width, frame.Height), picture.Span.Visual);
                var n = geometry.NormalizedSourceRect;
                return new FrameDraw(picture.ClipId, canvasToTarget.Compose(geometry.Transform), picture.Opacity,
                    new RectD(0, 0, geometry.SourceRect.Width, geometry.SourceRect.Height), frame,
                    new RectD(n.X * frame.Width, n.Y * frame.Height, n.Width * frame.Width, n.Height * frame.Height));
            }

            default:
                throw new ArgumentException($"Unknown layer type {resolved.Layer?.GetType().Name}.", nameof(resolved));
        }
    }
}

/// <summary>
/// Turns a <see cref="CompositionDrawPlan"/> drawn at the canvas size (<see cref="Affine2D.Identity"/>) into
/// pixels: <c>Canvas.Width × Canvas.Height</c> BGRA, straight alpha, every pixel opaque (the background is
/// opaque black). One instance per job, used from one thread at a time; it may hold resources between
/// frames and releases them on <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface ICompositionRasterizer : IDisposable
{
    /// <summary>Renders <paramref name="plan"/> into <paramref name="target"/> (rows of <paramref name="stride"/>
    /// bytes, at least <c>stride · Canvas.Height</c> bytes).</summary>
    void Render(CompositionDrawPlan plan, Span<byte> target, int stride);
}
