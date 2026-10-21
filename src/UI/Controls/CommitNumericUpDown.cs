using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// A <see cref="NumericUpDown"/> whose typed text becomes its value only when the input is committed — Enter or the
/// field losing the focus (the base control's own commit) — not on every keystroke. Avalonia's control parses the text
/// after each key, so typing "99" into a field limited to 51 first applied 9 as an edit and then refused 99, leaving the
/// model at 9; with a fraction ("2,5") the "2" stayed. Every value it sends is now one the user finished typing; the
/// spinner arrows and the arrow keys still change the value at once. Styled as a plain NumericUpDown.
/// <para>
/// With a <see cref="StepCommand"/> a spin (the spinner arrows, the arrow keys, the wheel) is not a value change: the
/// command gets the direction (+1 / −1) and decides the next value; a typed value is always just a value. The field of a
/// transition's seconds steps on the 0.1 s grid this way (D044 §5), and typing 0.85 is never mistaken for a step.
/// </para>
/// </summary>
public class CommitNumericUpDown : NumericUpDown
{
    public static readonly StyledProperty<ICommand?> StepCommandProperty =
        AvaloniaProperty.Register<CommitNumericUpDown, ICommand?>(nameof(StepCommand));

    /// <summary>When set, a spin executes it with +1 or −1 instead of adding <see cref="NumericUpDown.Increment"/>.</summary>
    public ICommand? StepCommand
    {
        get => GetValue(StepCommandProperty);
        set => SetValue(StepCommandProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    /// <summary>True while the user is typing in the field (it has the keyboard focus): the text is not parsed yet.</summary>
    internal bool IsTyping => TypingOverride ?? IsKeyboardFocusWithin;

    /// <summary>Tests only: replaces the focus check (a headless control never has the keyboard focus).</summary>
    internal bool? TypingOverride { get; set; }

    protected override void OnSpin(SpinEventArgs e)
    {
        if (!Step(e.Direction)) base.OnSpin(e);
    }

    /// <summary>A spin in <paramref name="direction"/> through <see cref="StepCommand"/>; false when there is none.</summary>
    internal bool Step(SpinDirection direction)
    {
        if (StepCommand is not { } command) return false;
        var step = direction == SpinDirection.Increase ? 1 : -1;
        if (command.CanExecute(step)) command.Execute(step);
        return true;
    }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        if (IsTyping) return;    // committed by Enter / focus loss (NumericUpDown.CommitInput)
        base.OnTextChanged(oldValue, newValue);
    }
}
