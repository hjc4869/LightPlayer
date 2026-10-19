using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LightStudio.LightPlayer.Converters;

/// <summary>
/// True when the bound number is above zero. Used on a row's scroll buttons, which are bound to how
/// far the row can scroll and so are only shown once it holds more than it can display.
/// </summary>
public sealed class PositiveNumberConverter : IValueConverter
{
    public static PositiveNumberConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number && number > 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
