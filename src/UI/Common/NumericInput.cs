using System.Globalization;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Text rules shared by every numeric field (NumericUpDown) in the UI. Avalonia parses typed text with
/// <see cref="NumberStyles.Any"/> by default, which accepts whitespace, group separators (a space in
/// ru-RU, so "9 0" became 90), parentheses, trailing signs, exponents and currency symbols. Fields
/// accept only an optional leading sign, digits and the culture's decimal separator; other text keeps
/// the last value — the model is not changed — and the field shows the value again when it loses
/// focus. An empty field has no value (null in the view model, never an edit). Ranges are still
/// checked by the edit service (D017).
/// </summary>
public static class NumericInput
{
    public static NumberStyles ParsingStyle { get; } = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
}
