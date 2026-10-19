using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace LightStudio.LightPlayer.Converters;

/// <summary>
/// Adapts a page's content inset to the shell's narrow (mobile) layout. The converter parameter holds
/// the wide-layout thickness (e.g. <c>"32,32,32,0"</c>) and, optionally, an explicit narrow-layout
/// thickness after a <c>'|'</c> separator (e.g. <c>"32,32,32,0|8,4,8,0"</c>). When the bound value (a
/// page's <c>IsNarrow</c> flag) is <see langword="true"/> the narrow thickness is returned, defaulting
/// to <see cref="NarrowSide"/> on every side when it is not specified. Used on the scroll viewers'
/// inner containers so the vertical inset scrolls with the content and the horizontal inset tightens
/// on narrow layouts.
/// </summary>
public sealed class NarrowInsetConverter : IValueConverter
{
    /// <summary>Inset applied on narrow layouts when the parameter omits an explicit narrow thickness.</summary>
    public const double NarrowSide = 5d;

    public static NarrowInsetConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var narrow = value is true;

        switch (parameter)
        {
            case Thickness thickness:
                return narrow ? new Thickness(NarrowSide) : thickness;

            case string text:
            {
                var separator = text.IndexOf('|');
                if (separator < 0)
                    return narrow ? new Thickness(NarrowSide) : Thickness.Parse(text);

                return narrow
                    ? Thickness.Parse(text[(separator + 1)..])
                    : Thickness.Parse(text[..separator]);
            }

            default:
                return narrow ? new Thickness(NarrowSide) : default(Thickness);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
