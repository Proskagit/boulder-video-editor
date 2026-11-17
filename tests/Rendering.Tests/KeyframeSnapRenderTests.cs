using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D063 in pixels: a keyframed picture drawn on a frame between its keyframes lands where the interpolation puts it, the
/// same in the export and the Preview; a picture snapped to the canvas edges leaves no thin black line (export and Preview)
/// where the unsnapped one did; the route plate and the heart rate in one corner never overlap, in every corner, landscape
/// and portrait.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class KeyframeSnapRenderTests
{
    private static readonly FrameSize Landscape = new(320, 180);

    private static DecodedFrame Bright(int w, int h) =>
        Scene.Frame(w, h, (x, y) => ((byte)(64 + x * 191 / (w - 1)), (byte)(64 + y * 191 / (h - 1)), 160, 255));

    private static int DarkPixels(Image image)
    {
        var n = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var p = image.At(x, y);
            if (p.R < 48 && p.G < 48 && p.B < 48) n++;
        }
        return n;
    }

    private static (Image Export, Image Preview) Draw(PictureLayer picture, FrameSize source)
    {
        var layer = new ResolvedLayer(picture, Bright(source.Width, source.Height));
        return (Render.Export(Scene.Plan(Landscape, layer)),
            Render.Preview(Landscape, Landscape.Width, Landscape.Height, new[] { Scene.AsPreview(layer) }));
    }

    [Fact]
    public void A_keyframed_picture_moves_between_its_keyframes_the_same_in_the_export_and_the_Preview()
    {
        var keys = new TransformKeyframes(ImmutableArray.Create(
            new TransformKeyframe(Guid.NewGuid(), MediaTime.Zero, -80, 0, 0.5),
            new TransformKeyframe(Guid.NewGuid(), MediaTime.FromSeconds(1), 80, 0, 0.5)));
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Video, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero)
        {
            Visual = VisualProperties.Default, SourceSize = Landscape, Keyframes = keys
        };
        foreach (var (frame, expectedCentre) in new[] { (0L, 80.0), (12L, 80 + 160 * 12 / 25.0 - 0), (25L, 240.0) })
        {
            var visual = span.VisualAt(frame, Scene.Rate);
            var picture = new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(Landscape, Landscape, visual)) { Visual = visual };
            var (export, preview) = Draw(picture, Landscape);
            // The half-size picture is 160 px wide: its bright columns are centred where the keyframes put it.
            var columns = Enumerable.Range(0, Landscape.Width).Where(x => export.At(x, 90).R >= 48).ToList();
            Assert.Equal(expectedCentre, (columns.First() + columns.Last() + 1) / 2.0, 0);
            for (var x = 0; x < Landscape.Width; x++)
                Assert.InRange(Math.Abs(export.At(x, 90).R - preview.At(x, 90).R), 0, 2);
        }
    }

    [Fact]
    public void A_picture_scaled_a_hair_short_of_the_canvas_shows_black_lines_until_it_is_snapped()
    {
        var source = new FrameSize(640, 360);
        var short_ = VisualProperties.Default with { Scale = 0.985 };            // 2.4 px short horizontally
        var (before, _) = Draw(Scene.Picture(Landscape, source, short_), source);
        Assert.True(DarkPixels(before) > 0);

        var snapped = CanvasSnap.Scale(Landscape, source, short_, threshold: 4);
        Assert.True(snapped.Snapped);
        var (export, preview) = Draw(Scene.Picture(Landscape, source, snapped.Visual), source);
        Assert.Equal(0, DarkPixels(export));
        Assert.Equal(0, DarkPixels(preview));
    }

    [Fact]
    public void A_picture_moved_a_little_off_the_edge_is_snapped_back_without_a_black_column()
    {
        var source = new FrameSize(640, 360);
        var nudged = VisualProperties.Default with { Scale = 1.1, PositionX = 18.6 };   // the left edge 2.6 px inside the canvas
        var (before, _) = Draw(Scene.Picture(Landscape, source, nudged), source);
        Assert.True(DarkPixels(before) > 0);

        var snapped = CanvasSnap.Move(Landscape, source, nudged, threshold: 4);
        Assert.Equal(CropEdges.Left, snapped.Edges);
        var (export, preview) = Draw(Scene.Picture(Landscape, source, snapped.Visual), source);
        Assert.Equal(0, DarkPixels(export));
        Assert.Equal(0, DarkPixels(preview));
    }

    // --- the plates (D063 §C) ------------------------------------------------------------------------------------------

    private static Image DrawBoulder(FrameSize canvas, OverlayCorner plate, OverlayCorner heart)
    {
        var background = Scene.Picture(canvas, canvas, VisualProperties.Default);
        var frame = Scene.Solid(canvas.Width, canvas.Height, 128, 128, 128);
        var fields = TemplateRules.Defaults(TemplateKind.Boulder) with
        {
            Route = "12", Grade = "7A", Status = BoulderStatus.Top, PlateCorner = plate, Corner = heart, ShowHeartRate = true,
            Look = HeartRateLook.Plain
        };
        var (intro, outro) = TemplateRules.Animation(TemplateKind.Boulder);
        var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), TemplateKind.Boulder, fields, MediaTime.Zero);
        var template = new TemplateLayer(Guid.NewGuid(), span, new TemplateClock(3, 5, intro, outro, 3, 75))
        {
            HeartRate = 142
        };
        return Render.Export(Scene.Plan(canvas, new ResolvedLayer(background, frame), new ResolvedLayer(template, null)));
    }

    private static (int Left, int Top, int Right, int Bottom)? Box(Image image, Func<byte, byte, byte, bool> match)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var (red, green, blue, _) = image.At(x, y);
            if (!match(red, green, blue)) continue;
            (l, r, t, b) = (Math.Min(l, x), Math.Max(r, x), Math.Min(t, y), Math.Max(b, y));
        }
        return r < 0 ? null : (l, t, r, b);
    }

    [Theory]
    [InlineData(960, 540)]
    [InlineData(540, 960)]
    public void In_one_corner_the_heart_rate_never_covers_the_plate_and_stands_nearer_the_centre(int width, int height)
    {
        var canvas = new FrameSize(width, height);
        foreach (var corner in Enum.GetValues<OverlayCorner>())
        {
            var image = DrawBoulder(canvas, corner, corner);
            // The heart is red (#FF1744), the plate navy (#0D1B2E) with its yellow TOP block.
            var heart = Box(image, (r, g, b) => r > 200 && g < 80 && b < 110 && b > 30)!.Value;
            var plate = Box(image, (r, g, b) => r < 30 && g < 45 && b > 30 && b < 70)!.Value;
            var overlap = heart.Left <= plate.Right && plate.Left <= heart.Right && heart.Top <= plate.Bottom && plate.Top <= heart.Bottom;
            Assert.False(overlap, $"{corner} {width}×{height}: heart {heart} over the plate {plate}");
            var top = CornerOverlayLayout.IsTop(corner);
            Assert.True(top ? heart.Top > plate.Bottom : heart.Bottom < plate.Top, $"{corner}: the heart rate is not nearer the centre");
            Assert.True(CornerOverlayLayout.IsLeft(corner) ? plate.Left < width / 2 : plate.Right > width / 2, $"{corner}: the plate is not in its corner");
            Assert.True(top ? plate.Top < height / 2 : plate.Bottom > height / 2, $"{corner}: the plate is not in its corner");
        }
    }

    [Fact]
    public void A_portrait_plate_is_larger_than_the_landscape_one_relative_to_the_short_side_and_the_landscape_heart_rate_smaller()
    {
        var landscape = DrawBoulder(new FrameSize(960, 540), OverlayCorner.BottomLeft, OverlayCorner.TopRight);
        var portrait = DrawBoulder(new FrameSize(540, 960), OverlayCorner.BottomLeft, OverlayCorner.TopRight);
        static int Height((int Left, int Top, int Right, int Bottom) b) => b.Bottom - b.Top + 1;
        var navy = new Func<byte, byte, byte, bool>((r, g, b) => r < 30 && g < 45 && b > 30 && b < 70);
        var red = new Func<byte, byte, byte, bool>((r, g, b) => r > 200 && g < 80 && b < 110 && b > 30);
        Assert.True(Height(Box(portrait, navy)!.Value) > Height(Box(landscape, navy)!.Value) * 1.25);
        // The heart: 1/1.5 of before on landscape (D063 amendment 1), 0.85 on portrait, relative to the short side.
        Assert.InRange(Height(Box(portrait, red)!.Value) / (double)Height(Box(landscape, red)!.Value), 0.85 * 1.5 - 0.15, 0.85 * 1.5 + 0.15);
    }
}
