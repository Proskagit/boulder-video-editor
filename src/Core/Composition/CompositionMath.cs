using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// Where and how one picture layer lands on the canvas (DECISIONS D018). All coordinates are in
/// pixels with the origin at the top-left and Y pointing down.
/// <para>
/// <see cref="SourceRect"/> is the cropped part of the source in source pixels;
/// <see cref="NormalizedSourceRect"/> is the same rectangle in 0..1 units of the source, for a
/// renderer whose decoded frame has a different resolution than <see cref="Source"/>.
/// <see cref="Transform"/> maps the crop-local rectangle [0, SourceRect.Width] × [0, SourceRect.Height]
/// (origin at the crop's top-left) onto the canvas.
/// </para>
/// </summary>
public sealed record LayerGeometry(
    FrameSize Canvas,
    FrameSize Source,
    RectD SourceRect,
    RectD NormalizedSourceRect,
    double FitScale,
    double Scale,
    double RotationDegrees,
    PointD Center,
    Affine2D Transform,
    double Opacity,
    RectD Bounds,
    bool CoversCanvas);

/// <summary>
/// Pure composition geometry (D018), shared by the preview and the export. For every visual clip
/// the operations are applied in this order:
/// <list type="number">
/// <item>crop — normalized insets of the source → source rectangle;</item>
/// <item>fit — the cropped rectangle is scaled uniformly to the largest size that fits inside the
/// canvas (“contain”): <c>fit = min(canvasW / cropW, canvasH / cropH)</c>, centred;</item>
/// <item>scale — uniform, around the picture's centre;</item>
/// <item>rotation — around the picture's centre, positive = clockwise on screen;</item>
/// <item>position — the picture's centre moves by (PositionX, PositionY) canvas pixels from the
/// canvas centre (0, 0 = centred; +X right, +Y down);</item>
/// <item>opacity — a multiplier 0..1 for the whole layer.</item>
/// </list>
/// As a matrix: <c>M = T(canvasW/2 + PositionX, canvasH/2 + PositionY) · R(rotation) · S(fit · scale) · T(−cropW/2, −cropH/2)</c>.
/// </summary>
public static class CompositionMath
{
    /// <summary>Layout of a picture of <paramref name="source"/> pixels with <paramref name="visual"/>
    /// on <paramref name="canvas"/>. Throws for invalid sizes or properties (the edit service and the
    /// project loader never let such values into the model).</summary>
    public static LayerGeometry Layout(FrameSize canvas, FrameSize source, VisualProperties visual)
    {
        if (!canvas.IsValid) throw new ArgumentOutOfRangeException(nameof(canvas), "The canvas must have a positive size.");
        if (!source.IsValid) throw new ArgumentOutOfRangeException(nameof(source), "The source must have a positive size.");
        if (ClipPropertyValidator.ValidateVisual(visual, allowCrop: true) is { } error) throw new ArgumentException(error, nameof(visual));

        // 1. crop
        var crop = visual.Crop;
        var normalized = new RectD(crop.Left, crop.Top, 1 - crop.Left - crop.Right, 1 - crop.Top - crop.Bottom);
        var sourceRect = new RectD(crop.Left * source.Width, crop.Top * source.Height,
            normalized.Width * source.Width, normalized.Height * source.Height);
        if (!(sourceRect.Width > 0) || !(sourceRect.Height > 0))
            throw new ArgumentException("The crop leaves nothing of the source.", nameof(visual));

        // 2. fit ("contain"): the tighter of the two axis ratios, decided by cross-multiplication
        //    so the choice itself doesn't depend on rounding.
        var fit = canvas.Width * sourceRect.Height <= canvas.Height * sourceRect.Width
            ? canvas.Width / sourceRect.Width
            : canvas.Height / sourceRect.Height;

        // 3.–5. scale, rotation, position
        var center = new PointD(canvas.Width / 2.0 + visual.PositionX, canvas.Height / 2.0 + visual.PositionY);
        var transform = Affine2D.Translation(center.X, center.Y)
            .Compose(Affine2D.Rotation(visual.RotationDegrees))
            .Compose(Affine2D.Scaling(fit * visual.Scale))
            .Compose(Affine2D.Translation(-sourceRect.Width / 2, -sourceRect.Height / 2));

        return new LayerGeometry(canvas, source, sourceRect, normalized, fit, visual.Scale, visual.RotationDegrees, center,
            transform, visual.Opacity, Bounds(transform, sourceRect.Width, sourceRect.Height),
            Covers(canvas, source, visual, transform, sourceRect));
    }

    /// <summary>
    /// Transform of a text layer: text has no source picture and is not fitted. The renderer lays the
    /// text out at the given font size (canvas pixels), centres the resulting box on the local origin
    /// and applies this transform: <c>T(canvasW/2 + PositionX, canvasH/2 + PositionY) · R(rotation) · S(scale)</c>.
    /// </summary>
    public static Affine2D TextTransform(FrameSize canvas, VisualProperties visual)
    {
        if (!canvas.IsValid) throw new ArgumentOutOfRangeException(nameof(canvas), "The canvas must have a positive size.");
        if (ClipPropertyValidator.ValidateVisual(visual, allowCrop: false) is { } error) throw new ArgumentException(error, nameof(visual));

        return Affine2D.Translation(canvas.Width / 2.0 + visual.PositionX, canvas.Height / 2.0 + visual.PositionY)
            .Compose(Affine2D.Rotation(visual.RotationDegrees))
            .Compose(Affine2D.Scaling(visual.Scale));
    }

    private static RectD Bounds(Affine2D m, double width, double height)
    {
        Span<PointD> corners = stackalloc PointD[]
        {
            m.Apply(new PointD(0, 0)), m.Apply(new PointD(width, 0)),
            m.Apply(new PointD(0, height)), m.Apply(new PointD(width, height))
        };
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in corners)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// True only when the transformed picture provably contains the whole canvas.
    /// Rotations by multiples of 90° are decided exactly in rational arithmetic from the model values
    /// (a picture that ends exactly on the canvas edge covers it; one a fraction of a pixel short does
    /// not). Other angles are decided by mapping the canvas corners back into the picture with a
    /// safety margin of 10⁻⁶ of the picture size — orders of magnitude above double rounding — so a
    /// "covers" answer is never a rounding artefact; borderline cases answer "does not cover".
    /// </summary>
    private static bool Covers(FrameSize canvas, FrameSize source, VisualProperties v, Affine2D transform, RectD sourceRect)
    {
        if (Affine2D.QuarterTurns(v.RotationDegrees) is { } quarters)
        {
            ExactRational R(double x) => ExactRational.FromDouble(x);
            ExactRational cw = canvas.Width, ch = canvas.Height;
            var w = (1 - R(v.Crop.Left) - R(v.Crop.Right)) * source.Width;
            var h = (1 - R(v.Crop.Top) - R(v.Crop.Bottom)) * source.Height;
            var k = ExactRational.Min(cw / w, ch / h) * R(v.Scale);
            var (dw, dh) = quarters % 2 == 0 ? (w * k, h * k) : (h * k, w * k);
            var two = (ExactRational)2;
            var x = cw / two + R(v.PositionX);
            var y = ch / two + R(v.PositionY);
            return x - dw / two <= 0 && x + dw / two >= cw && y - dh / two <= 0 && y + dh / two >= ch;
        }

        var inverse = transform.Invert();
        var margin = 1e-6 * Math.Max(1.0, Math.Max(sourceRect.Width, sourceRect.Height));
        foreach (var corner in new[] { new PointD(0, 0), new PointD(canvas.Width, 0), new PointD(0, canvas.Height), new PointD(canvas.Width, canvas.Height) })
        {
            var local = inverse.Apply(corner);
            if (local.X < margin || local.X > sourceRect.Width - margin || local.Y < margin || local.Y > sourceRect.Height - margin)
                return false;
        }
        return true;
    }
}
