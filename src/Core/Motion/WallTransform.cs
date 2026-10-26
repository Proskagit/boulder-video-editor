using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Motion;

/// <summary>
/// A projective transform of the wall between two frames of one video, in the normalized coordinates of the
/// display-oriented source frame (D019 / D034 §6: 0..1 across the width and the height): a 3 × 3 matrix, row-major,
/// applied as <c>(x', y') = ((M00 x + M01 y + M02) / w, (M10 x + M11 y + M12) / w)</c> with
/// <c>w = M20 x + M21 y + M22</c>. A similarity (shift, uniform scale, rotation — in pixels, so not uniform in normalized
/// units of a non-square frame) and a homography are both represented this way. Immutable.
/// </summary>
public readonly record struct WallTransform(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static readonly WallTransform Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public bool IsFinite =>
        double.IsFinite(M00) && double.IsFinite(M01) && double.IsFinite(M02) &&
        double.IsFinite(M10) && double.IsFinite(M11) && double.IsFinite(M12) &&
        double.IsFinite(M20) && double.IsFinite(M21) && double.IsFinite(M22);

    public (double X, double Y) Apply(double x, double y)
    {
        var w = M20 * x + M21 * y + M22;
        return ((M00 * x + M01 * y + M02) / w, (M10 * x + M11 * y + M12) / w);
    }

    public NormalizedPoint Apply(NormalizedPoint point)
    {
        var (x, y) = Apply(point.X, point.Y);
        return new NormalizedPoint(x, y);
    }

    /// <summary>This transform followed by <paramref name="next"/> (the matrix product <c>next · this</c>).</summary>
    public WallTransform Then(WallTransform next) => new WallTransform(
        next.M00 * M00 + next.M01 * M10 + next.M02 * M20,
        next.M00 * M01 + next.M01 * M11 + next.M02 * M21,
        next.M00 * M02 + next.M01 * M12 + next.M02 * M22,
        next.M10 * M00 + next.M11 * M10 + next.M12 * M20,
        next.M10 * M01 + next.M11 * M11 + next.M12 * M21,
        next.M10 * M02 + next.M11 * M12 + next.M12 * M22,
        next.M20 * M00 + next.M21 * M10 + next.M22 * M20,
        next.M20 * M01 + next.M21 * M11 + next.M22 * M21,
        next.M20 * M02 + next.M21 * M12 + next.M22 * M22).Normalized();

    /// <summary>The inverse (adjugate / determinant), normalized; not finite for a singular matrix.</summary>
    public WallTransform Inverse()
    {
        var c00 = M11 * M22 - M12 * M21;
        var c01 = M02 * M21 - M01 * M22;
        var c02 = M01 * M12 - M02 * M11;
        var c10 = M12 * M20 - M10 * M22;
        var c11 = M00 * M22 - M02 * M20;
        var c12 = M02 * M10 - M00 * M12;
        var c20 = M10 * M21 - M11 * M20;
        var c21 = M01 * M20 - M00 * M21;
        var c22 = M00 * M11 - M01 * M10;
        var det = M00 * c00 + M01 * c10 + M02 * c20;
        return new WallTransform(c00 / det, c01 / det, c02 / det, c10 / det, c11 / det, c12 / det, c20 / det, c21 / det, c22 / det)
            .Normalized();
    }

    /// <summary>The same transform with <see cref="M22"/> = 1 (unchanged when it is 0 or not finite).</summary>
    public WallTransform Normalized() =>
        M22 == 0 || !double.IsFinite(M22) || M22 == 1
            ? this
            : new WallTransform(M00 / M22, M01 / M22, M02 / M22, M10 / M22, M11 / M22, M12 / M22, M20 / M22, M21 / M22, 1);

    /// <summary>The element-wise blend of two normalized transforms at <paramref name="t"/> (0 → <paramref name="a"/>,
    /// 1 → <paramref name="b"/>): for the short gaps between analysed frames, where the motion is small and smooth.</summary>
    public static WallTransform Lerp(WallTransform a, WallTransform b, double t)
    {
        a = a.Normalized();
        b = b.Normalized();
        double L(double x, double y) => x + (y - x) * t;
        return new WallTransform(L(a.M00, b.M00), L(a.M01, b.M01), L(a.M02, b.M02), L(a.M10, b.M10), L(a.M11, b.M11),
            L(a.M12, b.M12), L(a.M20, b.M20), L(a.M21, b.M21), L(a.M22, b.M22)).Normalized();
    }

    /// <summary>A transform given in the pixels of a <paramref name="width"/> × <paramref name="height"/> frame,
    /// expressed in normalized coordinates: <c>S⁻¹ · H · S</c> with <c>S = diag(width, height, 1)</c>.</summary>
    public static WallTransform FromPixels(WallTransform pixels, int width, int height)
    {
        double w = width, h = height;
        return new WallTransform(
            pixels.M00, pixels.M01 * h / w, pixels.M02 / w,
            pixels.M10 * w / h, pixels.M11, pixels.M12 / h,
            pixels.M20 * w, pixels.M21 * h, pixels.M22).Normalized();
    }
}
