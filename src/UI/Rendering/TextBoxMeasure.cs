using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.UI.Rendering;

/// <summary>Measures a text's box — what the placement grid (<c>TextPlacement</c>, D043 §3) places and a new text is placed
/// by. Injected so the view models stay usable without a running Avalonia platform (tests).</summary>
public interface ITextBoxMeasurer
{
    /// <summary>The box's width and height, local units = canvas pixels at scale 1.</summary>
    (double Width, double Height) Measure(TextProperties text);
}

/// <summary>The painter's own layout rule (<see cref="CompositionPainter.Layout"/>): the box the Preview and the export draw.</summary>
public sealed class TextBoxMeasure : ITextBoxMeasurer
{
    public (double Width, double Height) Measure(TextProperties text)
    {
        var layout = CompositionPainter.Layout(text);
        return (layout.MaxTextWidth, layout.Height);
    }
}
