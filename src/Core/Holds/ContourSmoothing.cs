using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// D065 §2: the smoothing of a new hold's contour — a mask's pixel boundary («ёжик») turned into a smooth outline (a circle
/// stays a circle, an uneven hold becomes a smooth «amoeba»). The contour is resampled every pixel along its length, smoothed
/// with a circular Gaussian whose width grows with <c>strength</c> and with the hold's size, scaled about its centroid back
/// to its area (a Gaussian shrinks a shape) and simplified with Douglas–Peucker. Strength 0 returns the contour unchanged.
/// Applied once, when a hold is created; a saved contour and a contour edited by hand are never smoothed again.
/// <para>D075 §2 (the owner): the smoothing removes the teeth — clusters of close vertices — but never cuts a corner that stands
/// alone: on the contour simplified at <see cref="CornerToleranceShare"/> of the hold's diameter, a vertex turning by at least
/// <see cref="CornerAngleDegrees"/> whose two sides are both at least <see cref="CornerSideShare"/> of the diameter is a corner;
/// the Gaussian never averages across a corner, so a triangle keeps its tips.</para>
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

    /// <summary>D075 §2: the simplification that finds the corners, as a share of the hold's diameter (at least 1.5 px).</summary>
    public const double CornerToleranceShare = 0.04;

    /// <summary>D075 §2: the least turn of a corner, in degrees.</summary>
    public const double CornerAngleDegrees = 60;   // a circle simplified at 4 % turns about 46° a vertex: never a corner

    /// <summary>D075 §2: the shortest side on each side of a corner, as a share of the hold's diameter (shorter: a tooth).</summary>
    public const double CornerSideShare = 0.15;

    /// <summary>D075 §2: … and at least this many pixels — over a small hold's pixel steps the contour never turns like a corner.</summary>
    public const double MinCornerSidePixels = 6;

    /// <summary>D075 §2: a hold smaller than this (its equivalent diameter, px) has no corners — a few pixels across, its
    /// pixel steps are all it has; it is smoothed as before.</summary>
    public const double MinCornerHoldPixels = 30;

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
        var corners = Corners(samples, diameter);
        var smoothed = Gaussian(samples, sigmaSamples, corners);

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

    /// <summary>
    /// D075 §2: the samples that are corners standing alone — the vertices of the contour simplified at
    /// <see cref="CornerToleranceShare"/> × <paramref name="diameter"/> that turn by at least <see cref="CornerAngleDegrees"/>
    /// with both sides at least <see cref="CornerSideShare"/> × <paramref name="diameter"/> long (the nearest sample each).
    /// </summary>
    public static HashSet<int> Corners(List<(double X, double Y)> samples, double diameter)
    {
        var corners = new HashSet<int>();
        if (diameter < MinCornerHoldPixels) return corners;
        var polygon = MaskContour.Simplify(samples, Math.Max(1.5, CornerToleranceShare * diameter));
        var m = polygon.Count;
        if (m < 3) return corners;
        var minSide = Math.Max(MinCornerSidePixels, CornerSideShare * diameter);
        var cos = Math.Cos(CornerAngleDegrees * Math.PI / 180);
        for (var i = 0; i < m; i++)
        {
            var (prev, here, next) = (polygon[(i - 1 + m) % m], polygon[i], polygon[(i + 1) % m]);
            double ax = here.X - prev.X, ay = here.Y - prev.Y, bx = next.X - here.X, by = next.Y - here.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < minSide || lb < minSide) continue;              // a short side: a tooth, smoothed
            if ((ax * bx + ay * by) / (la * lb) > cos) continue;     // turning less than the corner angle
            var nearest = 0;
            var best = double.MaxValue;
            for (var s = 0; s < samples.Count; s++)
            {
                var d = (samples[s].X - here.X) * (samples[s].X - here.X) + (samples[s].Y - here.Y) * (samples[s].Y - here.Y);
                if (d < best) (best, nearest) = (d, s);
            }
            // The turn measured on the contour itself, between its points a side's length before and after: a bump of a
            // round hold turns little there, a tip keeps its angle.
            var (before, after) = (Along(samples, nearest, -minSide), Along(samples, nearest, minSide));
            double cx = here.X - before.X, cy = here.Y - before.Y, dx = after.X - here.X, dy = after.Y - here.Y;
            double lc = Math.Sqrt(cx * cx + cy * cy), ld = Math.Sqrt(dx * dx + dy * dy);
            if (lc == 0 || ld == 0 || (cx * dx + cy * dy) / (lc * ld) > cos) continue;
            corners.Add(nearest);
        }
        return corners;
    }

    /// <summary>The point <paramref name="distance"/> along the closed contour from its point <paramref name="index"/>
    /// (negative: backwards).</summary>
    private static (double X, double Y) Along(List<(double X, double Y)> ring, int index, double distance)
    {
        var n = ring.Count;
        var step = Math.Sign(distance);
        var left = Math.Abs(distance);
        var i = index;
        for (var guard = 0; guard < n; guard++)
        {
            var j = ((i + step) % n + n) % n;
            var length = Distance(ring[i], ring[j]);
            if (length >= left)
            {
                var t = length > 0 ? left / length : 0;
                return (ring[i].X + (ring[j].X - ring[i].X) * t, ring[i].Y + (ring[j].Y - ring[i].Y) * t);
            }
            left -= length;
            i = j;
        }
        return ring[i];
    }

    /// <summary>A circular Gaussian of σ samples over a closed ring of samples, never averaging across a corner
    /// (<paramref name="corners"/>: a corner is not averaged — only the area's rescale moves it, by a fraction of a pixel; the
    /// samples beside it average only their own side).</summary>
    private static List<(double X, double Y)> Gaussian(List<(double X, double Y)> ring, double sigma, IReadOnlySet<int>? corners = null)
    {
        var n = ring.Count;
        var radius = Math.Min((int)Math.Ceiling(3 * sigma), (n - 1) / 2);
        var kernel = new double[radius + 1];
        for (var k = 0; k <= radius; k++) kernel[k] = Math.Exp(-k * k / (2 * sigma * sigma));
        var result = new List<(double X, double Y)>(n);
        for (var i = 0; i < n; i++)
        {
            if (corners is not null && corners.Contains(i))
            {
                result.Add(ring[i]);
                continue;
            }
            double x = ring[i].X * kernel[0], y = ring[i].Y * kernel[0], sum = kernel[0];
            foreach (var direction in new[] { -1, 1 })
            {
                for (var k = 1; k <= radius; k++)
                {
                    var j = ((i + direction * k) % n + n) % n;
                    x += ring[j].X * kernel[k];
                    y += ring[j].Y * kernel[k];
                    sum += kernel[k];
                    if (corners is not null && corners.Contains(j)) break;   // the corner itself counts, nothing past it
                }
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
