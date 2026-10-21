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

/// <summary>A fill transition's colour (D044 §2, <see cref="SolidLayer"/>): <see cref="LocalRect"/> — the canvas in canvas
/// pixels — filled with <see cref="ColorHex"/> through <see cref="DrawOperation.Transform"/> at the opacity.</summary>
public sealed record SolidDraw(Guid ClipId, Affine2D Transform, double Opacity, RectD LocalRect, string ColorHex)
    : DrawOperation(ClipId, Transform, Opacity);

/// <summary>
/// A template (D045): drawn by the renderer from its kind, fields and clock in canvas pixels through
/// <see cref="DrawOperation.Transform"/> (canvas → target). A kind with a treatment (<see cref="TemplateRules.HasTreatment"/>,
/// VHS) first treats everything drawn before it (<see cref="TemplateTreatment"/>), then draws its graphics.
/// </summary>
public sealed record TemplateDraw(Guid ClipId, Affine2D Transform, double Opacity, FrameSize Canvas, TemplateKind Kind,
    TemplateProperties Fields, TemplateClock Clock) : DrawOperation(ClipId, Transform, Opacity)
{
    public bool HasTreatment => TemplateRules.HasTreatment(Kind);
}

/// <summary>
/// The route outline of a picture layer's hold markup (D034 §4–§6), drawn right above the layer's frame with the layer's
/// opacity. Like <see cref="FrameDraw"/> it lives in the layer's crop-local coordinates (source pixels):
/// <see cref="Polygons"/> are the contours there, <see cref="LocalRect"/> is the crop (the outline is clipped to it) and
/// <see cref="Transform"/> maps them to the target. The stroke is <see cref="StrokeWidth"/> canvas pixels —
/// <c>StrokeWidth1080 × short side / 1080</c>, so it does not depend on the clip's scale — i.e.
/// <c>StrokeWidth × CanvasScale</c> target pixels (<see cref="CanvasScale"/>: target pixels per canvas pixel), at least
/// <see cref="MinTargetStroke"/> (0 for the export; the Preview alone keeps a line visible at one screen pixel); centred on
/// each contour, round joins. With a keyline (<see cref="KeylineHex"/> not null) the width is shared: the keyline colour
/// across the whole width, the core colour over its middle half — a keyline of a quarter on each side.
/// <para>
/// <see cref="PolygonOpacities"/> (D036): each contour's own opacity on this frame, by index into <see cref="Polygons"/>,
/// on top of <see cref="DrawOperation.Opacity"/>; empty — every contour at 1. The contours of one opacity are drawn as a
/// group with that opacity (keyline and core together, so a fading hold's keyline never shows through its core and its
/// overlapping segments never darken); contours at 0 are left out of <see cref="Polygons"/> altogether.
/// </para>
/// </summary>
public sealed record OutlineDraw(Guid ClipId, Affine2D Transform, double Opacity, ImmutableArray<ImmutableArray<PointD>> Polygons,
    RectD LocalRect, double StrokeWidth, double CanvasScale, string CoreHex, string? KeylineHex, double MinTargetStroke = 0)
    : DrawOperation(ClipId, Transform, Opacity)
{
    /// <summary>The core's share of the stroke when there is a keyline.</summary>
    public const double CoreShare = 0.5;

    public ImmutableArray<double> PolygonOpacities { get; init; } = ImmutableArray<double>.Empty;

    /// <summary>The opacity of contour <paramref name="index"/>.</summary>
    public double PolygonOpacity(int index) => PolygonOpacities.IsDefaultOrEmpty ? 1.0 : PolygonOpacities[index];

    /// <summary>The contours grouped by their opacity (D036), in order of each group's first contour; one group at 1
    /// without animation.</summary>
    public IEnumerable<(double Opacity, ImmutableArray<ImmutableArray<PointD>> Polygons)> OpacityGroups()
    {
        if (PolygonOpacities.IsDefaultOrEmpty)
        {
            yield return (1.0, Polygons);
            yield break;
        }
        var order = new List<double>();
        var groups = new Dictionary<double, ImmutableArray<ImmutableArray<PointD>>.Builder>();
        for (var i = 0; i < Polygons.Length; i++)
        {
            var opacity = PolygonOpacities[i];
            if (!groups.TryGetValue(opacity, out var group))
            {
                groups[opacity] = group = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
                order.Add(opacity);
            }
            group.Add(Polygons[i]);
        }
        foreach (var opacity in order) yield return (opacity, groups[opacity].ToImmutable());
    }
}

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
            layers.SelectMany(layer => LayerOperations(canvas, canvasToTarget, layer)).ToImmutableArray());
    }

    /// <summary>The operations of one layer: <see cref="Operation"/>, then the layer's route outline, if any (D034).</summary>
    public static IEnumerable<DrawOperation> LayerOperations(FrameSize canvas, Affine2D canvasToTarget, ResolvedLayer resolved, double minTargetStroke = 0)
    {
        yield return Operation(canvas, canvasToTarget, resolved);
        if (resolved.Layer is PictureLayer { Span.Outline: { } } picture && resolved.Frame is { } frame &&
            Outline(canvas, canvasToTarget, picture, new FrameSize(frame.Width, frame.Height), minTargetStroke) is { } outline)
            yield return outline;
    }

    /// <summary>
    /// The route outline of <paramref name="picture"/> (D034 §6): each normalized source point → the crop-local
    /// coordinates of the layer's D018 geometry (drawn through its transform, like the frame); the geometry from the source
    /// size, or — unknown — from
    /// <paramref name="decodedSize"/>, like the frame itself. An animated outline (D036) carries the opacity of each
    /// contour shown on the layer's frame (<see cref="PictureLayer.OutlineOpacities"/>); the hidden ones are left out. Null
    /// without an outline or when no contour is shown.
    /// </summary>
    public static OutlineDraw? Outline(FrameSize canvas, Affine2D canvasToTarget, PictureLayer picture, FrameSize decodedSize, double minTargetStroke = 0)
    {
        if (picture.Span.Outline is not { } outline) return null;
        var geometry = picture.Geometry ?? CompositionMath.Layout(canvas, decodedSize, picture.Span.Visual);
        var (w, h) = (geometry.Source.Width, geometry.Source.Height);
        var crop = geometry.SourceRect;
        var m = picture.Motion.Apply(canvas, geometry.Transform);   // the outline moves with its frame (D044 §2)

        var opacities = picture.OutlineOpacities;
        var animated = !opacities.IsDefault && opacities.Length == outline.Polygons.Length;
        var polygons = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(outline.Polygons.Length);
        var shown = ImmutableArray.CreateBuilder<double>(animated ? outline.Polygons.Length : 0);
        for (var i = 0; i < outline.Polygons.Length; i++)
        {
            if (animated && !(opacities[i] > 0)) continue;
            polygons.Add(outline.Polygons[i].Select(p => new PointD(p.X * w - crop.X, p.Y * h - crop.Y)).ToImmutableArray());
            if (animated) shown.Add(opacities[i]);
        }
        if (polygons.Count == 0) return null;

        return new OutlineDraw(picture.ClipId, canvasToTarget.Compose(m), picture.Opacity, polygons.ToImmutable(),
            new RectD(0, 0, crop.Width, crop.Height), StrokeWidthOn(canvas, outline.StrokeWidth1080),
            Math.Sqrt(Math.Abs(canvasToTarget.Determinant)), outline.ColorHex,
            outline.Keyline ? RouteColor.KeylineHex(outline.ColorHex) : null, minTargetStroke)
        {
            // Every contour at 1 draws exactly as an outline without animation.
            PolygonOpacities = animated && shown.Any(o => o < 1) ? shown.ToImmutable() : ImmutableArray<double>.Empty
        };
    }

    /// <summary>D034 §4: the stroke in canvas (= output) pixels — <c>StrokeWidth1080 × short side / 1080</c>.</summary>
    public static double StrokeWidthOn(FrameSize canvas, double strokeWidth1080) =>
        strokeWidth1080 * Math.Min(canvas.Width, canvas.Height) / 1080.0;

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
                return new TextDraw(text.ClipId, canvasToTarget.Compose(text.Motion.Apply(canvas, text.Transform)), text.Opacity, text.Text);

            case SolidLayer solid:
                return new SolidDraw(solid.ClipId, canvasToTarget, solid.Opacity, new RectD(0, 0, canvas.Width, canvas.Height), solid.ColorHex);

            case TemplateLayer template:
                return new TemplateDraw(template.ClipId, canvasToTarget, 1.0, canvas, template.Kind, template.Span.Fields, template.Clock);

            case PictureLayer picture:
            {
                var frame = resolved.Frame ?? throw new ArgumentException("A picture layer needs its decoded frame.", nameof(resolved));
                var geometry = picture.Geometry
                    ?? CompositionMath.Layout(canvas, new FrameSize(frame.Width, frame.Height), picture.Span.Visual);
                var n = geometry.NormalizedSourceRect;
                return new FrameDraw(picture.ClipId, canvasToTarget.Compose(picture.Motion.Apply(canvas, geometry.Transform)), picture.Opacity,
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

    /// <summary>As <see cref="Render(CompositionDrawPlan, Span{byte}, int)"/>, cancelled with <paramref name="ct"/> where
    /// the rasterizer may wait (D043: an outlined text's glyphs prepared on the UI thread); a cancelled render throws
    /// <see cref="OperationCanceledException"/>.</summary>
    void Render(CompositionDrawPlan plan, Span<byte> target, int stride, CancellationToken ct) => Render(plan, target, stride);
}
