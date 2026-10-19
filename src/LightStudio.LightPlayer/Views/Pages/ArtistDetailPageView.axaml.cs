using System;
using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class ArtistDetailPageView : UserControl
{
    private const double HeaderBreakpoint = 650;
    public ArtistDetailPageView()
    {
        InitializeComponent();
    }
    
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            HeaderActions.IsVisible = Bounds.Width >= HeaderBreakpoint;
            var wide = Bounds.Width > HeaderBreakpoint;
            HeaderBackdrop.IsVisible = wide;
            HeaderContent.MinHeight = wide ? Math.Max(200, Math.Min(1200, Bounds.Width) / 4) : 0;
            HeaderText.Margin = new Thickness(0, 32, 6, wide ? 22 : 0);
            PageContent.Spacing = wide ? 28 : 20;
        }
    }
}
