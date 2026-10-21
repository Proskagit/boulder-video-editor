namespace AiVideoEditor.Core.Composition;

/// <summary>
/// The picture treatment of a template (D045 §3): applied by the renderer to everything drawn below the template, before
/// its graphics. BGRA, opaque, in place. Deterministic — a function of the pixels, the template's clock and nothing else
/// (no shared random state) — so the Preview and the export compute the same bytes at the same size. Sizes are in
/// 1080-units of the buffer (× its short side / 1080), so a smaller Preview buffer gets the same look, scaled.
/// </summary>
public static class TemplateTreatment
{
    /// <summary>The VHS band's cycle: it crosses the frame bottom → top in <see cref="BandCrossing"/> s, then rests.</summary>
    public const double BandCycle = 4.8;
    public const double BandCrossing = 2.2;

    /// <summary>
    /// VHS (fixed strength, the product owner, 2026-10-21): a slight horizontal softness, a chroma offset (red to the right,
    /// blue to the left), 22 % less saturation, lifted blacks, faint scanlines, luma noise; a tracking band drifting up
    /// the frame every <see cref="BandCycle"/> s (row jitter and brighter noise inside it); the head-switching strip at the
    /// bottom. The holds and the climber stay readable.
    /// </summary>
    public static void Vhs(byte[] bgra, int width, int height, int stride, TemplateClock clock)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if ((long)stride * height > bgra.Length) throw new ArgumentException("The buffer is smaller than stride × height.", nameof(bgra));
        if (width <= 0 || height <= 0) return;
        var u = Math.Min(width, height) / 1080.0;
        var shift = Math.Max(1, (int)Math.Round(3 * u));
        var bandHalf = 23 * u;
        var phase = (clock.Counter + 1.2) % BandCycle;
        var bandY = phase < BandCrossing ? height * (1.05 - phase / BandCrossing * 1.15) : double.NaN;
        var headRows = (int)Math.Round(12 * u);
        var scanPeriod = Math.Max(1, (int)Math.Round(2 * u));
        var frame = clock.CounterFrame;

        // Rows are independent (each reads only its own source row), so they run in parallel with the same result.
        Parallel.For(0, height, () => new byte[width * 4], (y, _, row) =>
        {
            var line = bgra.AsSpan(y * stride, width * 4);
            line.CopyTo(row);

            var offset = 0;
            var noise = 7.0;
            if (!double.IsNaN(bandY) && Math.Abs(y - bandY) < bandHalf)
            {
                var edge = 1 - Math.Abs(y - bandY) / bandHalf;
                offset = (int)Math.Round((Hash(y, frame, 7) - 0.5) * 18 * u * edge);
                noise += 26 * edge;
            }
            if (y >= height - headRows)
            {
                offset = (int)Math.Round((10 + Hash(y, frame, 3) * 14) * u);
                noise += 20;
            }
            var scan = (y / scanPeriod) % 2 == 0 ? 1.0 : 0.93;

            for (var x = 0; x < width; x++)
            {
                int At(int dx) => Math.Clamp(x - offset + dx, 0, width - 1) * 4;
                int c = At(0), l = At(-1), r = At(1), rs = At(-shift), bs = At(shift);
                double b = (row[l] + 2 * row[c] + row[r]) / 4.0;
                double g = (row[l + 1] + 2 * row[c + 1] + row[r + 1]) / 4.0;
                double red = (row[rs + 2] * 3 + row[c + 2]) / 4.0;
                b = (b + row[bs]) / 2.0;
                var lum = 0.0722 * b + 0.7152 * g + 0.2126 * red;
                const double sat = 0.78;
                b = lum + (b - lum) * sat;
                g = lum + (g - lum) * sat;
                red = lum + (red - lum) * sat;
                var n = (Hash(x, y, frame) - 0.5) * 2 * noise;
                const double lift = 10;
                var i = x * 4;
                line[i] = Byte((lift + b * (1 - lift / 255) + n) * scan);
                line[i + 1] = Byte((lift + g * (1 - lift / 255) + n) * scan);
                line[i + 2] = Byte((lift + red * (1 - lift / 255) + n) * scan + 3);
                line[i + 3] = 255;
            }
            return row;
        }, _ => { });
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>A stable pseudo-random value in [0, 1) from three integers.</summary>
    internal static double Hash(long a, long b, long c)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }
}
