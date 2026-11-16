using System.Collections.Immutable;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.People;

/// <summary>
/// The appearance of a person for re-identification after a loss (D061 §3.4): a colour histogram of the upper inner part of
/// the box (the torso and the clothing — least disturbed by the wall and the arms). Chromatic pixels go into hue × saturation
/// bins, grey ones (low saturation) into brightness bins, so a white or black T-shirt is as distinctive as a red one.
/// Deterministic, cheap (a sparse grid of pixels) and independent of the frame's resolution.
/// </summary>
public static class PersonAppearance
{
    public const int HueBins = 8;
    public const int SaturationBins = 3;
    public const int GreyBins = 4;
    public const int Length = HueBins * SaturationBins + GreyBins;

    /// <summary>The histogram of <paramref name="box"/> on <paramref name="frame"/> (BGRA); empty when the box has no pixels.</summary>
    public static ImmutableArray<float> Of(DecodedFrame frame, NormalizedBox box)
    {
        // The torso region: the middle 60 % of the width, 15–60 % of the height.
        var x0 = box.X0 + box.Width * 0.2;
        var x1 = box.X1 - box.Width * 0.2;
        var y0 = box.Y0 + box.Height * 0.15;
        var y1 = box.Y0 + box.Height * 0.6;
        var px0 = Math.Clamp((int)(x0 * frame.Width), 0, frame.Width - 1);
        var px1 = Math.Clamp((int)Math.Ceiling(x1 * frame.Width), 0, frame.Width);
        var py0 = Math.Clamp((int)(y0 * frame.Height), 0, frame.Height - 1);
        var py1 = Math.Clamp((int)Math.Ceiling(y1 * frame.Height), 0, frame.Height);
        if (px1 <= px0 || py1 <= py0) return ImmutableArray<float>.Empty;

        // At most ~48 × 48 samples whatever the size.
        var stepX = Math.Max(1, (px1 - px0) / 48);
        var stepY = Math.Max(1, (py1 - py0) / 48);
        var bins = new float[Length];
        var pixels = frame.Pixels.Span;
        var total = 0;
        for (var y = py0; y < py1; y += stepY)
        {
            var row = y * frame.Stride;
            for (var x = px0; x < px1; x += stepX)
            {
                var i = row + x * DecodedFrame.BytesPerPixel;
                bins[Bin(pixels[i + 2], pixels[i + 1], pixels[i])]++;
                total++;
            }
        }
        if (total == 0) return ImmutableArray<float>.Empty;
        for (var i = 0; i < bins.Length; i++) bins[i] /= total;
        return ImmutableArray.Create(bins);
    }

    /// <summary>The bin of one RGB pixel.</summary>
    internal static int Bin(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        var saturation = max == 0 ? 0 : chroma / (double)max;
        if (saturation < 0.2 || max < 40)
            return HueBins * SaturationBins + Math.Min(GreyBins - 1, max * GreyBins / 256);
        double hue;
        if (max == r) hue = (g - b) / (double)chroma % 6;
        else if (max == g) hue = (b - r) / (double)chroma + 2;
        else hue = (r - g) / (double)chroma + 4;
        if (hue < 0) hue += 6;
        var h = Math.Min(HueBins - 1, (int)(hue / 6 * HueBins));
        var s = Math.Min(SaturationBins - 1, (int)((saturation - 0.2) / 0.8 * SaturationBins));
        return h * SaturationBins + s;
    }

    /// <summary>The Bhattacharyya coefficient of two histograms: 1 identical, 0 disjoint; 0 when either is empty.</summary>
    public static double Similarity(ImmutableArray<float> a, ImmutableArray<float> b)
    {
        if (a.IsDefaultOrEmpty || b.IsDefaultOrEmpty || a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Sqrt(Math.Max(0, a[i]) * Math.Max(0, b[i]));
        return Math.Clamp(sum, 0, 1);
    }

    /// <summary>A running mean of histograms (<paramref name="weight"/> for the new one); the old one when the new is empty.</summary>
    public static ImmutableArray<float> Blend(ImmutableArray<float> old, ImmutableArray<float> next, double weight)
    {
        if (next.IsDefaultOrEmpty) return old;
        if (old.IsDefaultOrEmpty || old.Length != next.Length) return next;
        var result = new float[old.Length];
        for (var i = 0; i < result.Length; i++) result[i] = (float)(old[i] * (1 - weight) + next[i] * weight);
        return ImmutableArray.Create(result);
    }
}
