using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Rendering;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D064 §2: <see cref="VolumeEnvelopeView"/> as drawn — the line at the clip's volume (100 % in the middle of an audio clip, of
/// the lower half of a video clip), following the points, the points and the fade handles on the selected clip only, the sound
/// fade's area shaded; and <see cref="ClipVolumeShape.HitTest"/> finds what the drawing shows.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class VolumeEnvelopeViewTests
{
    private const int Width = 100, Height = 36;

    private static ClipVolumeShape Shape(double volume = 1, VolumeEnvelope? points = null, bool lowerHalf = false, bool active = false,
        double fadeIn = 0) =>
        new(volume, points, MediaTime.Zero, 1, 100, Width, lowerHalf, fadeIn, 0, false, active, null);

    private static Image Draw(ClipVolumeShape shape) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var view = new VolumeEnvelopeView { Shape = shape };
        view.Measure(new Size(Width, Height));
        view.Arrange(new Rect(0, 0, Width, Height));
        using var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(view);
        var pixels = new byte[Width * Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, Width * 4); }
        finally { handle.Free(); }
        return new Image(Width, Height, pixels);
    }).GetTask().GetAwaiter().GetResult();

    /// <summary>The row of the most opaque pixel in column <paramref name="x"/>.</summary>
    private static int LineRow(Image image, int x) => Enumerable.Range(0, Height).OrderByDescending(y => image.At(x, y).A).First();

    [Fact]
    public void The_line_of_an_audio_clip_at_100_percent_lies_in_the_middle_and_follows_the_volume()
    {
        Assert.InRange(LineRow(Draw(Shape()), 40), 17, 19);
        Assert.InRange(LineRow(Draw(Shape(volume: 2)), 40), 0, 1);
        Assert.InRange(LineRow(Draw(Shape(volume: 0.5)), 40), 26, 28);
    }

    [Fact]
    public void A_video_clips_line_is_in_its_lower_half()
    {
        Assert.InRange(LineRow(Draw(Shape(lowerHalf: true)), 40), 26, 28);
    }

    [Fact]
    public void The_line_follows_the_points()
    {
        var points = VolumeEnvelope.Of(new[]
        {
            new VolumePoint(Guid.NewGuid(), MediaTime.FromSeconds(0.2), 1), new VolumePoint(Guid.NewGuid(), MediaTime.FromSeconds(0.8), 2)
        });
        var image = Draw(Shape(points: points));
        Assert.InRange(LineRow(image, 10), 17, 19);
        Assert.InRange(LineRow(image, 90), 0, 1);
        Assert.True(LineRow(image, 50) is > 1 and < 17);
    }

    [Fact]
    public void Points_and_handles_show_on_the_selected_clip_only()
    {
        var points = new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(Guid.NewGuid(), MediaTime.FromSeconds(0.5), 1)));
        var idle = Draw(Shape(points: points));
        var active = Draw(Shape(points: points, active: true));
        Assert.True(active.At(50, 15).A > idle.At(50, 15).A);      // the point's dot above the 1 px line
        Assert.True(active.At(1, 3).A > 0);                          // the fade-in handle at the top left
        Assert.Equal(0, idle.At(1, 3).A);
    }

    [Fact]
    public void A_sound_fade_shades_its_area()
    {
        var image = Draw(Shape(fadeIn: 40));
        Assert.True(image.At(10, 5).A > 0);
        Assert.Equal(0, image.At(70, 5).A);
    }

    [Fact]
    public void The_hit_test_finds_the_handles_the_points_and_the_line()
    {
        var id = Guid.NewGuid();
        var shape = Shape(points: new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(id, MediaTime.FromSeconds(0.5), 1))), active: true, fadeIn: 20);
        Assert.Equal(VolumeHitKind.FadeIn, shape.HitTest(20, 3, Height).Kind);
        Assert.Equal(VolumeHitKind.FadeOut, shape.HitTest(99, 3, Height).Kind);
        Assert.Equal(new VolumeHit(VolumeHitKind.Point, id), shape.HitTest(51, 18, Height));
        Assert.Equal(VolumeHitKind.Line, shape.HitTest(75, 19, Height).Kind);
        Assert.Equal(VolumeHitKind.None, shape.HitTest(75, 30, Height).Kind);
        Assert.Equal(VolumeHitKind.None, (shape with { IsActive = false }).HitTest(75, 18, Height).Kind);
        Assert.Equal(0.5, shape.GainOfY(27, Height), 6);
    }
}
