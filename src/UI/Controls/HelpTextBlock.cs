using AiVideoEditor.UI.Help;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AiVideoEditor.UI.Controls;

/// <summary>D068 §3: a selectable text block that shows a help line's <see cref="HelpSpan"/>s as runs (bold where marked).</summary>
public sealed class HelpTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<HelpSpan>?> SpansProperty =
        AvaloniaProperty.Register<HelpTextBlock, IReadOnlyList<HelpSpan>?>(nameof(Spans));

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public IReadOnlyList<HelpSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SpansProperty) return;

        var inlines = new InlineCollection();
        foreach (var span in Spans ?? Array.Empty<HelpSpan>())
            inlines.Add(new Run(span.Text) { FontWeight = span.IsBold ? FontWeight.SemiBold : FontWeight.Normal });
        Inlines = inlines;
    }
}
