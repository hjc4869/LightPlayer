using System;
using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Wraps its children into rows of columns <see cref="MinItemWidth"/> wide, as many as fit the
/// available width. With <see cref="StretchItems"/> the columns widen equally to fill the row.
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<UniformWrapPanel, double>(nameof(MinItemWidth), 1d);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<UniformWrapPanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<UniformWrapPanel, double>(nameof(RowSpacing));

    public static readonly StyledProperty<bool> StretchItemsProperty =
        AvaloniaProperty.Register<UniformWrapPanel, bool>(nameof(StretchItems));

    static UniformWrapPanel()
    {
        AffectsMeasure<UniformWrapPanel>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty, StretchItemsProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public bool StretchItems
    {
        get => GetValue(StretchItemsProperty);
        set => SetValue(StretchItemsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (int columns, double itemWidth) = GetColumns(availableSize.Width);
        var constraint = new Size(itemWidth, double.PositiveInfinity);
        double height = 0, rowHeight = 0;
        int column = 0, rows = 0;
        foreach (Control child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            child.Measure(constraint);
            if (column == 0)
            {
                rows++;
            }

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++column == columns)
            {
                height += rowHeight;
                rowHeight = 0;
                column = 0;
            }
        }

        height += rowHeight + Math.Max(0, rows - 1) * RowSpacing;
        double width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : columns * itemWidth + Math.Max(0, columns - 1) * ColumnSpacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (int columns, double itemWidth) = GetColumns(finalSize.Width);
        double y = 0, rowHeight = 0;
        int column = 0, start = 0;
        for (int index = 0; index <= Children.Count; index++)
        {
            Control? child = index < Children.Count ? Children[index] : null;
            if (child is { IsVisible: false })
            {
                continue;
            }

            if (child is not null)
            {
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                column++;
            }

            // A full row, or the last partial one, is placed once its height is known.
            if (column == columns || (child is null && column > 0))
            {
                int placed = 0;
                for (int rowIndex = start; rowIndex < Math.Min(index + 1, Children.Count); rowIndex++)
                {
                    if (Children[rowIndex].IsVisible)
                    {
                        Children[rowIndex].Arrange(new Rect(placed++ * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
                    }
                }

                y += rowHeight + RowSpacing;
                rowHeight = 0;
                column = 0;
                start = index + 1;
            }
        }

        return finalSize;
    }

    private (int Columns, double ItemWidth) GetColumns(double width)
    {
        double minimum = Math.Max(1, MinItemWidth);
        if (!double.IsFinite(width))
        {
            return (Math.Max(1, Children.Count), minimum);
        }

        int columns = Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (minimum + ColumnSpacing)));
        if (!StretchItems)
        {
            return (columns, Math.Min(minimum, width));
        }

        // Whole DIPs keep the last column inside the row once each cell is rounded to pixels.
        return (columns, Math.Max(0, Math.Floor((width - (columns - 1) * ColumnSpacing) / columns)));
    }
}
