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
    /// operation (null: the layer is skipped, e.g. no bitmap yet).</summary>
    public static void Paint(DrawingContext context, CompositionDrawPlan plan, Func<FrameDraw, IImage?> image)
    {
        if (!(plan.CanvasBounds.Width > 0) || !(plan.CanvasBounds.Height > 0)) return;

        var canvasRect = RenderConversions.ToRect(plan.CanvasBounds);
        context.FillRectangle(Background, canvasRect);
        using (context.PushClip(canvasRect))
        {
            foreach (var op in plan.Operations)
            {
                using (context.PushTransform(RenderConversions.ToMatrix(op.Transform)))
                using (context.PushOpacity(op.Opacity))
                {
                    switch (op)
                    {
                        case FrameDraw frame:
                            if (image(frame) is { } bitmap)
                                context.DrawImage(bitmap, RenderConversions.ToRect(frame.FrameSourceRect), RenderConversions.ToRect(frame.LocalRect));
                            break;
                        case TextDraw text:
                            DrawText(context, text.Text);
                            break;
                        case PlaceholderDraw placeholder:
                            DrawPlaceholder(context, placeholder);
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
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily), text.FontSize, new ImmutableSolidColorBrush(Color.Parse(text.ColorHex)));
        formatted.MaxTextWidth = Math.Max(formatted.WidthIncludingTrailingWhitespace, 1);
        formatted.TextAlignment = text.Alignment switch
        {
            Core.Entities.TextAlignment.Left => Avalonia.Media.TextAlignment.Left,
            Core.Entities.TextAlignment.Right => Avalonia.Media.TextAlignment.Right,
            _ => Avalonia.Media.TextAlignment.Center
        };
        return formatted;
    }

    private static void DrawText(DrawingContext context, TextProperties text)
    {
        var formatted = Layout(text);
        context.DrawText(formatted, new Point(-formatted.MaxTextWidth / 2, -formatted.Height / 2));
    }

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
