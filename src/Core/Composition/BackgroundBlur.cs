using System.Runtime.CompilerServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// The blurred background of a picture (D057 §3.7; the look of the owner's reference of 2026-11-10, §7.12): the visible
/// picture (its crop) enlarged to fill the canvas, centred, and blurred moderately — the scene stays recognisable as a soft
/// backdrop, slightly darkened so the picture in front stands out.
/// <list type="number">
/// <item>A canvas-aspect copy (<see cref="SizeFor"/>, a canvas side / <see cref="Reduction"/>) of the crop's centred
/// fill rectangle; each copy pixel averages <see cref="CellSamples"/>² points at fixed normalized positions — the same
/// points whatever size the frame was decoded at (the Preview's reduced frames, the export's full ones).</item>
/// <item>Three box passes of a radius <see cref="BlurPerMille"/> ‰ of the copy's long side (≈ a Gaussian of about that
/// width), then <see cref="Brightness"/> / 256 of the brightness.</item>
/// </list>
/// Pure and deterministic; the painter stretches the copy over the canvas with smoothing. The same copy for the Preview and
/// the export (one operation, <see cref="CompositionDrawPlan.BlurredBackground"/>), cached per decoded frame and crop.
/// </summary>
public static class BackgroundBlur
{
    /// <summary>The reduction: the copy's long side is the canvas's divided by this (within [<see cref="MinSide"/>,
    /// <see cref="MaxSide"/>]).</summary>
    public const int Reduction = 8;

    public const int MinSide = 16;

    public const int MaxSide = 240;

    /// <summary>Sample points per copy pixel side.</summary>
    public const int CellSamples = 4;

    /// <summary>The box radius in per mille of the copy's long side (three passes: a Gaussian of about that σ) — about 1.5 %
    /// of the canvas, as the owner's reference.</summary>
    public const int BlurPerMille = 15;

    /// <summary>The brightness kept, in 1/256.</summary>
    public const int Brightness = 230;

    private sealed record Entry(FrameSize Canvas, RectD LocalRect, RectD SourceRect, DecodedFrame Blurred);

    private static readonly ConditionalWeakTable<DecodedFrame, Entry> Cache = new();

    /// <summary>The background copy for <paramref name="frame"/> on <paramref name="canvas"/>: <paramref name="sourceRect"/>
    /// of the frame is the visible picture (its crop), <paramref name="localRect"/> its size in source pixels (its true
    /// aspect), as in the picture's <see cref="FrameDraw"/>.</summary>
    public static DecodedFrame Of(DecodedFrame frame, FrameSize canvas, RectD localRect, RectD sourceRect)
    {
        if (Cache.TryGetValue(frame, out var entry) && entry.Canvas == canvas && entry.LocalRect == localRect && entry.SourceRect == sourceRect)
            return entry.Blurred;
        var blurred = Make(frame, canvas, localRect, sourceRect);
        Cache.AddOrUpdate(frame, new Entry(canvas, localRect, sourceRect, blurred));
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

    /// <summary>The part of the crop that fills the canvas, centred, in the crop's normalized coordinates
    /// <c>(u0, v0, width, height)</c>.</summary>
    public static (double U, double V, double Width, double Height) FillOf(RectD local, FrameSize canvas)
    {
        var picture = local.Width / local.Height;
        var target = (double)canvas.Width / canvas.Height;
        return picture >= target
            ? ((1 - target / picture) / 2, 0, target / picture, 1)      // a wider picture: its sides cut
            : (0, (1 - picture / target) / 2, 1, picture / target);    // a taller one: its top and bottom cut
    }

    private static DecodedFrame Make(DecodedFrame frame, FrameSize canvas, RectD local, RectD source)
    {
        var (w, h) = SizeFor(canvas);
        var small = new int[w * h * 3];
        if (local.Width > 0 && local.Height > 0 && source.Width > 0 && source.Height > 0)
        {
            Sample(frame, source, FillOf(local, canvas), small, w, h);
            var radius = Math.Max(1, (int)Math.Round(Math.Max(w, h) * BlurPerMille / 1000.0));
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
            pixels[i * 4] = Tone(small[i * 3]);
            pixels[i * 4 + 1] = Tone(small[i * 3 + 1]);
            pixels[i * 4 + 2] = Tone(small[i * 3 + 2]);
            pixels[i * 4 + 3] = 255;
        }
        return new DecodedFrame(w, h, w * DecodedFrame.BytesPerPixel, pixels, frame.Timestamp);
    }

    private static byte Tone(int value) => (byte)Math.Clamp(value * Brightness / 256, 0, 255);

    /// <summary>Each copy pixel: the average of <see cref="CellSamples"/>² points of its cell of the fill rectangle.</summary>
    private static void Sample(DecodedFrame frame, RectD source, (double U, double V, double Width, double Height) fill, int[] small, int w, int h)
    {
        var src = frame.Pixels.Span;
        const int n = CellSamples * CellSamples;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                long b = 0, g = 0, r = 0;
                for (var sy = 0; sy < CellSamples; sy++)
                {
                    var v = fill.V + (y + (sy + 0.5) / CellSamples) / h * fill.Height;
                    var py = Math.Clamp((int)(source.Y + v * source.Height), 0, frame.Height - 1);
                    var row = py * frame.Stride;
                    for (var sx = 0; sx < CellSamples; sx++)
                    {
                        var u = fill.U + (x + (sx + 0.5) / CellSamples) / w * fill.Width;
                        var px = Math.Clamp((int)(source.X + u * source.Width), 0, frame.Width - 1);
                        var p = row + px * DecodedFrame.BytesPerPixel;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                    }
                }
                var o = (y * w + x) * 3;
                small[o] = (int)(b / n);
                small[o + 1] = (int)(g / n);
                small[o + 2] = (int)(r / n);
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
