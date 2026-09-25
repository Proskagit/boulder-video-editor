using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Playback;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Draws a composition (Phase 7 Step 7) with Avalonia's <see cref="DrawingContext"/>: the
/// <see cref="PreviewDrawPlan"/> for <see cref="Layers"/> on <see cref="Canvas"/> — the shared Core plan plus
/// the Preview's placeholders — painted by <see cref="CompositionPainter"/> (the same routine the export
/// uses, D023), bottom to top, clipped to the canvas, letterboxed in the control. All geometry comes from
/// the plan (D018); this control only owns the bitmaps. Every layer has two <see cref="WriteableBitmap"/>s, alternated so
/// the bitmap being written is never the one on screen; a frame is copied only when the layer's
/// decoded frame changes. UI thread only.
/// </summary>
public sealed class CompositionView : Control
{
    public static readonly StyledProperty<ImmutableArray<LayerPicture>> LayersProperty =
        AvaloniaProperty.Register<CompositionView, ImmutableArray<LayerPicture>>(nameof(Layers), ImmutableArray<LayerPicture>.Empty);

    public static readonly StyledProperty<FrameSize> CanvasProperty =
        AvaloniaProperty.Register<CompositionView, FrameSize>(nameof(Canvas), new FrameSize(1920, 1080));

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
        var plan = PreviewDrawPlan.Build(Canvas, Bounds.Width, Bounds.Height, layers);
        CompositionPainter.Paint(context, plan,
            frame => _bitmaps.TryGetValue(frame.ClipId, out var bitmaps) ? bitmaps.Current : null);
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

            var bitmap = _pair[_next] = FrameBitmap.Ensure(_pair[_next], frame);
            FrameBitmap.Copy(frame, bitmap);

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
