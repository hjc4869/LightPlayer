using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace LightStudio.LightPlayer.Converters;

/// <summary>
/// True when every bound value is true. A value its binding cannot resolve, such as a path through a null
/// object, counts as false rather than leaving the target at its default.
/// </summary>
public sealed class AllTrueConverter : IMultiValueConverter
{
    public static AllTrueConverter Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(value => value is true);
}
