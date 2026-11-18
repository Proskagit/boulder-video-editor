namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D069 §8: a transition's effect on a picture's BGRA pixels, in place — the same code for the Preview and the export.
/// <list type="bullet">
/// <item>Blur: three passes of a box blur (close to a Gaussian) of radius <c>Blur × short side</c>, along both axes or one
/// (Смахивание's motion blur).</item>
/// <item>Glitch: the red channel shifted left and the blue right by up to 3 % of the width, and a few horizontal bands moved
/// sideways — chosen by the effect's seed.</item>
/// </list>
/// Alpha is kept.
/// </summary>
public static class TransitionEffects
{
    public static void Apply(Span<byte> pixels, int width, int height, int stride, LayerEffect effect)
    {
        if (effect.Blur > 0)
        {
            var radius = (int)Math.Round(effect.Blur * Math.Min(width, height), MidpointRounding.AwayFromZero);
            if (radius > 0) Blur(pixels, width, height, stride, radius, effect.Axis);
        }
        if (effect.Glitch > 0) Glitch(pixels, width, height, stride, effect.Glitch, effect.Seed);
    }

    /// <summary>Three box passes per axis (the colour channels; alpha kept).</summary>
    public static void Blur(Span<byte> pixels, int width, int height, int stride, int radius, BlurAxis axis)
    {
        var line = new int[Math.Max(width, height) * 3];
        var output = new byte[Math.Max(width, height) * 3];
        for (var pass = 0; pass < 3; pass++)
        {
            if (axis != BlurAxis.Vertical)
                for (var y = 0; y < height; y++) BoxLine(pixels, y * stride, 4, width, radius, line, output);
            if (axis != BlurAxis.Horizontal)
                for (var x = 0; x < width; x++) BoxLine(pixels, x * 4, stride, height, radius, line, output);
        }
    }

    /// <summary>A box average of radius <paramref name="r"/> along one line of <paramref name="count"/> pixels starting at
    /// <paramref name="start"/>, <paramref name="step"/> bytes apart; the edges repeat the end pixels.</summary>
    private static void BoxLine(Span<byte> px, int start, int step, int count, int r, int[] line, byte[] output)
    {
        for (var i = 0; i < count; i++)
        {
            var o = start + i * step;
            line[i * 3] = px[o];
            line[i * 3 + 1] = px[o + 1];
            line[i * 3 + 2] = px[o + 2];
        }
        var window = 2 * r + 1;
        int s0 = 0, s1 = 0, s2 = 0;
        for (var k = -r; k <= r; k++)
        {
            var c = Math.Clamp(k, 0, count - 1) * 3;
            s0 += line[c];
            s1 += line[c + 1];
            s2 += line[c + 2];
        }
        for (var i = 0; i < count; i++)
        {
            output[i * 3] = (byte)((s0 + r) / window);
            output[i * 3 + 1] = (byte)((s1 + r) / window);
            output[i * 3 + 2] = (byte)((s2 + r) / window);
            var add = Math.Min(i + r + 1, count - 1) * 3;
            var remove = Math.Max(i - r, 0) * 3;
            s0 += line[add] - line[remove];
            s1 += line[add + 1] - line[remove + 1];
            s2 += line[add + 2] - line[remove + 2];
        }
        for (var i = 0; i < count; i++)
        {
            var o = start + i * step;
            px[o] = output[i * 3];
            px[o + 1] = output[i * 3 + 1];
            px[o + 2] = output[i * 3 + 2];
        }
    }

    /// <summary>The channel split and the shifted bands of a glitch of <paramref name="strength"/> 0…1.</summary>
    public static void Glitch(Span<byte> pixels, int width, int height, int stride, double strength, int seed)
    {
        var shift = (int)Math.Round(strength * 0.03 * width, MidpointRounding.AwayFromZero);
        var row = new byte[width * 4];
        var random = new Random(seed);
        var bands = strength > 0.3 ? 1 + (int)(strength * 4) : 0;
        var bandTops = new int[bands];
        var bandHeights = new int[bands];
        var bandShifts = new int[bands];
        for (var b = 0; b < bands; b++)
        {
            bandTops[b] = random.Next(height);
            bandHeights[b] = Math.Max(1, (int)(height * (0.02 + 0.06 * random.NextDouble())));
            bandShifts[b] = (int)((random.NextDouble() * 2 - 1) * strength * 0.08 * width);
        }
        for (var y = 0; y < height; y++)
        {
            var line = pixels.Slice(y * stride, width * 4);
            line.CopyTo(row);
            var band = 0;
            for (var b = 0; b < bands; b++)
                if (y >= bandTops[b] && y < bandTops[b] + bandHeights[b]) band += bandShifts[b];
            for (var x = 0; x < width; x++)
            {
                var src = Math.Clamp(x - band, 0, width - 1);
                var red = Math.Clamp(src + shift, 0, width - 1);
                var blue = Math.Clamp(src - shift, 0, width - 1);
                line[x * 4] = row[blue * 4];
                line[x * 4 + 1] = row[src * 4 + 1];
                line[x * 4 + 2] = row[red * 4 + 2];
                line[x * 4 + 3] = row[src * 4 + 3];
            }
        }
    }
}
