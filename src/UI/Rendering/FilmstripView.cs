using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Rendering;

/// <summary>D068 item 10: what a video clip's filmstrip shows — the video's pictures and the clip's timing (its first source
/// time and speed) at the timeline's zoom.</summary>
public sealed record ClipFilmstrip(Filmstrip Strip, MediaTime SourceIn, ClipSpeed Speed, double PixelsPerSecond)
{
    /// <summary>The source time shown at <paramref name="x"/> pixels from the clip's left edge.</summary>
    public MediaTime SourceTimeAt(double x)
    {
        var timelineTicks = (long)(Math.Max(x, 0) / PixelsPerSecond * TimeSpan.TicksPerSecond);
        return new MediaTime(SourceIn.Ticks + timelineTicks * Speed.Numerator / Speed.Denominator);
    }
}

/// <summary>
/// D068 item 10: draws a timeline video clip as a strip of pictures (as every editor does), each tile the video's picture
/// nearest to the source time at the tile's middle; only the tiles inside the timeline's visible area are drawn, and the
/// view redraws when the timeline scrolls. Under the clip's name and its waveform, a little dimmed so they stay readable.
/// </summary>
public sealed class FilmstripView : Control
{
    public static readonly StyledProperty<ClipFilmstrip?> FilmstripProperty =
        AvaloniaProperty.Register<FilmstripView, ClipFilmstrip?>(nameof(Filmstrip));

    private ScrollViewer? _scroll;

    static FilmstripView()
    {
        AffectsRender<FilmstripView>(FilmstripProperty);
    }

    public FilmstripView() => IsHitTestVisible = false;

    public ClipFilmstrip? Filmstrip
    {
        get => GetValue(FilmstripProperty);
        set => SetValue(FilmstripProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    /// <summary>The tiles' width for a clip <paramref name="height"/> pixels high: the pictures' own aspect.</summary>
    internal static double TileWidth(Filmstrip strip, double height)
    {
        var first = strip.Frames[0].Picture;
        return Math.Max(8, height * first.Width / (double)first.Height);
    }

    public override void Render(DrawingContext context)
    {
        if (Filmstrip is not { } clip || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var tile = TileWidth(clip.Strip, Bounds.Height);
        var (from, to) = _scroll is not null && this.TranslatePoint(default, _scroll) is { } origin
            ? WaveformView.Visible(origin.X, _scroll.Viewport.Width, Bounds.Width)
            : (0, (int)Math.Ceiling(Bounds.Width));
        if (from >= to) return;

        using (context.PushOpacity(0.55))
        {
            for (var i = (int)Math.Floor(from / tile); i * tile < to; i++)
            {
                var x = i * tile;
                var frame = clip.Strip.Nearest(clip.SourceTimeAt(x + tile / 2));
                if (ThumbnailBitmapConverter.Instance.Convert(frame.Picture, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture) is not Bitmap bitmap)
                    continue;
                var width = Math.Min(tile, Bounds.Width - x);           // the last tile is cut, not squeezed
                context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width * width / tile, bitmap.PixelSize.Height),
                    new Rect(x, 0, width, Bounds.Height));
            }
        }
    }
}
