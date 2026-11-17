using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D063 §C: the sizes and places of «Боулдер»'s route plate and of the heart rate in the canvas corners — one computation for
/// the Preview and the export (the template painter draws through it).
/// <list type="bullet">
/// <item><b>Sizes.</b> Every size is in 1080-units (× the canvas's short side / 1080, as before) times a factor of the
/// canvas's shape: 1 on a landscape or square canvas (the look of before), on a portrait canvas the route plate up to
/// <see cref="PortraitPlateFactor"/> and the heart rate down to <see cref="PortraitHeartFactor"/> — a continuous function of
/// the aspect ratio (<see cref="Portraitness"/>: 0 at 1 : 1 and wider, 1 at 9 : 16 and taller), never of a resolution.</item>
/// <item><b>The route plate</b> stands in its own corner (<see cref="TemplateClip.PlateCorner"/>) at the margins of before
/// — left / right <c>60 → 48</c>, bottom <c>64 → 260</c> (above a phone's controls), top <c>64 → 200</c> units, landscape →
/// portrait.</item>
/// <item><b>The heart rate</b> stands in its corner at the placement grid's safe margin (D043 §3, as before). When the route
/// plate is in the same corner, the heart rate is beside it nearer the canvas centre — above the plate in a bottom corner,
/// below it in a top one, aligned with the plate's outer edge — so it never covers the plate or its text; in another corner
/// neither moves.</item>
/// </list>
/// </summary>
public static class CornerOverlayLayout
{
    public const double PortraitPlateFactor = 1.35;
    public const double PortraitHeartFactor = 0.85;

    /// <summary>The route plate's height in 1080-units (× <see cref="PlateFactor"/>).</summary>
    public const double PlateHeightUnits = 62;

    /// <summary>The gap between the plate and the heart rate standing beside it, in 1080-units.</summary>
    public const double StackGapUnits = 18;

    /// <summary>A 1080-unit in canvas pixels: the canvas's short side / 1080.</summary>
    public static double Unit(FrameSize canvas) => Math.Min(canvas.Width, canvas.Height) / 1080.0;

    /// <summary>0 for a landscape or square canvas, 1 for 9 : 16 and taller, linear in the height / width ratio between.</summary>
    public static double Portraitness(FrameSize canvas)
    {
        var ratio = canvas.Height / (double)canvas.Width;
        return Math.Clamp((ratio - 1) / (16.0 / 9.0 - 1), 0, 1);
    }

    /// <summary>The route plate's size factor (on top of the 1080-unit).</summary>
    public static double PlateFactor(FrameSize canvas) => 1 + (PortraitPlateFactor - 1) * Portraitness(canvas);

    /// <summary>The heart rate's size factor (on top of the 1080-unit).</summary>
    public static double HeartFactor(FrameSize canvas) => 1 + (PortraitHeartFactor - 1) * Portraitness(canvas);

    public static bool IsLeft(OverlayCorner corner) => corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft;
    public static bool IsTop(OverlayCorner corner) => corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;

    private static double Lerp(double landscape, double portrait, double p) => landscape + (portrait - landscape) * p;

    /// <summary>The route plate's row in <paramref name="corner"/>: its outer x (the left edge in a left corner, the right
    /// edge in a right one), its top and its height, canvas pixels.</summary>
    public static (double OuterX, double Top, double Height) PlateRow(FrameSize canvas, OverlayCorner corner)
    {
        var (u, p) = (Unit(canvas), Portraitness(canvas));
        var height = PlateHeightUnits * u * PlateFactor(canvas);
        var side = Lerp(60, 48, p) * u;
        var outerX = IsLeft(corner) ? side : canvas.Width - side;
        var top = IsTop(corner) ? Lerp(64, 200, p) * u : canvas.Height - Lerp(64, 260, p) * u - height;
        return (outerX, top, height);
    }

    /// <summary>
    /// The heart rate's block of <paramref name="width"/> × <paramref name="height"/> canvas pixels (top-left) in
    /// <paramref name="corner"/>; with <paramref name="plateCorner"/> the same corner, beside the route plate nearer the centre.
    /// </summary>
    public static RectD HeartRate(FrameSize canvas, OverlayCorner corner, double width, double height, OverlayCorner? plateCorner)
    {
        if (plateCorner == corner)
        {
            var (outerX, top, plateHeight) = PlateRow(canvas, corner);
            var gap = StackGapUnits * Unit(canvas);
            var x = IsLeft(corner) ? outerX : outerX - width;
            var y = IsTop(corner) ? top + plateHeight + gap : top - gap - height;
            return new RectD(x, y, width, height);
        }
        var margin = TextPlacement.MarginFactor * Math.Min(canvas.Width, canvas.Height);
        return new RectD(IsLeft(corner) ? margin : canvas.Width - margin - width,
            IsTop(corner) ? margin : canvas.Height - margin - height, width, height);
    }
}
