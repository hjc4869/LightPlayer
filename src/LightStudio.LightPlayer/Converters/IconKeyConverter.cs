using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LightStudio.LightPlayer.Converters;

/// <summary>
/// Resolves a geometry resource key (e.g. "IconPlay") to the <see cref="Geometry"/>
/// stored in application resources, so view models can expose icon identifiers as
/// plain strings.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static IconKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            && resource is Geometry geometry)
        {
            return geometry;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
