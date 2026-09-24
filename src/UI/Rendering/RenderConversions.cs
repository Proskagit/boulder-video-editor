using AiVideoEditor.Core.Composition;
using Avalonia;

namespace AiVideoEditor.UI.Rendering;

/// <summary>The only place composition geometry meets Avalonia types.</summary>
public static class RenderConversions
{
    /// <summary>
    /// <see cref="Affine2D"/> uses column vectors (<c>x' = A·x + B·y + Tx</c>, <c>y' = C·x + D·y + Ty</c>);
    /// Avalonia's <see cref="Matrix"/> uses row vectors (<c>x' = x·M11 + y·M21 + M31</c>,
    /// <c>y' = x·M12 + y·M22 + M32</c>), hence the transposition.
    /// </summary>
    public static Matrix ToMatrix(Affine2D m) => new(m.A, m.C, m.B, m.D, m.Tx, m.Ty);

    public static Rect ToRect(RectD r) => new(r.X, r.Y, r.Width, r.Height);
}
