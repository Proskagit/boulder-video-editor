using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// D058 §5: a field's label that changes the field by dragging it sideways — <see cref="StepPerPixel"/> per pixel to the
/// right (less to the left), ×10 with Shift, ×0.1 with Ctrl; the value is rounded to two decimals and kept within the
/// field's limits. It only sets the field's <see cref="NumericUpDown.Value"/>, so the field's binding, its rules and its
/// undo merging apply exactly as when typing (consecutive changes of one property of one clip are one undo step, D017); the
/// field itself stays for exact input.
/// </summary>
public class ScrubLabel : TextBlock
{
    public static readonly StyledProperty<NumericUpDown?> TargetProperty =
        AvaloniaProperty.Register<ScrubLabel, NumericUpDown?>(nameof(Target));

    public static readonly StyledProperty<decimal> StepPerPixelProperty =
        AvaloniaProperty.Register<ScrubLabel, decimal>(nameof(StepPerPixel), 1m);

    /// <summary>The field the drag changes.</summary>
    public NumericUpDown? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>The change for one pixel of the drag.</summary>
    public decimal StepPerPixel
    {
        get => GetValue(StepPerPixelProperty);
        set => SetValue(StepPerPixelProperty, value);
    }

    /// <summary>Styled as a TextBlock (the Inspector's <c>TextBlock.label</c>).</summary>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    private Point? _start;
    private decimal _startValue;

    public ScrubLabel()
    {
        Background = Avalonia.Media.Brushes.Transparent;   // the whole label takes the pointer
    }

    /// <summary>The ↔ cursor, made when the pointer first comes (a cursor needs the platform, absent in headless tests).</summary>
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Cursor ??= new Cursor(StandardCursorType.SizeWestEast);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Target is not { IsEffectivelyEnabled: true } target || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _start = e.GetPosition(this);
        _startValue = target.Value ?? 0;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_start is not { } start || Target is not { } target) return;
        var value = ValueAt(_startValue, e.GetPosition(this).X - start.X, StepPerPixel, e.KeyModifiers, target.Minimum, target.Maximum);
        if (target.Value != value) target.Value = value;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_start is null) return;
        _start = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _start = null;
    }

    /// <summary>The value for a drag of <paramref name="pixels"/> from <paramref name="start"/>: whole pixels × the step
    /// (Shift ×10, Ctrl ×0.1), rounded to two decimals, within [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
    public static decimal ValueAt(decimal start, double pixels, decimal stepPerPixel, KeyModifiers modifiers, decimal minimum, decimal maximum)
    {
        var step = stepPerPixel * (modifiers.HasFlag(KeyModifiers.Shift) ? 10m : 1m) * (modifiers.HasFlag(KeyModifiers.Control) ? 0.1m : 1m);
        var value = Math.Round(start + (decimal)Math.Round(pixels) * step, 2, MidpointRounding.AwayFromZero);
        return Math.Clamp(value, minimum, maximum);
    }
}
