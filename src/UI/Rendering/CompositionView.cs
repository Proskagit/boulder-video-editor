using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Draws a composition (Phase 7 Step 7) with Avalonia's <see cref="DrawingContext"/>: the
/// <see cref="CompositionDrawPlan"/> for <see cref="Layers"/> on <see cref="Canvas"/>, bottom to top,
/// clipped to the canvas, letterboxed in the control. All geometry comes from the plan (D018); this
/// control only owns the bitmaps. Every layer has two <see cref="WriteableBitmap"/>s, alternated so
/// the bitmap being written is never the one on screen; a frame is copied only when the layer's
/// decoded frame changes. UI thread only.
/// </summary>
public sealed class CompositionView : Control
{
    public static readonly StyledProperty<ImmutableArray<LayerPicture>> LayersProperty =
        AvaloniaProperty.Register<CompositionView, ImmutableArray<LayerPicture>>(nameof(Layers), ImmutableArray<LayerPicture>.Empty);

    public static readonly StyledProperty<FrameSize> CanvasProperty =
        AvaloniaProperty.Register<CompositionView, FrameSize>(nameof(Canvas), new FrameSize(1920, 1080));

    private static readonly IBrush CanvasBackground = Brushes.Black;
    private static readonly IBrush PlaceholderFill = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly IBrush PlaceholderText = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Color PlaceholderBorder = Color.FromRgb(0x5A, 0x5A, 0x5A);

    private readonly Dictionary<Guid, LayerBitmaps> _bitmaps = new();

    static CompositionView()
    {
        AffectsRender<CompositionView>(LayersProperty, CanvasProperty);
    }

    public ImmutableArray<LayerPicture> Layers
    {
        get => GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    public FrameSize Canvas
    {
        get => GetValue(CanvasProperty);
        set => SetValue(CanvasProperty, value);
    }

    /// <summary>Bitmaps currently held (diagnostics).</summary>
    internal int BitmapLayerCount => _bitmaps.Count;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayersProperty)
            UpdateBitmaps();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var bitmaps in _bitmaps.Values) bitmaps.Dispose();
        _bitmaps.Clear();
    }

    public override void Render(DrawingContext context)
    {
        var layers = Layers.IsDefault ? ImmutableArray<LayerPicture>.Empty : Layers;
        var plan = CompositionDrawPlan.Build(Canvas, Bounds.Width, Bounds.Height, layers);
        if (plan.CanvasBounds.Width <= 0) return;

        var canvasRect = RenderConversions.ToRect(plan.CanvasBounds);
        context.FillRectangle(CanvasBackground, canvasRect);
        using (context.PushClip(canvasRect))
        {
            foreach (var op in plan.Operations)
            {
                using (context.PushTransform(RenderConversions.ToMatrix(op.Transform)))
                using (context.PushOpacity(op.Opacity))
                {
                    switch (op.Kind)
                    {
                        case DrawKind.Frame:
                            if (_bitmaps.TryGetValue(op.ClipId, out var bitmaps) && bitmaps.Current is { } bitmap)
                                context.DrawImage(bitmap, RenderConversions.ToRect(op.FrameSourceRect!.Value), RenderConversions.ToRect(op.LocalRect));
                            break;
                        case DrawKind.Text:
                            DrawText(context, op.Text!.Value);
                            break;
                        case DrawKind.Placeholder:
                            DrawPlaceholder(context, op);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Lays the text out at its font size (canvas pixels, the transform scales it), centres
    /// the text box on the local origin; lines are aligned inside the box.</summary>
    private static void DrawText(DrawingContext context, TextProperties text)
    {
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily), text.FontSize, new SolidColorBrush(Color.Parse(text.ColorHex)));
        var width = formatted.WidthIncludingTrailingWhitespace;
        formatted.MaxTextWidth = Math.Max(width, 1);
        formatted.TextAlignment = text.Alignment switch
        {
            Core.Entities.TextAlignment.Left => Avalonia.Media.TextAlignment.Left,
            Core.Entities.TextAlignment.Right => Avalonia.Media.TextAlignment.Right,
            _ => Avalonia.Media.TextAlignment.Center
        };
        context.DrawText(formatted, new Point(-formatted.MaxTextWidth / 2, -formatted.Height / 2));
    }

    private static void DrawPlaceholder(DrawingContext context, DrawOperation op)
    {
        var rect = RenderConversions.ToRect(op.LocalRect);
        var shortSide = Math.Min(rect.Width, rect.Height);
        context.DrawRectangle(PlaceholderFill, new Pen(new SolidColorBrush(PlaceholderBorder), Math.Max(1, shortSide * 0.004)), rect);

        var label = new FormattedText(op.Label ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface.Default, Math.Max(8, shortSide * 0.06), PlaceholderText);
        context.DrawText(label, new Point(rect.Center.X - label.Width / 2, rect.Center.Y - label.Height / 2));
    }

    private void UpdateBitmaps()
    {
        var layers = Layers.IsDefault ? ImmutableArray<LayerPicture>.Empty : Layers;
        var live = new HashSet<Guid>();
        foreach (var picture in layers)
        {
            if (picture.State != LayerPictureState.Frame || picture.Frame is not { } frame) continue;
            live.Add(picture.Layer.ClipId);
            if (!_bitmaps.TryGetValue(picture.Layer.ClipId, out var bitmaps))
                _bitmaps[picture.Layer.ClipId] = bitmaps = new LayerBitmaps();
            bitmaps.Show(frame);
        }

        // Keep bitmaps of layers that are only pending for now (they reappear with a new frame);
        // drop the ones whose layer is gone from the composition.
        foreach (var picture in layers) live.Add(picture.Layer.ClipId);
        foreach (var gone in _bitmaps.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _bitmaps[gone].Dispose();
            _bitmaps.Remove(gone);
        }
    }

    /// <summary>Two alternating bitmaps for one layer.</summary>
    private sealed class LayerBitmaps : IDisposable
    {
        private readonly WriteableBitmap?[] _pair = new WriteableBitmap?[2];
        private int _next;
        private DecodedFrame? _shown;

        public WriteableBitmap? Current { get; private set; }

        public void Show(DecodedFrame frame)
        {
            if (ReferenceEquals(frame, _shown)) return;

            var bitmap = _pair[_next];
            if (bitmap is null || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _pair[_next] = bitmap;
            }

            using (var target = bitmap.Lock())
            {
                // DecodedFrame buffers are array-backed; copy rows straight from the array.
                var source = MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
                    ? segment
                    : new ArraySegment<byte>(frame.Pixels.ToArray());
                var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(source.Array!, source.Offset + y * frame.Stride, target.Address + y * target.RowBytes, rowBytes);
            }

            Current = bitmap;
            _shown = frame;
            _next ^= 1;
        }

        public void Dispose()
        {
            foreach (var bitmap in _pair) bitmap?.Dispose();
            Current = null;
        }
    }
}
