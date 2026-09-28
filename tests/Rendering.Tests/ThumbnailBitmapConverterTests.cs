using System.Globalization;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Rendering;
using Avalonia.Media.Imaging;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D024 Step 9.4d: the Media Browser draws a thumbnail as an Avalonia bitmap of its size with its BGRA pixels, one bitmap
/// per thumbnail — rebuilt rows reuse it. Avalonia is initialised like the app.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ThumbnailBitmapConverterTests
{
    private static object? Convert(Thumbnail? thumbnail) =>
        ThumbnailBitmapConverter.Instance.Convert(thumbnail, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void A_thumbnail_becomes_a_bitmap_of_its_size_and_pixels()
    {
        // 3 × 2: red, green, blue / white, black, half-transparent grey (BGRA).
        byte[] pixels =
        [
            0, 0, 255, 255,   0, 255, 0, 255,   255, 0, 0, 255,
            255, 255, 255, 255,   0, 0, 0, 255,   128, 128, 128, 128
        ];
        var bitmap = Assert.IsType<WriteableBitmap>(Convert(new Thumbnail(3, 2, pixels)));

        Assert.Equal((3, 2), (bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        using var locked = bitmap.Lock();
        var read = new byte[pixels.Length];
        for (var y = 0; y < 2; y++)
            Marshal.Copy(locked.Address + y * locked.RowBytes, read, y * 12, 12);
        Assert.Equal(pixels, read);
    }

    [Fact]
    public void The_same_thumbnail_gives_the_same_bitmap_and_no_thumbnail_none()
    {
        var thumbnail = new Thumbnail(1, 1, new byte[] { 1, 2, 3, 255 });

        Assert.Same(Convert(thumbnail), Convert(thumbnail));
        Assert.NotSame(Convert(thumbnail), Convert(new Thumbnail(1, 1, new byte[] { 1, 2, 3, 255 })));
        Assert.Null(Convert(null));
    }
}
