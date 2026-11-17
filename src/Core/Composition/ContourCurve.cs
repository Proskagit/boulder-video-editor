using System.Collections.Immutable;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D065 §4: a hold's contour drawn as a smooth closed curve through its vertices instead of a broken line, flattened into
/// short straight pieces for the painter. A cubic Hermite curve with Catmull–Rom tangents scaled by the sides' lengths; at a
/// <b>corner</b> — a vertex where the contour turns by more than <see cref="CornerDegrees"/> — the tangent is zero, so a corner
/// stays a corner and a straight side between two corners stays straight (a rectangle or a hand-made polygon is drawn exactly
/// as its vertices say, never bulging out). The same curve in the Preview, the export and the markup mode; the saved vertices
/// never change. A side shorter than <see cref="TargetPiecePixels"/> on the target stays one straight piece, so a dense contour
/// (hundreds of vertices from an earlier version) is drawn as before.
/// </summary>
public static class ContourCurve
{
    /// <summary>The length of one flattened piece on the target, in pixels.</summary>
    public const double TargetPiecePixels = 3.0;

    /// <summary>The most pieces one side of the polygon is cut into.</summary>
    public const int MaxPiecesPerSide = 24;

    /// <summary>A turn sharper than this at a vertex makes it a corner.</summary>
    public const double CornerDegrees = 30;

    private static readonly double CornerCos = Math.Cos(CornerDegrees * Math.PI / 180);

    /// <summary>The closed curve through <paramref name="polygon"/>, flattened; <paramref name="targetScale"/> is the target's
    /// pixels per unit of the polygon. A polygon of fewer than three vertices is returned as it is.</summary>
    public static ImmutableArray<PointD> Flatten(ImmutableArray<PointD> polygon, double targetScale)
    {
        var n = polygon.Length;
        if (n < 3 || !(targetScale > 0) || double.IsInfinity(targetScale)) return polygon;

        // The tangent at each vertex per unit of length along the contour (zero at a corner).
        var tangents = new PointD[n];
        for (var i = 0; i < n; i++)
        {
            var (prev, p, next) = (polygon[(i - 1 + n) % n], polygon[i], polygon[(i + 1) % n]);
            var (d0, d1) = (Distance(prev, p), Distance(p, next));
            if (!(d0 > 0) || !(d1 > 0)) continue;
            var cos = ((p.X - prev.X) * (next.X - p.X) + (p.Y - prev.Y) * (next.Y - p.Y)) / (d0 * d1);
            if (cos < CornerCos) continue;   // a corner
            var sum = d0 + d1;
            tangents[i] = new PointD((next.X - prev.X) / sum, (next.Y - prev.Y) / sum);
        }

        var result = ImmutableArray.CreateBuilder<PointD>(n * 2);
        for (var i = 0; i < n; i++)
        {
            var (p1, p2) = (polygon[i], polygon[(i + 1) % n]);
            result.Add(p1);
            var length = Distance(p1, p2);
            var pieces = Math.Clamp((int)Math.Ceiling(length * targetScale / TargetPiecePixels), 1, MaxPiecesPerSide);
            if (pieces == 1) continue;
            var (t1, t2) = (tangents[i], tangents[(i + 1) % n]);
            if (t1 == default && t2 == default) continue;   // a straight side between two corners
            var (m1, m2) = (new PointD(t1.X * length, t1.Y * length), new PointD(t2.X * length, t2.Y * length));
            for (var k = 1; k < pieces; k++)
                result.Add(Hermite(p1, m1, p2, m2, (double)k / pieces));
        }
        return result.ToImmutable();
    }

    /// <summary>The cubic Hermite point at <paramref name="u"/> (0…1) from <paramref name="p1"/> (tangent
    /// <paramref name="m1"/>) to <paramref name="p2"/> (tangent <paramref name="m2"/>).</summary>
    public static PointD Hermite(PointD p1, PointD m1, PointD p2, PointD m2, double u)
    {
        var (u2, u3) = (u * u, u * u * u);
        var h00 = 2 * u3 - 3 * u2 + 1;
        var h10 = u3 - 2 * u2 + u;
        var h01 = -2 * u3 + 3 * u2;
        var h11 = u3 - u2;
        return new PointD(h00 * p1.X + h10 * m1.X + h01 * p2.X + h11 * m2.X, h00 * p1.Y + h10 * m1.Y + h01 * p2.Y + h11 * m2.Y);
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
