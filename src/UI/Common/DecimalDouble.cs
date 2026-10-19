using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Binds a slider (double) to a numeric field's value (decimal?, D039 §8): an empty field leaves the slider where it is; a
/// slider position goes back as a whole number (the slider snaps to whole steps), so the field and the model see the same
/// value the slider shows.
/// </summary>
public sealed class DecimalDouble : IValueConverter
{
    public static DecimalDouble Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? (double)d : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double v ? (decimal?)Math.Round((decimal)v) : BindingOperations.DoNothing;
}
