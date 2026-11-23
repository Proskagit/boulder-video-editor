using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D063 §C: the sizes and places of «Боулдер»'s route plate and of the heart rate in the canvas corners — one computation for
/// the Preview and the export (the template painter draws through it).
/// <list type="bullet">
/// <item><b>Sizes.</b> Every size is in 1080-units (× the canvas's short side / 1080, as before) times a factor of the
/// canvas's shape: the route plate 1 on a landscape or square canvas (the look of before) up to <see cref="PortraitPlateFactor"/>
/// on a portrait one; the heart rate <see cref="LandscapeHeartFactor"/> (D063 amendment 1) up to <see cref="PortraitHeartFactor"/> — a continuous function of
/// the aspect ratio (<see cref="Portraitness"/>: 0 at 1 : 1 and wider, 1 at 9 : 16 and taller), never of a resolution.</item>
/// <item><b>The route plate</b> stands in its own corner (<see cref="TemplateClip.PlateCorner"/>) at the margins of before
/// — left / right <c>60 → 48</c>, bottom <c>64 → 176</c> (above a phone's controls; 260 before D063 amendment 1), top <c>64 → 200</c> units, landscape →
/// portrait — and in a bottom corner one plate's height higher still (D079: YouTube's description at the bottom covered it); a top
/// corner stays where it was.</item>
/// <item><b>The heart rate</b> stands in its corner on the plate's line: centred on the row a plate has there, at the plate's
/// side margin (D063 amendment 1 — a plate bottom left and the heart rate bottom right on one line). When the route
/// plate is in the same corner, the heart rate is beside it nearer the canvas centre — above the plate in a bottom corner,
/// below it in a top one, aligned with the plate's outer edge — so it never covers the plate or its text.</item>
/// </list>
/// </summary>
public static class CornerOverlayLayout
{
    public const double PortraitPlateFactor = 1.35;
    public const double PortraitHeartFactor = 0.85;

    /// <summary>D063 amendment 1 (owner, 2026-11-17): the heart rate on a landscape canvas, 1.5 times smaller than before.</summary>
    public const double LandscapeHeartFactor = 1 / 1.5;

    /// <summary>The route plate's height in 1080-units (× <see cref="PlateFactor"/>).</summary>
    public const double PlateHeightUnits = 62;

    /// <summary>D079: how many plate heights a bottom corner's plate (and the heart rate on its row) stands above the margin.</summary>
    public const double BottomLiftPlates = 1;

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
    public static double HeartFactor(FrameSize canvas) => LandscapeHeartFactor + (PortraitHeartFactor - LandscapeHeartFactor) * Portraitness(canvas);

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
        // D063 amendment 1: on a portrait canvas one plate's height nearer the bottom than before (176 instead of 260).
        // D079: a bottom corner's plate one plate's height higher (YouTube's description covers the bottom of the picture); the
        // heart rate standing on this row or beside the plate moves with it.
        // D079: the row's outer edge (its top in a top corner, its bottom in a bottom one) on a whole pixel, wherever the lift
        // puts it — as the margins alone did on a 1080-high canvas.
        var top = IsTop(corner)
            ? Math.Round(Lerp(64, 200, p) * u)
            : Math.Round(canvas.Height - Lerp(64, 176, p) * u - BottomLiftPlates * height) - height;
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
        // D063 amendment 1: in another corner on the plate's line — centred on the row a plate has in this corner, at the
        // plate's side margin — so a plate bottom left and the heart rate bottom right stand on one line.
        var (x0, rowTop, rowHeight) = PlateRow(canvas, corner);
        return new RectD(IsLeft(corner) ? x0 : x0 - width, rowTop + rowHeight / 2 - height / 2, width, height);
    }
}
