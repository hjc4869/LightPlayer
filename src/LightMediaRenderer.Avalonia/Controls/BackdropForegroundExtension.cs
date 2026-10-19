using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;
using Avalonia.Media;

namespace LightMediaRenderer.Avalonia.Controls;

public sealed class BackdropForegroundExtension : MarkupExtension
{
    public Color Color { get; set; } = Colors.White;
    public string? ResourceKey { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var source = new CompiledBindingExtension(new CompiledBindingPathBuilder()
            .Self()
            .Property(BackdropBorder.ForegroundSourceProperty, PropertyInfoAccessorFactory.CreateAvaloniaPropertyAccessor)
            .Build());
        return new MultiBinding
        {
            Bindings =
            {
                source.ProvideValue(serviceProvider),
                ResourceKey is { } key
                    ? new DynamicResourceExtension(key).ProvideValue(serviceProvider)
                    : new CompiledBindingExtension { Source = new SolidColorBrush(Color) }.ProvideValue(serviceProvider),
                new CompiledBindingExtension(new CompiledBindingPathBuilder().Self().Build()).ProvideValue(serviceProvider),
            },
            Converter = ForegroundConverter.Instance,
        };
    }

    private sealed class ForegroundConverter : IMultiValueConverter
    {
        public static ForegroundConverter Instance { get; } = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 3 || values[1] is not IBrush original)
                return global::Avalonia.AvaloniaProperty.UnsetValue;
            return values[0] is BackdropBorder backdrop && values[2] is Control target
                ? backdrop.CreateForeground(original, target) : original;
        }
    }
}