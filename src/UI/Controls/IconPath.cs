using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Media;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// One of <see cref="Icons"/> inside a button (D039 §7): 14 × 14, stroked in the colour of the button's content, so it
/// follows hover / pressed / disabled like the label; <see cref="Filled"/> also fills it (the transport's solid shapes).
/// </summary>
public class IconPath : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<IconPath, bool>(nameof(Filled));
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<IconPath, string?>(nameof(Glyph));

    private static readonly Dictionary<string, Geometry> Parsed = new();

    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Shapes.Path);

    public IconPath()
    {
        Width = 14;
        Height = 14;
        Stretch = Stretch.Uniform;
        StrokeThickness = 1.5;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        var foreground = new Binding(nameof(ContentPresenter.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ContentPresenter) },
            FallbackValue = Brushes.White
        };
        Bind(StrokeProperty, foreground);
    }

    /// <summary>One of <see cref="Icons"/>: path data, turned into the geometry once the control is in a visual tree.</summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilledProperty || change.Property == StrokeProperty)
            Fill = Filled ? Stroke : null;
        if (change.Property == GlyphProperty && VisualRoot is not null) ShowGlyph();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowGlyph();
    }

    private void ShowGlyph()
    {
        if (Glyph is not { } glyph) return;
        if (!Parsed.TryGetValue(glyph, out var geometry)) Parsed[glyph] = geometry = Geometry.Parse(glyph);
        Data = geometry;
    }
}
