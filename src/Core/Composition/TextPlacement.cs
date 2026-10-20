using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>A row of the 3 × 3 placement grid (D043 §3, Q4).</summary>
public enum TextGridRow
{
    Top,
    Middle,
    Bottom
}

/// <summary>A column of the 3 × 3 placement grid (D043 §3, Q4).</summary>
public enum TextGridColumn
{
    Left,
    Centre,
    Right
}

/// <summary>
/// The placement grid (D043 §3): the position (<see cref="VisualProperties.PositionX"/> / <c>Y</c>, canvas pixels from the
/// centre) that puts a text's drawn box at <see cref="MarginFactor"/> × the canvas's short side from the canvas edges of its
/// cell — centred on the axis of the middle row / centre column. The box is the painter's text box
/// (<c>TextDraw</c>'s layout rule, measured by the renderer) grown by what is drawn around it: the background's padding,
/// or the outline's width when only the outline is on; then scaled and rotated as the text is, and its axis-aligned
/// bounds used. Only the position changes — the alignment inside the box, the scale and the rotation stay.
/// </summary>
public static class TextPlacement
{
    /// <summary>The safe margin, × the canvas's short side.</summary>
    public const double MarginFactor = 0.08;

    /// <summary>How far the drawing reaches past the text box on every side, canvas pixels before the scale.</summary>
    public static double DrawnExtent(TextProperties text) =>
        text.Background.IsEnabled ? TextBackground.PaddingFactor * text.FontSize
        : text.Outline.IsEnabled ? text.Outline.Thickness * text.FontSize
        : 0;

    /// <summary>The position of the cell <paramref name="row"/> × <paramref name="column"/> for a text box of
    /// <paramref name="boxWidth"/> × <paramref name="boxHeight"/> (the painter's layout, local units = canvas pixels at
    /// scale 1).</summary>
    public static (double X, double Y) Position(FrameSize canvas, TextProperties text, VisualProperties visual,
        double boxWidth, double boxHeight, TextGridRow row, TextGridColumn column)
    {
        if (!canvas.IsValid) throw new ArgumentOutOfRangeException(nameof(canvas), "The canvas must have a positive size.");
        var extent = DrawnExtent(text);
        var w = Math.Max(0, boxWidth) + 2 * extent;
        var h = Math.Max(0, boxHeight) + 2 * extent;
        var angle = visual.RotationDegrees * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle)));
        var boundsW = visual.Scale * (w * cos + h * sin);
        var boundsH = visual.Scale * (w * sin + h * cos);
        var margin = MarginFactor * Math.Min(canvas.Width, canvas.Height);

        var x = column switch
        {
            TextGridColumn.Left => -canvas.Width / 2.0 + margin + boundsW / 2,
            TextGridColumn.Right => canvas.Width / 2.0 - margin - boundsW / 2,
            _ => 0
        };
        var y = row switch
        {
            TextGridRow.Top => -canvas.Height / 2.0 + margin + boundsH / 2,
            TextGridRow.Bottom => canvas.Height / 2.0 - margin - boundsH / 2,
            _ => 0
        };
        return (x, y);
    }
}
