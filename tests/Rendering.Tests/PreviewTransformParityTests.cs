using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 23 (D038): the pixels of the Preview's gestures. An anchored crop leaves every pixel of the part that stays
/// visible exactly where it was in the export (upright, rotated and scaled, a portrait picture on a landscape canvas); a
/// 2 × 2 composition moved and scaled by the gesture math renders the same in the Preview and the export; the text box the
/// overlay hit-tests is the painter's box.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class PreviewTransformParityTests
{
    private static readonly FrameSize Canvas = new(320, 180);

    /// <summary>A smooth gradient (red along x, green along y, blue constant): sampling it anywhere gives a predictable,
    /// interpolation-stable colour.</summary>
    private static DecodedFrame Gradient(int w, int h) =>
        Scene.Frame(w, h, (x, y) => ((byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), 128, 255));

    private static Image Export(FrameSize source, DecodedFrame frame, VisualProperties visual) =>
        Render.Export(Scene.Plan(Canvas, new ResolvedLayer(Scene.Picture(Canvas, source, visual), frame)));

    /// <summary>Pixels whose centre lies at least <paramref name="margin"/> canvas px inside <paramref name="frame"/>.</summary>
    private static IEnumerable<(int X, int Y)> Inside(TransformFrame frame, double margin)
    {
        var inverse = frame.Transform.Invert();
        var scale = Math.Sqrt(Math.Abs(frame.Transform.Determinant));
        var m = margin / scale;
        var r = frame.LocalRect;
        for (var y = 0; y < Canvas.Height; y++)
        for (var x = 0; x < Canvas.Width; x++)
        {
            var p = inverse.Apply(new PointD(x + 0.5, y + 0.5));
            if (p.X >= r.X + m && p.X <= r.Right - m && p.Y >= r.Y + m && p.Y <= r.Bottom - m) yield return (x, y);
        }
    }

    private static void AssertSame(Image a, Image b, IEnumerable<(int X, int Y)> pixels, int tolerance, int atLeast)
    {
        var n = 0;
        foreach (var (x, y) in pixels)
        {
            var p = a.At(x, y);
            var q = b.At(x, y);
            Assert.True(Math.Abs(p.R - q.R) <= tolerance && Math.Abs(p.G - q.G) <= tolerance && Math.Abs(p.B - q.B) <= tolerance,
                $"({x},{y}): {p} vs {q}");
            n++;
        }
        Assert.True(n >= atLeast, $"only {n} pixels compared");
    }

    public static IEnumerable<object[]> Crops() => new[]
    {
        new object[] { 320, 180, VisualProperties.Default, CropEdges.Left, 0.3, 0.5 },
        new object[] { 320, 180, VisualProperties.Default with { RotationDegrees = 30, Scale = 0.6, PositionX = 20 }, CropEdges.Right | CropEdges.Bottom, 0.7, 0.65 },
        new object[] { 180, 320, VisualProperties.Default, CropEdges.Top, 0.5, 0.4 },                    // portrait on landscape
        new object[] { 180, 320, VisualProperties.Default with { RotationDegrees = 90 }, CropEdges.Bottom, 0.5, 0.7 }
    };

    [Theory]
    [MemberData(nameof(Crops))]
    public void An_anchored_crop_leaves_the_visible_pixels_where_they_were(int sw, int sh, VisualProperties start, CropEdges edges, double u, double v)
    {
        var source = new FrameSize(sw, sh);
        var frame = Gradient(sw, sh);
        var cropped = PreviewTransformMath.AnchoredCrop(Canvas, source, start, edges, new PointD(u * sw, v * sh));
        Assert.NotEqual(start.Crop, cropped.Crop);

        var before = Export(source, frame, start);
        var after = Export(source, frame, cropped);

        var kept = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Canvas, source, cropped));
        AssertSame(before, after, Inside(kept, 1.5), tolerance: 2, atLeast: 300);
        // Outside the kept part (but inside the old picture) the cropped-away pixels are gone: black now.
        var old = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Canvas, source, start));
        var gone = Inside(old, 1.5).Except(Inside(kept, -1.5)).Take(50).ToList();
        Assert.NotEmpty(gone);
        foreach (var (x, y) in gone) Assert.Equal((0, 0, 0, 255), ((int, int, int, int))after.At(x, y));
    }

    [Fact]
    public void A_2x2_composition_moved_and_scaled_by_gestures_renders_the_same_in_the_Preview_and_the_export()
    {
        var colours = new (byte R, byte G, byte B)[] { (230, 40, 40), (40, 200, 60), (50, 70, 230), (240, 220, 40) };
        var layers = new List<ResolvedLayer>();
        for (var i = 0; i < 4; i++)
        {
            // From the default: a corner drag to half the size, then a move into the quarter (the gesture math).
            var frame0 = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Canvas, Canvas, VisualProperties.Default));
            var corner = frame0.HandlePoint(CropEdges.Right | CropEdges.Bottom);
            var c = frame0.Center;
            var half = PreviewTransformMath.ScaleFromCorner(VisualProperties.Default, c, corner,
                new PointD(c.X + (corner.X - c.X) / 2, c.Y + (corner.Y - c.Y) / 2));
            var moved = PreviewTransformMath.Move(half, i % 2 == 0 ? -80 : 80, i < 2 ? -45 : 45, keepAxis: false);
            Assert.Equal(0.5, moved.Scale);
            var (r, g, b) = colours[i];
            layers.Add(new ResolvedLayer(Scene.Picture(Canvas, Canvas, moved), Scene.Solid(Canvas.Width, Canvas.Height, r, g, b)));
        }

        var export = Render.Export(Scene.Plan(Canvas, layers.ToArray()));
        var preview = Render.Preview(Canvas, Canvas.Width, Canvas.Height, layers.Select(Scene.AsPreview));

        for (var i = 0; i < 4; i++)
        {
            var quarter = PreviewTransformMath.PictureFrame(((PictureLayer)layers[i].Layer).Geometry!);
            AssertSame(export, preview, Inside(quarter, 1.5), tolerance: 3, atLeast: 2000);
            var (x, y) = (i % 2 == 0 ? 80 : 240, i < 2 ? 45 : 135);
            var p = export.At(x, y);
            Assert.Equal((colours[i].R, colours[i].G, colours[i].B), (p.R, p.G, p.B));
        }
    }

    [Fact]
    public void The_overlay_hit_tests_a_text_by_the_painters_box()
    {
        var text = new TextProperties("Hold on", "Inter", 40, "#FFFFFF", TextAlignment.Center);
        var layer = Scene.Text(Canvas, text, VisualProperties.Default with { PositionX = 50, RotationDegrees = 20 });
        var layers = new[] { new LayerPicture(layer, LayerPictureState.Text) };
        var box = CompositionPainter.Layout(text);
        Assert.True(box.MaxTextWidth > 40);

        Assert.Equal(layer.ClipId, PreviewTransformOverlay.LayerAt(Canvas, layers, new PointD(210, 90)));   // the centre
        Assert.Null(PreviewTransformOverlay.LayerAt(Canvas, layers, new PointD(210, 90 + box.Height)));    // below the box
        Assert.Null(PreviewTransformOverlay.LayerAt(Canvas, ImmutableArray<LayerPicture>.Empty, new PointD(210, 90)));
    }
}
