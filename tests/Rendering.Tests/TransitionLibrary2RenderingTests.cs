using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>D069 §8: the new transitions' parts in pixels — a revealed layer is drawn only in its shape (a wipe's half, a
/// circle), a blur softens the picture, a glitch splits it; the Preview shows what the export draws.</summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TransitionLibrary2RenderingTests
{
    private static readonly FrameSize Canvas = new(64, 36);

    private static ResolvedLayer Layer(DecodedFrame frame, RevealShape? reveal = null, LayerEffect? effect = null)
    {
        var picture = Scene.Picture(Canvas, new FrameSize(frame.Width, frame.Height), VisualProperties.Default);
        return new ResolvedLayer(picture with { Reveal = reveal, Effect = effect ?? LayerEffect.None }, frame);
    }

    [Fact]
    public void A_revealed_layer_is_drawn_only_in_its_shape_in_the_export_and_the_preview()
    {
        var a = Layer(Scene.Solid(64, 36, 200, 0, 0));
        var b = Layer(Scene.Solid(64, 36, 0, 0, 200), new RevealShape(false, new RectD(32, 0, 32, 36)));   // the right half
        var plan = Scene.Plan(Canvas, a, b);

        var export = Render.Export(plan);
        var preview = Render.Preview(Canvas, 64, 36, new[] { Scene.AsPreview(a), Scene.AsPreview(b) });

        foreach (var image in new[] { export, preview })
        {
            Assert.True(image.At(10, 18).R > 180 && image.At(10, 18).B < 20);       // A on the left
            Assert.True(image.At(54, 18).B > 180 && image.At(54, 18).R < 20);       // B on the right
        }
    }

    [Fact]
    public void A_circle_reveal_leaves_the_corners_to_A()
    {
        var a = Layer(Scene.Solid(64, 36, 200, 0, 0));
        var b = Layer(Scene.Solid(64, 36, 0, 0, 200), new RevealShape(true, new RectD(16, 2, 32, 32)));

        var image = Render.Export(Scene.Plan(Canvas, a, b));

        Assert.True(image.At(32, 18).B > 180);
        Assert.True(image.At(2, 2).R > 180);
    }

    [Fact]
    public void A_blur_and_a_glitch_change_the_picture_the_same_way_in_the_export_and_the_preview()
    {
        var stripes = Scene.Frame(64, 36, (x, _) => x % 8 < 4 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255));
        foreach (var effect in new[] { new LayerEffect(0.1, BlurAxis.Both, 0, 0), new LayerEffect(0, BlurAxis.Both, 1, 3) })
        {
            var layer = Layer(stripes, effect: effect);
            var export = Render.Export(Scene.Plan(Canvas, layer));
            var preview = Render.Preview(Canvas, 64, 36, new[] { Scene.AsPreview(layer) });
            var plain = Render.Export(Scene.Plan(Canvas, Layer(stripes)));

            var changed = 0;
            for (var x = 0; x < 64; x++)
            {
                var (e, p, o) = (export.At(x, 18), preview.At(x, 18), plain.At(x, 18));
                Assert.InRange(Math.Abs(e.R - p.R) + Math.Abs(e.G - p.G) + Math.Abs(e.B - p.B), 0, 6);
                if (Math.Abs(e.R - o.R) + Math.Abs(e.B - o.B) > 20) changed++;
            }
            Assert.True(changed > 8, $"{effect}: {changed} pixels changed");
        }
    }
}
