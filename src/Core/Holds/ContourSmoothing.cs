using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// D065 §2: the smoothing of a new hold's contour — a mask's pixel boundary («ёжик») turned into a smooth outline (a circle
/// stays a circle, an uneven hold becomes a smooth «amoeba»). The contour is resampled every pixel along its length, smoothed
/// with a circular Gaussian whose width grows with <c>strength</c> and with the hold's size, scaled about its centroid back
/// to its area (a Gaussian shrinks a shape) and simplified with Douglas–Peucker. Strength 0 returns the contour unchanged.
/// Applied once, when a hold is created; a saved contour and a contour edited by hand are never smoothed again.
/// </summary>
public static class ContourSmoothing
{
    public const int MinStrength = 0;
    public const int MaxStrength = 100;

    /// <summary>The strength by default (the owner's settings, D065 §3).</summary>
    public const int DefaultStrength = 50;

    /// <summary>At strength 100 the Gaussian's σ is this share of the hold's equivalent diameter.</summary>
    public const double MaxSigmaShareOfDiameter = 0.12;

    /// <summary>At strength 100 σ is at least this many pixels (the mask's pixel noise on a small hold).</summary>
    public const double MaxSigmaFloorPixels = 2.0;

    /// <summary>The simplification of the smoothed contour, in pixels.</summary>
    public const double SimplifyPixels = 0.25;

    private const int MaxSamples = 4096;

    /// <summary>
    /// The contour <paramref name="contour"/> (normalized to a <paramref name="width"/> × <paramref name="height"/> frame)
    /// smoothed with <paramref name="strength"/> (0…100); the contour itself when the strength is 0, the frame size unknown
    /// or the contour too small to smooth.
    /// </summary>
    public static ImmutableArray<NormalizedPoint> Smooth(ImmutableArray<NormalizedPoint> contour, int width, int height, double strength)
    {
        if (contour.IsDefault || contour.Length < 3 || width <= 0 || height <= 0) return contour;
        strength = Math.Clamp(strength, MinStrength, MaxStrength);
        if (!(strength > 0)) return contour;

        var ring = contour.Select(p => (X: p.X * width, Y: p.Y * height)).ToList();
        var area = Math.Abs(SignedArea(ring));
        if (!(area > 0)) return contour;
        var diameter = 2 * Math.Sqrt(area / Math.PI);
        var share = strength / MaxStrength;
        var sigma = Math.Max(share * MaxSigmaFloorPixels, share * MaxSigmaShareOfDiameter * diameter);

        var samples = Resample(ring, out var spacing);
        if (samples.Count < 8) return contour;
        var sigmaSamples = Math.Min(sigma / spacing, samples.Count / 6.0);
        if (!(sigmaSamples >= 0.3)) return contour;
        var smoothed = Gaussian(samples, sigmaSamples);

        // A Gaussian pulls a shape in; scale it about its centroid back to the original area.
        var smoothedArea = Math.Abs(SignedArea(smoothed));
        if (smoothedArea > 0)
        {
            var (cx, cy) = (smoothed.Average(p => p.X), smoothed.Average(p => p.Y));
            var k = Math.Sqrt(area / smoothedArea);
            for (var i = 0; i < smoothed.Count; i++)
                smoothed[i] = (cx + (smoothed[i].X - cx) * k, cy + (smoothed[i].Y - cy) * k);
        }

        var simplified = MaskContour.Simplify(smoothed, SimplifyPixels);
        if (simplified.Count < 3) return contour;
        if (simplified.Count > HoldMarkupRules.MaxPoints)
            simplified = MaskContour.Simplify(smoothed, SimplifyPixels * 4);
        if (simplified.Count < 3 || simplified.Count > HoldMarkupRules.MaxPoints) return contour;
        return simplified
            .Select(p => new NormalizedPoint(Math.Clamp(p.X / width, 0, 1), Math.Clamp(p.Y / height, 0, 1)))
            .ToImmutableArray();
    }

    /// <summary>The closed polygon resampled at an even spacing along its length (one pixel, or longer for a very long
    /// contour).</summary>
    private static List<(double X, double Y)> Resample(List<(double X, double Y)> ring, out double spacing)
    {
        var n = ring.Count;
        double perimeter = 0;
        for (var i = 0; i < n; i++) perimeter += Distance(ring[i], ring[(i + 1) % n]);
        spacing = Math.Max(1.0, perimeter / MaxSamples);
        var count = Math.Max(1, (int)Math.Round(perimeter / spacing));
        spacing = perimeter / count;
        var result = new List<(double X, double Y)>(count);
        var segment = 0;
        var start = 0.0;   // the distance along the contour where the current segment starts
        for (var s = 0; s < count; s++)
        {
            var target = s * spacing;
            var length = Distance(ring[segment], ring[(segment + 1) % n]);
            while (segment < n - 1 && start + length < target)
            {
                start += length;
                segment++;
                length = Distance(ring[segment], ring[(segment + 1) % n]);
            }
            var (a, b) = (ring[segment], ring[(segment + 1) % n]);
            var t = length > 0 ? Math.Clamp((target - start) / length, 0, 1) : 0;
            result.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
        }
        return result;
    }

    /// <summary>A circular Gaussian of σ samples over a closed ring of samples.</summary>
    private static List<(double X, double Y)> Gaussian(List<(double X, double Y)> ring, double sigma)
    {
        var n = ring.Count;
        var radius = Math.Min((int)Math.Ceiling(3 * sigma), (n - 1) / 2);
        var kernel = new double[radius + 1];
        double sum = 0;
        for (var k = 0; k <= radius; k++)
        {
            kernel[k] = Math.Exp(-k * k / (2 * sigma * sigma));
            sum += k == 0 ? kernel[k] : 2 * kernel[k];
        }
        var result = new List<(double X, double Y)>(n);
        for (var i = 0; i < n; i++)
        {
            double x = ring[i].X * kernel[0], y = ring[i].Y * kernel[0];
            for (var k = 1; k <= radius; k++)
            {
                var (a, b) = (ring[(i - k + n) % n], ring[(i + k) % n]);
                x += (a.X + b.X) * kernel[k];
                y += (a.Y + b.Y) * kernel[k];
            }
            result.Add((x / sum, y / sum));
        }
        return result;
    }

    private static double SignedArea(IReadOnlyList<(double X, double Y)> ring)
    {
        double a = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
