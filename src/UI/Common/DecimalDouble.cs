using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Binds a slider (double) to a numeric field's value (decimal?, D039 §8): an empty field leaves the slider where it is; the
/// slider's value goes back unchanged. FU-25.2: the binding also writes back after the field moved the slider, so rounding
/// here replaced a typed or saved fraction behind the field's back (a selected clip with 50,5 % showed 50 while the model
/// kept 50,5). A drag lands on whole steps by the slider's own snapping; the view models decide the precision they take.
/// </summary>
public sealed class DecimalDouble : IValueConverter
{
    public static DecimalDouble Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? (double)d : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double v ? (decimal?)(decimal)v : BindingOperations.DoNothing;
}
