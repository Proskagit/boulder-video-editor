using System.ComponentModel;
using System.Globalization;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// D061: the Preview's people layer over the composition — the people's boxes while the user picks the climber for Follow
/// (a click on a box picks it, Esc cancels), each person's own mask in its own colour while the masks are shown (a check of
/// the segmentation: separate people, never merged). Drawn through the same viewport and layer geometry as the composition
/// (D018, Follow included), so it sits exactly on the picture. Never part of the export. UI thread only.
/// </summary>
public sealed class PeopleOverlay : Control, ICustomHitTest
{
    public static readonly StyledProperty<PreviewViewModel?> PreviewProperty =
        AvaloniaProperty.Register<PeopleOverlay, PreviewViewModel?>(nameof(Preview));

    private static readonly Color[] Palette =
    {
        Color.Parse("#FF5252"), Color.Parse("#40C4FF"), Color.Parse("#FFD740"), Color.Parse("#69F0AE"),
        Color.Parse("#E040FB"), Color.Parse("#FFAB40"), Color.Parse("#B2FF59"), Color.Parse("#536DFE")
    };

    private static readonly IPen BoxKeyline = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 4);
    private static readonly IPen BoxPen = new Pen(Brushes.White, 2, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen FollowedPen = new Pen(new SolidColorBrush(Color.Parse("#00E676")), 3);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20));

    private readonly Dictionary<int, (OcclusionMask Mask, WriteableBitmap Bitmap)> _maskBitmaps = new();
    private PreviewViewModel? _preview;

    static PeopleOverlay()
    {
        PreviewProperty.Changed.AddClassHandler<PeopleOverlay>((o, e) => o.Attach(e.NewValue as PreviewViewModel));
        FocusableProperty.OverrideDefaultValue<PeopleOverlay>(true);
    }

    public PreviewViewModel? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    private void Attach(PreviewViewModel? preview)
    {
        if (_preview is not null) _preview.PropertyChanged -= OnPreviewChanged;
        _preview = preview;
        if (_preview is not null) _preview.PropertyChanged += OnPreviewChanged;
        InvalidateVisual();
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.PeopleOverlay))
        {
            InvalidateVisual();
            if (_preview?.PeopleOverlay is { IsPicking: true }) Focus();
        }
    }

    /// <summary>Clicks reach this layer only while the climber is picked.</summary>
    public bool HitTest(Point point) => _preview?.PeopleOverlay is { IsPicking: true };

    /// <summary>Normalized source coordinates → view pixels through the viewport and the layer's geometry.</summary>
    public static Point ToView(LayerGeometry geometry, FrameSize canvas, Size view, NormalizedPoint n)
    {
        var toView = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).Compose(geometry.Transform);
        var p = toView.Apply(new PointD(n.X * geometry.Source.Width - geometry.SourceRect.X, n.Y * geometry.Source.Height - geometry.SourceRect.Y));
        return new Point(p.X, p.Y);
    }

    /// <summary>View pixels → normalized source coordinates (outside the frame allowed).</summary>
    public static NormalizedPoint ToSource(LayerGeometry geometry, FrameSize canvas, Size view, Point p)
    {
        var toView = CompositionDrawPlan.Viewport(canvas, view.Width, view.Height).Compose(geometry.Transform);
        var local = toView.Invert().Apply(new PointD(p.X, p.Y));
        return new NormalizedPoint((local.X + geometry.SourceRect.X) / geometry.Source.Width, (local.Y + geometry.SourceRect.Y) / geometry.Source.Height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_preview?.PeopleOverlay is not { IsPicking: true } state) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _preview.PickAt(ToSource(state.Geometry, state.Canvas, Bounds.Size, point.Position));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _preview?.IsPickingPerson == true)
        {
            _preview.CancelPersonPick();
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_preview?.PeopleOverlay is not { } state || !state.Canvas.IsValid) return;
        var view = Bounds.Size;
        var viewport = CompositionDrawPlan.Viewport(state.Canvas, view.Width, view.Height);
        var canvasRect = CompositionDrawPlan.Bounds(state.Canvas, viewport);
        using var clip = context.PushClip(new Rect(canvasRect.X, canvasRect.Y, canvasRect.Width, canvasRect.Height));

        // The masks, each person in its colour, through the layer's transform over the whole source frame.
        var live = new HashSet<int>();
        foreach (var (id, mask) in state.Masks)
        {
            live.Add(id);
            var bitmap = MaskBitmap(id, mask);
            var toView = viewport.Compose(state.Geometry.Transform);
            using (context.PushTransform(RenderConversions.ToMatrix(toView)))
                context.DrawImage(bitmap, new Rect(0, 0, mask.Width, mask.Height),
                    new Rect(-state.Geometry.SourceRect.X, -state.Geometry.SourceRect.Y, state.Geometry.Source.Width, state.Geometry.Source.Height));
        }
        foreach (var gone in _maskBitmaps.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _maskBitmaps[gone].Bitmap.Dispose();
            _maskBitmaps.Remove(gone);
        }

        if (!state.IsPicking) return;
        foreach (var person in state.People.OrderBy(p => p.IsFollowed))
        {
            var a = ToView(state.Geometry, state.Canvas, view, new NormalizedPoint(person.Box.X0, person.Box.Y0));
            var b = ToView(state.Geometry, state.Canvas, view, new NormalizedPoint(person.Box.X1, person.Box.Y1));
            var rect = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            context.DrawRectangle(null, BoxKeyline, rect);
            context.DrawRectangle(null, person.IsFollowed ? FollowedPen : BoxPen, rect);
            var label = new FormattedText(person.IsFollowed ? $"№{person.Id} ✓" : $"№{person.Id}", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            context.DrawRectangle(LabelBackground, null, new Rect(rect.X, rect.Y, label.Width + 8, label.Height + 2));
            context.DrawText(label, new Point(rect.X + 4, rect.Y + 1));
        }
    }

    private WriteableBitmap MaskBitmap(int id, OcclusionMask mask)
    {
        if (_maskBitmaps.TryGetValue(id, out var cached) && ReferenceEquals(cached.Mask, mask)) return cached.Bitmap;
        var bitmap = cached.Bitmap is { } old && old.PixelSize.Width == mask.Width && old.PixelSize.Height == mask.Height
            ? old
            : new WriteableBitmap(new PixelSize(mask.Width, mask.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        if (cached.Bitmap is { } previous && !ReferenceEquals(previous, bitmap)) previous.Dispose();
        var color = Palette[((id % Palette.Length) + Palette.Length) % Palette.Length];
        using (var target = bitmap.Lock())
        {
            var row = new byte[mask.Width * 4];
            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width; x++)
                {
                    var a = mask.Coverage[y * mask.Width + x] * 140 / 255;   // translucent
                    row[x * 4] = (byte)(color.B * a / 255);
                    row[x * 4 + 1] = (byte)(color.G * a / 255);
                    row[x * 4 + 2] = (byte)(color.R * a / 255);
                    row[x * 4 + 3] = (byte)a;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
            }
        }
        _maskBitmaps[id] = (mask, bitmap);
        return bitmap;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var (_, bitmap) in _maskBitmaps.Values) bitmap.Dispose();
        _maskBitmaps.Clear();
    }
}
