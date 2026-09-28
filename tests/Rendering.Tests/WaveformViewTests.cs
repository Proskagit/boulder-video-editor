using System.Runtime.InteropServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Rendering;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D024 Step 9.5d: <see cref="WaveformView"/> as drawn — an audio clip's waveform over the whole height around the
/// centre, a video clip's in the lower half only (PO-W1), full height at full scale and 200 % (PO-W2), a muted clip
/// dimmed, silence not drawn. Avalonia is initialised like the app.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class WaveformViewTests
{
    private const int Width = 100, Height = 40;

    /// <summary>One second at 100 px/s; the first half full scale, the second half silent (480 samples per peak).</summary>
    private static ClipWaveform Clip(bool lowerHalf = false, bool muted = false, double volume = 2) =>
        new(new Waveform(480, 48_000, Enumerable.Range(0, 100).Select(i => i < 50 ? Waveform.FullScale : (byte)0).ToArray()),
            MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero, ClipSpeed.Normal, volume, muted, lowerHalf, 100);

    private static Image Draw(ClipWaveform clip) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var view = new WaveformView { Waveform = clip };
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

    private static byte Alpha(Image image, int x, int y) => image.At(x, y).A;

    [Fact]
    public void An_audio_clip_is_drawn_over_the_whole_height_where_there_is_sound()
    {
        var image = Draw(Clip());

        Assert.All(new[] { 0, 10, 20, 30, 39 }, y => Assert.True(Alpha(image, 25, y) > 0, $"row {y} empty"));
        Assert.All(new[] { 0, 20, 39 }, y => Assert.Equal(0, Alpha(image, 75, y)));   // the silent half: nothing
    }

    [Fact]
    public void A_video_clip_takes_the_lower_half_only()
    {
        var image = Draw(Clip(lowerHalf: true));

        Assert.All(new[] { 0, 10, 19 }, y => Assert.Equal(0, Alpha(image, 25, y)));
        Assert.All(new[] { 21, 30, 39 }, y => Assert.True(Alpha(image, 25, y) > 0, $"row {y} empty"));
    }

    [Fact]
    public void At_100_percent_a_full_scale_peak_takes_half_the_height()
    {
        var image = Draw(Clip(volume: 1));

        Assert.True(Alpha(image, 25, 20) > 0);
        Assert.Equal(0, Alpha(image, 25, 2));                                      // above the half-height band
        Assert.Equal(0, Alpha(image, 25, 37));
        Assert.True(Alpha(image, 25, 12) > 0);
    }

    [Fact]
    public void A_muted_clip_is_the_same_shape_dimmed()
    {
        var audible = Draw(Clip());
        var muted = Draw(Clip(muted: true));

        Assert.True(Alpha(muted, 25, 20) > 0);
        Assert.True(Alpha(muted, 25, 20) < Alpha(audible, 25, 20));
        Assert.Equal(0, Alpha(muted, 75, 20));
    }
}
