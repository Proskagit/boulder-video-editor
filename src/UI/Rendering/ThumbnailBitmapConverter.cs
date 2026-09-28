using System.Globalization;
using System.Runtime.CompilerServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Shows a <see cref="Thumbnail"/> (BGRA, straight alpha) as an Avalonia bitmap, copied like a decoded Preview frame
/// (<see cref="FrameBitmap"/>). One bitmap per thumbnail instance: the Media Browser rebuilds its rows on every media
/// change, and they reuse the bitmap instead of copying the pixels again; it goes away with its thumbnail.
/// </summary>
public sealed class ThumbnailBitmapConverter : IValueConverter
{
    public static readonly ThumbnailBitmapConverter Instance = new();

    private static readonly SourceTimestamp NoTime = new(0, new TimeBase(1, 1));
    private readonly ConditionalWeakTable<Thumbnail, WriteableBitmap> _bitmaps = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Thumbnail thumbnail ? _bitmaps.GetValue(thumbnail, ToBitmap) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static WriteableBitmap ToBitmap(Thumbnail thumbnail)
    {
        var frame = new DecodedFrame(thumbnail.Width, thumbnail.Height, thumbnail.Stride, thumbnail.Pixels, NoTime);
        var bitmap = FrameBitmap.Ensure(null, frame);
        FrameBitmap.Copy(frame, bitmap);
        return bitmap;
    }
}
