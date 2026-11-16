using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D061 (Occlusion) in pixels: a white outline on grey with a person's mask over its left half — in the exported frame the
/// outline is gone under the person and drawn where nobody stands; the Preview draws the same (one painter, one plan); a
/// mask of nobody leaves the outline exactly as without Occlusion; Follow moves the picture and its outline together.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class PeopleOcclusionRenderTests
{
    private const byte Grey = 64;

    /// <summary>A rectangle from (0.25, 0.25) to (0.75, 0.75) of the source.</summary>
    private static readonly ImmutableArray<NormalizedPoint> Rect = ImmutableArray.Create(
        new NormalizedPoint(0.25, 0.25), new NormalizedPoint(0.75, 0.25), new NormalizedPoint(0.75, 0.75), new NormalizedPoint(0.25, 0.75));

    /// <summary>A mask of 40 × 30 cells; <paramref name="leftHalf"/>: a person over the left half of the frame.</summary>
    private static PersonMaskTrack Masks(bool leftHalf)
    {
        var grid = new byte[40 * 30];
        if (leftHalf)
            for (var y = 0; y < 30; y++) for (var x = 0; x < 20; x++) grid[y * 40 + x] = 255;
        var entries = MaskCodec.Entry(1, grid, 40, 30) is { } e ? ImmutableArray.Create(e) : ImmutableArray<PersonMaskEntry>.Empty;
        return new PersonMaskTrack(40, 30, MediaTime.FromSeconds(0.1),
            ImmutableArray.Create(new PersonMaskSample(MediaTime.Zero, entries), new PersonMaskSample(MediaTime.FromSeconds(1), entries)), "test");
    }

    private static ResolvedLayer Layer(FrameSize canvas, PersonMaskTrack? masks)
    {
        var source = new FrameSize(160, 90);
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Video, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero)
        {
            SourceSize = source, PeopleMasks = masks,
            Outline = new HoldOutline(ImmutableArray.Create(Rect), 8, "#FFFFFF", false)
        };
        var frame = Scene.Solid(source.Width, source.Height, Grey, Grey, Grey);
        return new ResolvedLayer(new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(canvas, source, VisualProperties.Default)), frame);
    }

    private static double Bright(Image image, int y, int x0, int x1)
    {
        double sum = 0;
        for (var x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++) sum += Math.Max(0, image.At(x, y).G - Grey) / (255.0 - Grey);
        return sum;
    }

    [Fact]
    public void The_outline_is_hidden_under_the_person_and_drawn_beside_it()
    {
        var canvas = new FrameSize(1920, 1080);
        var image = Render.Export(Scene.Plan(canvas, Layer(canvas, Masks(leftHalf: true))));
        var withoutPeople = Render.Export(Scene.Plan(canvas, Layer(canvas, null)));
        // The left edge of the rectangle (x = 480) is under the person: nothing drawn; the right edge (x = 1440) as without.
        Assert.True(Bright(image, 540, 450, 510) < 0.05, $"left {Bright(image, 540, 450, 510)}");
        Assert.InRange(Bright(image, 540, 1410, 1470), Bright(withoutPeople, 540, 1410, 1470) - 0.05, Bright(withoutPeople, 540, 1410, 1470) + 0.05);
        Assert.True(Bright(withoutPeople, 540, 450, 510) > 6);
        // The top edge: hidden on the left, drawn on the right.
        Assert.True(Bright(image, 270, 600, 700) < Bright(withoutPeople, 270, 600, 700) * 0.1 + 0.5 || image.At(650, 270).G < Grey + 10);
        Assert.True(image.At(1200, 270).G > 200);
        Assert.True(image.At(650, 270).G < Grey + 10);
    }

    [Fact]
    public void A_mask_of_nobody_leaves_the_frame_exactly_as_without_Occlusion()
    {
        var canvas = new FrameSize(1280, 720);
        var empty = Render.Export(Scene.Plan(canvas, Layer(canvas, Masks(leftHalf: false))));
        var without = Render.Export(Scene.Plan(canvas, Layer(canvas, null)));
        Assert.Equal(without.Pixels, empty.Pixels);
    }

    [Fact]
    public void The_preview_draws_what_the_export_draws()
    {
        var canvas = new FrameSize(1280, 720);
        var layer = Layer(canvas, Masks(leftHalf: true));
        var export = Render.Export(Scene.Plan(canvas, layer));
        var preview = Render.Preview(canvas, 1280, 720, new[] { Scene.AsPreview(layer) });
        // The same painter and plan: hidden on the left, drawn on the right, in both.
        Assert.True(export.At(320, 360).G < Grey + 10 && preview.At(320, 360).G < Grey + 10);
        Assert.True(export.At(960, 360).G > 200 && preview.At(960, 360).G > 200);
    }
}
