using Avalonia.Controls;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// A <see cref="NumericUpDown"/> whose typed text becomes its value only when the input is committed — Enter or the
/// field losing the focus (the base control's own commit) — not on every keystroke. Avalonia's control parses the text
/// after each key, so typing "99" into a field limited to 51 first applied 9 as an edit and then refused 99, leaving the
/// model at 9; with a fraction ("2,5") the "2" stayed. Every value it sends is now one the user finished typing; the
/// spinner arrows and the arrow keys still change the value at once. Styled as a plain NumericUpDown.
/// </summary>
public class CommitNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    /// <summary>True while the user is typing in the field (it has the keyboard focus): the text is not parsed yet.</summary>
    internal bool IsTyping => TypingOverride ?? IsKeyboardFocusWithin;

    /// <summary>Tests only: replaces the focus check (a headless control never has the keyboard focus).</summary>
    internal bool? TypingOverride { get; set; }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        if (IsTyping) return;    // committed by Enter / focus loss (NumericUpDown.CommitInput)
        base.OnTextChanged(oldValue, newValue);
    }
}
