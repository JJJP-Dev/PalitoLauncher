using System.Globalization;
using System.Windows.Data;

namespace PalitoLauncher.Converters;

/// <summary>Niega un bool (p.ej. IsEnabled = !IsOverlayOpen).</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
