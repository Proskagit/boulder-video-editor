using System.Runtime.CompilerServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// The blurred background of a picture (D057 §3.7, changed at the owner's acceptance of 2026-11-10: no zoomed copy of the
/// video behind it). The picture's own edges are carried out into the free canvas — every canvas point is taken back into
/// the picture as it is drawn there (its crop, scale, rotation, position and the camera's motion: the picture's transform)
/// and clamped to the nearest point of the visible picture — then blurred strongly, slightly desaturated and darkened, so
/// the bars carry the colours at the picture's edges as a soft light and nothing of the subject is repeated.
/// <list type="number">
/// <item>The visible source rectangle is reduced to a small grid (<see cref="GridSide"/> on its long side) by averaging
/// <see cref="CellSamples"/>² points at fixed normalized positions in each cell — the same points whatever size the frame
/// was decoded at (the Preview's reduced frames, the export's full ones), and cheap even at 4K.</item>
/// <item>The canvas-aspect copy (<see cref="SizeFor"/>) samples that grid bilinearly at each of its pixels' canvas points
/// through the inverse of the picture's transform, clamped to the visible picture (the edge extension).</item>
/// <item>Three box passes of a radius the copy's long side / <see cref="RadiusDivisor"/> (≈ a Gaussian), then
/// <see cref="Desaturation"/> % towards the luma and <see cref="Brightness"/> / 256 of the brightness.</item>
/// </list>
/// Pure and deterministic; the painter stretches the copy over the canvas with smoothing. The same copy for the Preview and
/// the export (one operation, <see cref="CompositionDrawPlan.BlurredBackground"/>), cached per decoded frame and placement.
/// </summary>
public static class BackgroundBlur
{
    /// <summary>The reduction: the copy's long side is the canvas's divided by this (within [<see cref="MinSide"/>,
    /// <see cref="MaxSide"/>]).</summary>
    public const int Reduction = 16;

    public const int MinSide = 16;

    public const int MaxSide = 240;

    /// <summary>The long side of the source grid.</summary>
    public const int GridSide = 48;

    /// <summary>Sample points per cell side of the source grid.</summary>
    public const int CellSamples = 6;

    /// <summary>The box radius is the copy's long side divided by this (the owner's check of 0.38.1: 12 was too soft, about
    /// 30 % less blur — the Gaussian's width goes with <c>2r + 1</c>: 21 → 15 at 1080 × 1920).</summary>
    public const int RadiusDivisor = 17;

    /// <summary>Per cent of each colour moved towards the pixel's luma.</summary>
    public const int Desaturation = 15;

    /// <summary>The brightness kept, in 1/256.</summary>
    public const int Brightness = 180;

    private sealed record Entry(FrameSize Canvas, Affine2D PictureToCanvas, RectD LocalRect, RectD SourceRect, DecodedFrame Blurred);

    private static readonly ConditionalWeakTable<DecodedFrame, Entry> Cache = new();

    /// <summary>
    /// The background copy for <paramref name="frame"/> drawn on <paramref name="canvas"/> by <paramref name="pictureToCanvas"/>
    /// (its <paramref name="localRect"/> showing <paramref name="sourceRect"/> of the frame, as in the picture's
    /// <see cref="FrameDraw"/> at the canvas size).
    /// </summary>
    public static DecodedFrame Of(DecodedFrame frame, FrameSize canvas, Affine2D pictureToCanvas, RectD localRect, RectD sourceRect)
    {
        if (Cache.TryGetValue(frame, out var entry) && entry.Canvas == canvas && entry.PictureToCanvas == pictureToCanvas
            && entry.LocalRect == localRect && entry.SourceRect == sourceRect)
            return entry.Blurred;
        var blurred = Make(frame, canvas, pictureToCanvas, localRect, sourceRect);
        Cache.AddOrUpdate(frame, new Entry(canvas, pictureToCanvas, localRect, sourceRect, blurred));
        return blurred;
    }

    /// <summary>The size of the copy for <paramref name="canvas"/>: the canvas's aspect, its long side
    /// <c>long / Reduction</c> within [<see cref="MinSide"/>, <see cref="MaxSide"/>].</summary>
    public static (int Width, int Height) SizeFor(FrameSize canvas)
    {
        var longSide = Math.Clamp(Math.Max(canvas.Width, canvas.Height) / Reduction, MinSide, MaxSide);
        return canvas.Width >= canvas.Height
            ? (longSide, Math.Max(1, (int)Math.Round((double)longSide * canvas.Height / canvas.Width)))
            : (Math.Max(1, (int)Math.Round((double)longSide * canvas.Width / canvas.Height)), longSide);
    }

    /// <summary>The source grid's size for a visible source of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static (int Width, int Height) GridFor(double width, double height) => width >= height
        ? (GridSide, Math.Clamp((int)Math.Round(GridSide * height / width), 1, GridSide))
        : (Math.Clamp((int)Math.Round(GridSide * width / height), 1, GridSide), GridSide);

    private static DecodedFrame Make(DecodedFrame frame, FrameSize canvas, Affine2D pictureToCanvas, RectD localRect, RectD sourceRect)
    {
        var (w, h) = SizeFor(canvas);
        var small = new int[w * h * 3];
        if (localRect.Width > 0 && localRect.Height > 0 && sourceRect.Width > 0 && sourceRect.Height > 0
            && Math.Abs(pictureToCanvas.Determinant) > 1e-12)
        {
            var (gw, gh) = GridFor(sourceRect.Width, sourceRect.Height);
            var grid = Grid(frame, sourceRect, gw, gh);
            Extend(grid, gw, gh, small, w, h, canvas, pictureToCanvas.Invert(), localRect);
            var radius = Math.Max(2, Math.Max(w, h) / RadiusDivisor);
            var temp = new int[small.Length];
            for (var pass = 0; pass < 3; pass++)
            {
                BoxHorizontal(small, temp, w, h, radius);
                BoxVertical(temp, small, w, h, radius);
            }
        }

        var pixels = new byte[w * h * DecodedFrame.BytesPerPixel];
        for (var i = 0; i < w * h; i++)
        {
            int b = small[i * 3], g = small[i * 3 + 1], r = small[i * 3 + 2];
            var luma = (114 * b + 587 * g + 299 * r) / 1000;
            pixels[i * 4] = Tone(b, luma);
            pixels[i * 4 + 1] = Tone(g, luma);
            pixels[i * 4 + 2] = Tone(r, luma);
            pixels[i * 4 + 3] = 255;
        }
        return new DecodedFrame(w, h, w * DecodedFrame.BytesPerPixel, pixels, frame.Timestamp);
    }

    private static byte Tone(int value, int luma) =>
        (byte)Math.Clamp((value * (100 - Desaturation) + luma * Desaturation) / 100 * Brightness / 256, 0, 255);

    /// <summary>The visible source reduced to <paramref name="gw"/> × <paramref name="gh"/> BGR cells, each the average of
    /// <see cref="CellSamples"/>² points at fixed normalized positions.</summary>
    private static int[] Grid(DecodedFrame frame, RectD source, int gw, int gh)
    {
        var src = frame.Pixels.Span;
        var grid = new int[gw * gh * 3];
        const int n = CellSamples * CellSamples;
        for (var gy = 0; gy < gh; gy++)
            for (var gx = 0; gx < gw; gx++)
            {
                long b = 0, g = 0, r = 0;
                for (var sy = 0; sy < CellSamples; sy++)
                {
                    var py = Math.Clamp((int)(source.Y + (gy + (sy + 0.5) / CellSamples) * source.Height / gh), 0, frame.Height - 1);
                    var row = py * frame.Stride;
                    for (var sx = 0; sx < CellSamples; sx++)
                    {
                        var px = Math.Clamp((int)(source.X + (gx + (sx + 0.5) / CellSamples) * source.Width / gw), 0, frame.Width - 1);
                        var p = row + px * DecodedFrame.BytesPerPixel;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                    }
                }
                var o = (gy * gw + gx) * 3;
                grid[o] = (int)(b / n);
                grid[o + 1] = (int)(g / n);
                grid[o + 2] = (int)(r / n);
            }
        return grid;
    }

    /// <summary>Each copy pixel's canvas point → the picture's local space → clamped into the visible picture → the grid,
    /// sampled bilinearly.</summary>
    private static void Extend(int[] grid, int gw, int gh, int[] small, int w, int h, FrameSize canvas, Affine2D canvasToPicture, RectD local)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = canvasToPicture.Apply(new PointD((x + 0.5) * canvas.Width / w, (y + 0.5) * canvas.Height / h));
                var u = Math.Clamp((p.X - local.X) / local.Width, 0, 1) * gw - 0.5;
                var v = Math.Clamp((p.Y - local.Y) / local.Height, 0, 1) * gh - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(u), 0, gw - 1), y0 = Math.Clamp((int)Math.Floor(v), 0, gh - 1);
                int x1 = Math.Min(x0 + 1, gw - 1), y1 = Math.Min(y0 + 1, gh - 1);
                double fx = Math.Clamp(u - x0, 0, 1), fy = Math.Clamp(v - y0, 0, 1);
                for (var c = 0; c < 3; c++)
                {
                    var top = grid[(y0 * gw + x0) * 3 + c] * (1 - fx) + grid[(y0 * gw + x1) * 3 + c] * fx;
                    var bottom = grid[(y1 * gw + x0) * 3 + c] * (1 - fx) + grid[(y1 * gw + x1) * 3 + c] * fx;
                    small[(y * w + x) * 3 + c] = (int)Math.Round(top * (1 - fy) + bottom * fy);
                }
            }
    }

    /// <summary>A sliding box of <paramref name="radius"/> along the rows, the edge pixels repeated.</summary>
    private static void BoxHorizontal(int[] from, int[] to, int w, int h, int radius)
    {
        var size = 2 * radius + 1;
        for (var y = 0; y < h; y++)
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += from[(y * w + Math.Clamp(k, 0, w - 1)) * 3 + c];
                for (var x = 0; x < w; x++)
                {
                    to[(y * w + x) * 3 + c] = sum / size;
                    sum += from[(y * w + Math.Min(x + radius + 1, w - 1)) * 3 + c] - from[(y * w + Math.Max(x - radius, 0)) * 3 + c];
                }
            }
    }

    /// <summary>A sliding box of <paramref name="radius"/> along the columns, the edge pixels repeated.</summary>
    private static void BoxVertical(int[] from, int[] to, int w, int h, int radius)
    {
        var size = 2 * radius + 1;
        for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += from[(Math.Clamp(k, 0, h - 1) * w + x) * 3 + c];
                for (var y = 0; y < h; y++)
                {
                    to[(y * w + x) * 3 + c] = sum / size;
                    sum += from[(Math.Min(y + radius + 1, h - 1) * w + x) * 3 + c] - from[(Math.Max(y - radius, 0) * w + x) * 3 + c];
                }
            }
    }
}
