using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Draws a <see cref="CompositionDrawPlan"/> with Avalonia's <see cref="DrawingContext"/> — the one drawing
/// routine of the Preview (<see cref="CompositionView"/>, UI thread) and of the export
/// (<see cref="AvaloniaCompositionRasterizer"/>, any thread; D023). It only creates immutable brushes and
/// no <see cref="AvaloniaObject"/>s, so it can run off the UI thread.
/// <para>
/// Background, then clip to the canvas bounds, then per operation bottom to top: push the transform and
/// the opacity and draw — a frame's source rectangle into its local rectangle, text laid out by
/// <see cref="FormattedText"/> (the text box rule of <see cref="TextDraw"/>), a Preview placeholder.
/// </para>
/// </summary>
public static class CompositionPainter
{
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromArgb(
        CompositionDrawPlan.Background.A, CompositionDrawPlan.Background.R, CompositionDrawPlan.Background.G, CompositionDrawPlan.Background.B));
    private static readonly IBrush PlaceholderFill = new ImmutableSolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly IBrush PlaceholderText = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly ImmutableSolidColorBrush PlaceholderBorder = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));

    /// <summary>Draws <paramref name="plan"/>. <paramref name="image"/> supplies the bitmap of a frame
    /// operation (null: the layer is skipped, e.g. no bitmap yet). <paramref name="glyphs"/> supplies the glyph geometry of
    /// an outlined text (D043 variant B) — the caller's own cache, waited for with <paramref name="ct"/> off the UI thread;
    /// required when the plan has an outlined text.</summary>
    public static void Paint(DrawingContext context, CompositionDrawPlan plan, Func<FrameDraw, IImage?> image,
        TextGlyphCache? glyphs = null, CancellationToken ct = default)
    {
        if (!(plan.CanvasBounds.Width > 0) || !(plan.CanvasBounds.Height > 0)) return;

        var canvasRect = RenderConversions.ToRect(plan.CanvasBounds);
        context.FillRectangle(Background, canvasRect);
        using (context.PushClip(canvasRect))
        {
            foreach (var op in plan.Operations)
            {
                using (context.PushTransform(RenderConversions.ToMatrix(op.Transform)))
                // An outline composites its opacity itself, as a group (D036 §4).
                using (context.PushOpacity(op is OutlineDraw ? 1.0 : op.Opacity))
                {
                    switch (op)
                    {
                        case FrameDraw frame:
                            if (image(frame) is { } bitmap)
                                context.DrawImage(bitmap, RenderConversions.ToRect(frame.FrameSourceRect), RenderConversions.ToRect(frame.LocalRect));
                            break;
                        case TextDraw text when text.Text.IsPlain:
                            DrawText(context, text.Text);       // every text before D043: exactly as before
                            break;
                        case TextDraw text:
                            DrawStyledText(context, text.Text, glyphs, ct);
                            break;
                        case SolidDraw solid:          // a fill transition (D044 §2)
                            context.FillRectangle(new ImmutableSolidColorBrush(Color.Parse(solid.ColorHex)), RenderConversions.ToRect(solid.LocalRect));
                            break;
                        case PlaceholderDraw placeholder:
                            DrawPlaceholder(context, placeholder);
                            break;
                        case OutlineDraw outline:
                            DrawOutline(context, outline);
                            break;
                        default:
                            throw new NotSupportedException($"Unknown draw operation {op.GetType().Name}.");
                    }
                }
            }
        }
    }

    /// <summary>The text box rule (<see cref="TextDraw"/>): laid out at its font size (local units; the
    /// transform scales it), no wrapping, box as wide as the widest line incl. trailing spaces, lines aligned
    /// inside the box, box centred on the local origin. A font that isn't installed falls back to Avalonia's
    /// default family.</summary>
    public static FormattedText Layout(TextProperties text)
    {
        var typeface = text.Bold ? new Typeface(text.FontFamily, FontStyle.Normal, FontWeight.Bold) : new Typeface(text.FontFamily);
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, text.FontSize, new ImmutableSolidColorBrush(Color.Parse(text.ColorHex)));
        formatted.MaxTextWidth = Math.Max(formatted.WidthIncludingTrailingWhitespace, 1);
        formatted.TextAlignment = text.Alignment switch
        {
            Core.Entities.TextAlignment.Left => Avalonia.Media.TextAlignment.Left,
            Core.Entities.TextAlignment.Right => Avalonia.Media.TextAlignment.Right,
            _ => Avalonia.Media.TextAlignment.Center
        };
        return formatted;
    }

    /// <summary>The top-left corner of a laid-out text box centred on the local origin.</summary>
    public static Point BoxOrigin(FormattedText layout) => new(-layout.MaxTextWidth / 2, -layout.Height / 2);

    /// <summary>The text box (<see cref="Layout"/>) with what is drawn around it (D043 §2): the background's padding when
    /// the background is on — the Move tool's frame and the placement grid's box.</summary>
    public static Rect DrawnBox(TextProperties text)
    {
        var layout = Layout(text);
        var box = new Rect(BoxOrigin(layout), new Size(layout.MaxTextWidth, layout.Height));
        return text.Background.IsEnabled ? box.Inflate(TextBackground.PaddingFactor * text.FontSize) : box;
    }

    /// <summary>D043 §2–§3: the background, then the outline, then the fill. The fill of an outlined text is its glyph
    /// geometry (the outline's own shape — ClearType fringes and a hinting offset otherwise, Step 27.1); a text without an
    /// outline is filled by <c>DrawText</c> (bold or with a background only).</summary>
    private static void DrawStyledText(DrawingContext context, TextProperties text, TextGlyphCache? glyphs, CancellationToken ct)
    {
        var layout = Layout(text);
        var origin = BoxOrigin(layout);
        if (text.Background.IsEnabled)
        {
            var background = text.Background;
            var box = new Rect(origin, new Size(layout.MaxTextWidth, layout.Height)).Inflate(TextBackground.PaddingFactor * text.FontSize);
            var radius = TextBackground.CornerFactor * text.FontSize;
            context.DrawRectangle(new ImmutableSolidColorBrush(Color.Parse(background.ColorHex), background.Opacity), null, box, radius, radius);
        }

        if (!text.Outline.IsEnabled)
        {
            context.DrawText(layout, origin);
            return;
        }

        var cache = glyphs ?? throw new InvalidOperationException("An outlined text needs a glyph cache (D043, variant B).");
        if (cache.Get(text, ct) is not { } geometry) return;
        var outline = text.Outline;
        // The visible width outside the glyphs is half the pen; the fill drawn over it covers the inner half.
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse(outline.ColorHex)), 2 * outline.Thickness * text.FontSize,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, pen, geometry);
        context.DrawGeometry(new ImmutableSolidColorBrush(Color.Parse(text.ColorHex)), null, geometry);
    }

    private static void DrawText(DrawingContext context, TextProperties text)
    {
        var formatted = Layout(text);
        context.DrawText(formatted, new Point(-formatted.MaxTextWidth / 2, -formatted.Height / 2));
    }

    /// <summary>
    /// The route outline (D034, <see cref="OutlineDraw"/>), in the layer's crop-local coordinates under its transform
    /// already pushed: clipped to the crop rectangle; each contour a closed polyline of segments with round caps (round
    /// joins) — no geometry objects, so it runs off the UI thread like the rest of the painter; with a keyline, every
    /// contour in the keyline colour over the full width first, then every core over the middle half, so the keyline of
    /// one hold never covers the core of another. The contours are drawn per group of one opacity
    /// (<see cref="OutlineDraw.OpacityGroups"/>; one group without animation), each group composited once with the hold's ×
    /// the layer's opacity through an opacity mask (a layer, D036 §4) — the keyline and the core of a fading hold fade
    /// together and its overlapping segments never darken; the keyline-then-core rule holds within a group.
    /// </summary>
    private static void DrawOutline(DrawingContext context, OutlineDraw op)
    {
        var localScale = Math.Sqrt(Math.Abs(op.Transform.Determinant));   // target pixels per local unit
        if (!(localScale > 0) || op.Polygons.IsEmpty) return;
        var fullWidth = Math.Max(op.StrokeWidth * op.CanvasScale, op.MinTargetStroke) / localScale;

        var bounds = RenderConversions.ToRect(op.LocalRect);
        using (context.PushClip(bounds))
        {
            foreach (var (holdOpacity, polygons) in op.OpacityGroups())
            {
                // The group's opacity — the hold's × the layer's (clip opacity, fade, dissolve) — applied once to what the
                // group drew (an opacity mask is a layer; a plain opacity would fade every segment on its own).
                var opacity = holdOpacity * op.Opacity;
                if (!(opacity > 0)) continue;
                using (opacity < 1 ? context.PushOpacityMask(new ImmutableSolidColorBrush(Colors.White, opacity), bounds) : default(DrawingContext.PushedState?))
                {
                    var width = fullWidth;
                    if (op.KeylineHex is { } keyline)
                    {
                        Stroke(context, polygons, Pen(keyline, width));
                        width *= OutlineDraw.CoreShare;
                    }
                    Stroke(context, polygons, Pen(op.CoreHex, width));
                }
            }
        }
    }

    private static void Stroke(DrawingContext context, ImmutableArray<ImmutableArray<PointD>> polygons, ImmutablePen pen)
    {
        foreach (var polygon in polygons)
        {
            for (var i = 0; i < polygon.Length; i++)
            {
                var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Length]);
                context.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
            }
        }
    }

    private static ImmutablePen Pen(string hex, double width) =>
        new(new ImmutableSolidColorBrush(Color.Parse(hex)), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static void DrawPlaceholder(DrawingContext context, PlaceholderDraw op)
    {
        var rect = RenderConversions.ToRect(op.LocalRect);
        var shortSide = Math.Min(rect.Width, rect.Height);
        context.DrawRectangle(PlaceholderFill, new ImmutablePen(PlaceholderBorder, Math.Max(1, shortSide * 0.004)), rect);

        var label = new FormattedText(op.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface.Default, Math.Max(8, shortSide * 0.06), PlaceholderText);
        context.DrawText(label, new Point(rect.Center.X - label.Width / 2, rect.Center.Y - label.Height / 2));
    }
}

/// <summary>Copies <see cref="DecodedFrame"/>s into Avalonia bitmaps. Decoded frames are BGRA with straight
/// alpha (ffmpeg <c>bgra</c>): the bitmap is <see cref="AlphaFormat.Unpremul"/>, so images with transparency
/// blend with the layers below (D018); video frames are opaque either way.</summary>
public static class FrameBitmap
{
    /// <summary><paramref name="bitmap"/> if it has the frame's size, otherwise a new one (the old one is disposed).</summary>
    public static WriteableBitmap Ensure(WriteableBitmap? bitmap, DecodedFrame frame)
    {
        if (bitmap is not null && bitmap.PixelSize.Width == frame.Width && bitmap.PixelSize.Height == frame.Height)
            return bitmap;
        bitmap?.Dispose();
        return new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
    }

    public static void Copy(DecodedFrame frame, WriteableBitmap bitmap)
    {
        using var target = bitmap.Lock();
        // DecodedFrame buffers are array-backed; copy rows straight from the array.
        var source = MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
            ? segment
            : new ArraySegment<byte>(frame.Pixels.ToArray());
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        for (var y = 0; y < frame.Height; y++)
            Marshal.Copy(source.Array!, source.Offset + y * frame.Stride, target.Address + y * target.RowBytes, rowBytes);
    }
}
