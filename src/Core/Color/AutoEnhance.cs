namespace AiVideoEditor.Core.Color;

/// <summary>
/// D069 §7: «Автоулучшение» — the sliders a clip's frames call for, from their statistics (no AI model): the exposure
/// brings the mean luma towards a mid grey, the contrast stretches a narrow range, the highlights pull back clipped whites and
/// the shadows lift crushed blacks, the white balance makes the frames' mean grey (grey world), the saturation lifts a dull
/// picture. Each step is damped and bounded, so the result is a starting point the user can adjust. Deterministic.
/// </summary>
public static class AutoEnhance
{
    /// <summary>The statistics of BGRA frames (a few of the clip's, any size): their pixels sampled on a grid.</summary>
    public sealed record Stats(double MeanLuma, double P5, double P99, double MeanR, double MeanG, double MeanB, double MeanSaturation);

    /// <summary>Gathers the statistics of <paramref name="frames"/> (BGRA, width, height, stride) — about 20 000 pixels each.</summary>
    public static Stats Measure(IEnumerable<(byte[] Pixels, int Width, int Height, int Stride)> frames)
    {
        var histogram = new long[256];
        double sumR = 0, sumG = 0, sumB = 0, sumSat = 0;
        long count = 0;
        foreach (var (pixels, width, height, stride) in frames)
        {
            var step = Math.Max(1, (int)Math.Sqrt((double)width * height / 20_000));
            for (var y = 0; y < height; y += step)
            for (var x = 0; x < width; x += step)
            {
                var i = y * stride + x * 4;
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                histogram[(54 * r + 183 * g + 19 * b) >> 8]++;
                sumR += ColorAdjust.SrgbToLinear(r / 255.0);
                sumG += ColorAdjust.SrgbToLinear(g / 255.0);
                sumB += ColorAdjust.SrgbToLinear(b / 255.0);
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                sumSat += max == 0 ? 0 : (max - min) / (double)max;
                count++;
            }
        }
        if (count == 0) return new Stats(0.45, 0.06, 0.95, 1, 1, 1, 0.3);
        double mean = 0;
        for (var v = 0; v < 256; v++) mean += v * histogram[v];
        return new Stats(mean / count / 255, Percentile(0.05), Percentile(0.99), sumR / count, sumG / count, sumB / count, sumSat / count);

        double Percentile(double p)
        {
            long target = (long)Math.Ceiling(p * count), seen = 0;
            for (var v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen >= target) return v / 255.0;
            }
            return 1;
        }
    }

    /// <summary>The correction the statistics call for (brightness left at 0).</summary>
    public static ColorCorrection Suggest(Stats s)
    {
        // Exposure: the mean luma towards 0.45 (sRGB values ≈ linear^(1/2.2)), damped, ±60.
        var exposure = s.MeanLuma <= 0.001 ? 60 : Bound(2.2 * Math.Log2(0.45 / s.MeanLuma) / 2 * 100 * 0.6, 60);
        // Contrast: a range (5 %…99 %) narrower than 0.8 is stretched, up to +40.
        var spread = s.P99 - s.P5;
        var contrast = spread >= 0.8 ? 0 : Bound((0.85 / Math.Max(spread, 0.1) - 1) * 50, 40, 0);
        var highlights = s.P99 > 0.95 ? -Bound((s.P99 - 0.95) / 0.05 * 30, 30, 0) : 0;
        var shadows = s.P5 < 0.06 ? Bound((0.06 - s.P5) / 0.06 * 25, 25, 0) : 0;
        // Grey world: the gains that make the mean red, green and blue equal (ColorAdjust's balance), damped, ±50.
        var temperature = 0;
        var tint = 0;
        if (s.MeanR > 1e-4 && s.MeanB > 1e-4 && s.MeanG > 1e-4)
        {
            var q = s.MeanB / s.MeanR;
            temperature = Bound((q - 1) / (0.3 * (q + 1)) * 100 * 0.6, 50);
            var q2 = s.MeanG / ((s.MeanR + s.MeanB) / 2);
            tint = Bound((1 - 1 / q2) / 0.25 * 100 * 0.6, 50);
        }
        var saturation = s.MeanSaturation < 0.3 ? Bound((0.3 - s.MeanSaturation) * 100, 25, 0) : 0;
        return new ColorCorrection(exposure, 0, contrast, highlights, shadows, saturation, temperature, tint);
    }

    private static int Bound(double value, int limit, int min = int.MinValue) =>
        (int)Math.Round(Math.Clamp(value, Math.Max(-limit, min), limit), MidpointRounding.AwayFromZero);
}
