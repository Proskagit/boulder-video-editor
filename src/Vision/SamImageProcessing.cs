using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Vision;

/// <summary>
/// SAM 2's own pre- and postprocessing (sam2/utils/transforms.py), reproduced exactly as the E1 reference does it
/// (D034 §3, reference/onnx_reference.json):
/// <list type="bullet">
/// <item>the frame as RGB / 255, resized to 1024 × 1024 by the antialiased bilinear filter of torchvision's
/// <c>Resize</c> (<c>F.interpolate(mode="bilinear", align_corners=False, antialias=True)</c>: the triangle filter
/// stretched by the scale when downscaling), then normalized with the ImageNet mean and deviation, NCHW;</item>
/// <item>the decoder's 256 × 256 logits upsampled bilinearly to the frame (<c>align_corners=False</c>, no antialias),
/// thresholded at 0.</item>
/// </list>
/// </summary>
public static class SamImageProcessing
{
    private static readonly double[] Mean = { 0.485, 0.456, 0.406 };
    private static readonly double[] Std = { 0.229, 0.224, 0.225 };

    /// <summary>The encoder input <c>[1, 3, size, size]</c> for a BGRA <paramref name="frame"/>.</summary>
    public static float[] Preprocess(DecodedFrame frame, int size = HoldModel.InputSize)
    {
        int w = frame.Width, h = frame.Height;
        var pixels = frame.Pixels.Span;

        // Horizontal pass: each source row → size columns, per channel (RGB, 0..1).
        var horizontal = Weights(w, size);
        var rows = new double[3][];
        for (var c = 0; c < 3; c++) rows[c] = new double[h * size];
        for (var y = 0; y < h; y++)
        {
            var row = y * frame.Stride;
            for (var x = 0; x < size; x++)
            {
                var (start, weights) = horizontal[x];
                double r = 0, g = 0, b = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var i = row + (start + k) * DecodedFrame.BytesPerPixel;
                    var wk = weights[k];
                    b += pixels[i] * wk;
                    g += pixels[i + 1] * wk;
                    r += pixels[i + 2] * wk;
                }
                rows[0][y * size + x] = r / 255.0;
                rows[1][y * size + x] = g / 255.0;
                rows[2][y * size + x] = b / 255.0;
            }
        }

        // Vertical pass, then the normalization.
        var vertical = Weights(h, size);
        var tensor = new float[3 * size * size];
        for (var c = 0; c < 3; c++)
        {
            var plane = rows[c];
            var offset = c * size * size;
            for (var y = 0; y < size; y++)
            {
                var (start, weights) = vertical[y];
                for (var x = 0; x < size; x++)
                {
                    double v = 0;
                    for (var k = 0; k < weights.Length; k++) v += plane[(start + k) * size + x] * weights[k];
                    tensor[offset + y * size + x] = (float)((v - Mean[c]) / Std[c]);
                }
            }
        }
        return tensor;
    }

    /// <summary>
    /// The antialiased bilinear weights of PyTorch / PIL for <paramref name="input"/> → <paramref name="output"/> samples:
    /// <c>scale = in/out</c>, <c>support = max(scale, 1)</c>, the centre of output <c>i</c> at <c>scale·(i + 0.5)</c>, taps
    /// <c>[⌊centre − support + 0.5⌋, ⌊centre + support + 0.5⌋)</c> weighted by the triangle <c>1 − |(j − centre + 0.5)/max(scale,1)|</c>,
    /// normalized.
    /// </summary>
    public static (int Start, double[] Weights)[] Weights(int input, int output)
    {
        var scale = (double)input / output;
        var support = scale >= 1 ? scale : 1.0;
        var invScale = scale >= 1 ? 1.0 / scale : 1.0;
        var result = new (int, double[])[output];
        for (var i = 0; i < output; i++)
        {
            var center = scale * (i + 0.5);
            var xmin = Math.Max((int)(center - support + 0.5), 0);
            var xmax = Math.Min((int)(center + support + 0.5), input);
            var weights = new double[Math.Max(0, xmax - xmin)];
            double total = 0;
            for (var j = 0; j < weights.Length; j++)
            {
                var t = Math.Abs((j + xmin - center + 0.5) * invScale);
                weights[j] = t < 1 ? 1 - t : 0;
                total += weights[j];
            }
            if (total > 0)
                for (var j = 0; j < weights.Length; j++) weights[j] /= total;
            result[i] = (xmin, weights);
        }
        return result;
    }

    /// <summary>
    /// The mask of one 256 × 256 logits plane at <paramref name="width"/> × <paramref name="height"/>: bilinear,
    /// <c>align_corners=False</c> (<c>src = (dst + 0.5)·in/out − 0.5</c>, clamped at 0), value &gt; 0. Only the rows and
    /// columns next to a positive logit are interpolated — elsewhere all four neighbours are ≤ 0 and so is the value.
    /// </summary>
    public static byte[] Upsample(ReadOnlySpan<float> logits, int lowSize, int width, int height)
    {
        var mask = new byte[width * height];
        int r0 = lowSize, r1 = -1, c0 = lowSize, c1 = -1;
        for (var r = 0; r < lowSize; r++)
        for (var c = 0; c < lowSize; c++)
        {
            if (!(logits[r * lowSize + c] > 0)) continue;
            r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
        }
        if (r1 < 0) return mask;

        var ys = Taps(lowSize, height);
        var xs = Taps(lowSize, width);
        for (var y = 0; y < height; y++)
        {
            var (y0, y1, ly) = ys[y];
            if (y1 < r0 || y0 > r1) continue;
            for (var x = 0; x < width; x++)
            {
                var (x0, x1, lx) = xs[x];
                if (x1 < c0 || x0 > c1) continue;
                double top = logits[y0 * lowSize + x0] * (1 - lx) + logits[y0 * lowSize + x1] * lx;
                double bottom = logits[y1 * lowSize + x0] * (1 - lx) + logits[y1 * lowSize + x1] * lx;
                if (top * (1 - ly) + bottom * ly > 0) mask[y * width + x] = 1;
            }
        }
        return mask;
    }

    private static (int I0, int I1, double Lambda)[] Taps(int input, int output)
    {
        var scale = (double)input / output;
        var taps = new (int, int, double)[output];
        for (var i = 0; i < output; i++)
        {
            var src = Math.Max((i + 0.5) * scale - 0.5, 0);
            var i0 = Math.Min((int)src, input - 1);
            var i1 = Math.Min(i0 + 1, input - 1);
            taps[i] = (i0, i1, src - i0);
        }
        return taps;
    }
}
