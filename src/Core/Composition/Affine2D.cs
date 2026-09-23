namespace AiVideoEditor.Core.Composition;

/// <summary>A size in whole pixels (canvas or source picture).</summary>
public readonly record struct FrameSize(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>An axis-aligned rectangle in double precision.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>A point in double precision.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// 2D affine transform with column-vector semantics:
/// <c>x' = A·x + B·y + Tx</c>, <c>y' = C·x + D·y + Ty</c>. Y points down (screen/image convention),
/// so a rotation by a positive angle turns clockwise on screen. Renderers convert this to their own
/// matrix type (e.g. Avalonia's row-vector Matrix(M11 = A, M12 = C, M21 = B, M22 = D, M31 = Tx, M32 = Ty)).
/// </summary>
public readonly record struct Affine2D(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Affine2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine2D Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine2D Scaling(double factor) => new(factor, 0, 0, factor, 0, 0);

    /// <summary>Rotation by <paramref name="degrees"/> (clockwise on screen). Multiples of 90° use
    /// exact 0/±1 coefficients, so axis-aligned results carry no trigonometric rounding.</summary>
    public static Affine2D Rotation(double degrees)
    {
        var (sin, cos) = SinCos(degrees);
        return new Affine2D(cos, -sin, sin, cos, 0, 0);
    }

    /// <summary>sin/cos of <paramref name="degrees"/>; exact for multiples of 90°.</summary>
    public static (double Sin, double Cos) SinCos(double degrees)
    {
        if (QuarterTurns(degrees) is { } quarters)
        {
            return quarters switch
            {
                0 => (0.0, 1.0),
                1 => (1.0, 0.0),
                2 => (0.0, -1.0),
                _ => (-1.0, 0.0)
            };
        }
        return Math.SinCos(degrees * (Math.PI / 180.0));
    }

    /// <summary>0–3 when <paramref name="degrees"/> is an exact multiple of 90°, otherwise null.</summary>
    public static int? QuarterTurns(double degrees)
    {
        if (!double.IsFinite(degrees) || Math.IEEERemainder(degrees, 90.0) != 0) return null;
        var quarters = (long)Math.Round(degrees / 90.0) % 4;
        return (int)(quarters < 0 ? quarters + 4 : quarters);
    }

    /// <summary>Returns the transform that applies <paramref name="inner"/> first and this second.</summary>
    public Affine2D Compose(Affine2D inner) => new(
        A * inner.A + B * inner.C,
        A * inner.B + B * inner.D,
        C * inner.A + D * inner.C,
        C * inner.B + D * inner.D,
        A * inner.Tx + B * inner.Ty + Tx,
        C * inner.Tx + D * inner.Ty + Ty);

    public PointD Apply(PointD p) => new(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty);

    public double Determinant => A * D - B * C;

    /// <summary>The inverse transform; throws when the transform is singular.</summary>
    public Affine2D Invert()
    {
        var det = Determinant;
        if (det == 0 || !double.IsFinite(det)) throw new InvalidOperationException("The transform is not invertible.");
        var a = D / det;
        var b = -B / det;
        var c = -C / det;
        var d = A / det;
        return new Affine2D(a, b, c, d, -(a * Tx + b * Ty), -(c * Tx + d * Ty));
    }
}
