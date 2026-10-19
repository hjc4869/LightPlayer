using System;
using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Keeps the standard stack panel's realization behavior while reserving vertical extent for rows
/// that an incremental data source has not loaded yet.
/// </summary>
public sealed class IncrementalVirtualizingStackPanel : VirtualizingStackPanel
{
    public static readonly StyledProperty<int> TotalCountProperty =
        AvaloniaProperty.Register<IncrementalVirtualizingStackPanel, int>(nameof(TotalCount));

    static IncrementalVirtualizingStackPanel()
    {
        AffectsMeasure<IncrementalVirtualizingStackPanel>(TotalCountProperty);
    }

    public int TotalCount
    {
        get => GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        var loadedCount = Items.Count;
        var virtualHeight = loadedCount > 0
            ? measured.Height * Math.Max(loadedCount, TotalCount) / loadedCount
            : measured.Height;
        return new Size(measured.Width, Math.Max(measured.Height, virtualHeight));
    }
}