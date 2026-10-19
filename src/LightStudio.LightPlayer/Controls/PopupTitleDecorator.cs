using System;
using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Positions a pop-up button title as AppKit does: at <see cref="Inset"/> while it fits, otherwise
/// at <see cref="CompactInset"/> with the remaining width for trimming.
/// </summary>
public sealed class PopupTitleDecorator : Decorator
{
    public static readonly StyledProperty<double> InsetProperty =
        AvaloniaProperty.Register<PopupTitleDecorator, double>(nameof(Inset), 12);

    public static readonly StyledProperty<double> CompactInsetProperty =
        AvaloniaProperty.Register<PopupTitleDecorator, double>(nameof(CompactInset), 5);

    static PopupTitleDecorator()
    {
        AffectsMeasure<PopupTitleDecorator>(InsetProperty, CompactInsetProperty);
    }

    public double Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public double CompactInset
    {
        get => GetValue(CompactInsetProperty);
        set => SetValue(CompactInsetProperty, value);
    }

    private bool Fits(double width, double childWidth) => Inset + childWidth <= width;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var natural = child.DesiredSize;
        if (double.IsInfinity(availableSize.Width) || Fits(availableSize.Width, natural.Width))
        {
            return new Size(Inset + natural.Width, natural.Height);
        }

        child.Measure(new Size(Math.Max(0, availableSize.Width - CompactInset), availableSize.Height));
        return new Size(availableSize.Width, child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }

        child.Measure(new Size(double.PositiveInfinity, finalSize.Height));
        var natural = child.DesiredSize.Width;
        if (Fits(finalSize.Width, natural))
        {
            child.Arrange(new Rect(Inset, 0, natural, finalSize.Height));
        }
        else
        {
            var width = Math.Max(0, finalSize.Width - CompactInset);
            child.Measure(new Size(width, finalSize.Height));
            child.Arrange(new Rect(CompactInset, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
