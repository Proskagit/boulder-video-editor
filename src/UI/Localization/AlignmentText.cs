using System.Globalization;
using AiVideoEditor.Core.Entities;
using Avalonia.Data.Converters;

namespace AiVideoEditor.UI.Localization;

/// <summary>A text alignment as the user reads it (D039 §2): the drop-down shows these, never the enum's names.</summary>
public sealed class AlignmentText : IValueConverter
{
    public static AlignmentText Instance { get; } = new();

    public static string Of(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Left => Strings.Align_Left,
        TextAlignment.Center => Strings.Align_Center,
        TextAlignment.Right => Strings.Align_Right,
        _ => alignment.ToString()
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TextAlignment alignment ? Of(alignment) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
