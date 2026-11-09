using System;
using Avalonia.Controls;
using Avalonia.Layout;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class MediaPageView : UserControl
{
    // Two wide tiles (MinItemWidth 270), each with the 4-DIP spacing the tile grid reserves.
    private const double MinWideGridWidth = 2 * (270 + 4);
    private const double WideInset = 32;

    public MediaPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, MediaScrollViewer, GroupedView.ScrollViewer);
        IncrementalLoadingScroll.Track(this, MediaScrollViewer);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateWideGridFit(Bounds.Width);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateWideGridFit(e.NewSize.Width);
    }

    private void UpdateWideGridFit(double width)
    {
        if (width > 0 && DataContext is MediaPageViewModel page)
        {
            // Layout rounding snaps the insets to device pixels, so they can take part of a DIP from the grid.
            var inset = LayoutHelper.RoundLayoutValue(WideInset, LayoutHelper.GetLayoutScale(this));
            page.FitsOneWideColumn = width - (2 * inset) < MinWideGridWidth;
        }
    }
}
