using System.Runtime.CompilerServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D057 §3.7: the blurred background of a picture — its frame cropped to the canvas's aspect (centred: «fill»), reduced to
/// a small copy (about a sixteenth of the canvas, by averaging every source pixel of each cell) and blurred three times with
/// a box of <see cref="Radius"/> (≈ a Gaussian); the painter stretches it over the canvas with smoothing. Pure, deterministic
/// integer arithmetic — the Preview (decoding at a reduced size) and the export (at the full size) blur alike, and the
/// reduction keeps it cheap even at 4K. One default strength; the result is cached per decoded frame and canvas.
/// </summary>
public static class BackgroundBlur
{
    /// <summary>The reduction: the copy's long side is the canvas's divided by this (at least <see cref="MinSide"/>).</summary>
    public const int Reduction = 16;

    public const int MinSide = 16;

    public const int MaxSide = 240;

    /// <summary>The box radius of each of the three passes, in pixels of the reduced copy.</summary>
    public const int Radius = 3;

    private sealed record Entry(FrameSize Canvas, DecodedFrame Blurred);

    private static readonly ConditionalWeakTable<DecodedFrame, Entry> Cache = new();

    /// <summary>The blurred copy of <paramref name="frame"/> for <paramref name="canvas"/>.</summary>
    public static DecodedFrame Of(DecodedFrame frame, FrameSize canvas)
    {
        if (Cache.TryGetValue(frame, out var entry) && entry.Canvas == canvas) return entry.Blurred;
        var blurred = Make(frame, canvas);
        Cache.AddOrUpdate(frame, new Entry(canvas, blurred));
        return blurred;
    }

    /// <summary>The size of the reduced copy for <paramref name="canvas"/>: the canvas's aspect, its long side
    /// <c>long / Reduction</c> within [<see cref="MinSide"/>, <see cref="MaxSide"/>].</summary>
    public static (int Width, int Height) SizeFor(FrameSize canvas)
    {
        var longSide = Math.Clamp(Math.Max(canvas.Width, canvas.Height) / Reduction, MinSide, MaxSide);
        return canvas.Width >= canvas.Height
            ? (longSide, Math.Max(1, (int)Math.Round((double)longSide * canvas.Height / canvas.Width)))
            : (Math.Max(1, (int)Math.Round((double)longSide * canvas.Width / canvas.Height)), longSide);
    }

    /// <summary>The source rectangle of <paramref name="frame"/> with the canvas's aspect, centred (integer pixels).</summary>
    public static (int X, int Y, int Width, int Height) FillRect(int frameWidth, int frameHeight, FrameSize canvas)
    {
        // frame aspect wider than the canvas: crop the sides; else the top and bottom.
        if ((long)frameWidth * canvas.Height >= (long)frameHeight * canvas.Width)
        {
            var w = Math.Max(1, (int)((long)frameHeight * canvas.Width / canvas.Height));
            return ((frameWidth - w) / 2, 0, w, frameHeight);
        }
        var h = Math.Max(1, (int)((long)frameWidth * canvas.Height / canvas.Width));
        return (0, (frameHeight - h) / 2, frameWidth, h);
    }

    private static DecodedFrame Make(DecodedFrame frame, FrameSize canvas)
    {
        var (w, h) = SizeFor(canvas);
        var (sx, sy, sw, sh) = FillRect(frame.Width, frame.Height, canvas);
        var src = frame.Pixels.Span;
        var small = new int[w * h * 3];

        // Area average: each cell [x0, x1) × [y0, y1) of the fill rectangle → one pixel.
        for (var y = 0; y < h; y++)
        {
            int y0 = sy + (int)((long)y * sh / h), y1 = Math.Max(y0 + 1, sy + (int)((long)(y + 1) * sh / h));
            for (var x = 0; x < w; x++)
            {
                int x0 = sx + (int)((long)x * sw / w), x1 = Math.Max(x0 + 1, sx + (int)((long)(x + 1) * sw / w));
                long b = 0, g = 0, r = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * frame.Stride;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var p = row + xx * DecodedFrame.BytesPerPixel;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                    }
                }
                var n = (long)(y1 - y0) * (x1 - x0);
                var o = (y * w + x) * 3;
                small[o] = (int)(b / n);
                small[o + 1] = (int)(g / n);
                small[o + 2] = (int)(r / n);
            }
        }

        var temp = new int[small.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(small, temp, w, h);
            BoxVertical(temp, small, w, h);
        }

        var pixels = new byte[w * h * DecodedFrame.BytesPerPixel];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 4] = (byte)small[i * 3];
            pixels[i * 4 + 1] = (byte)small[i * 3 + 1];
            pixels[i * 4 + 2] = (byte)small[i * 3 + 2];
            pixels[i * 4 + 3] = 255;
        }
        return new DecodedFrame(w, h, w * DecodedFrame.BytesPerPixel, pixels, frame.Timestamp);
    }

    /// <summary>A box of radius <see cref="Radius"/> along the rows, the edge pixels repeated.</summary>
    private static void BoxHorizontal(int[] from, int[] to, int w, int h)
    {
        var size = 2 * Radius + 1;
        for (var y = 0; y < h; y++)
            for (var c = 0; c < 3; c++)
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var k = -Radius; k <= Radius; k++) sum += from[(y * w + Math.Clamp(x + k, 0, w - 1)) * 3 + c];
                    to[(y * w + x) * 3 + c] = sum / size;
                }
    }

    /// <summary>A box of radius <see cref="Radius"/> along the columns, the edge pixels repeated.</summary>
    private static void BoxVertical(int[] from, int[] to, int w, int h)
    {
        var size = 2 * Radius + 1;
        for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++)
                for (var y = 0; y < h; y++)
                {
                    var sum = 0;
                    for (var k = -Radius; k <= Radius; k++) sum += from[(Math.Clamp(y + k, 0, h - 1) * w + x) * 3 + c];
                    to[(y * w + x) * 3 + c] = sum / size;
                }
    }
}
