using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// One cell of the video library grid. The caption sits over either the preview fade or the
/// theme-aware placeholder surface.
/// </summary>
/// <remarks>
/// No drag handling and no queue buttons: videos are never mixed into the music queue, so the tile
/// only opens what it represents.
/// </remarks>
public partial class VideoTile : UserControl
{
    // Alpha ramp of the caption fade, top to bottom.
    private static readonly (double Offset, double Alpha)[] FadeStops =
    [
        (0.00, 0.00),
        (0.45, 0.45),
        (0.70, 0.72),
        (1.00, 0.94),
    ];

    private IDisposable? fadeSubscription;

    public VideoTile()
    {
        InitializeComponent();
        VideoTilePresenter.Track(this, PlaceholderGlyph, TypeGlyph);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        fadeSubscription?.Dispose();
        fadeSubscription = this.GetResourceObservable("AppPageBackgroundBrush")
            .Subscribe(new AnonymousObserver<object?>(value => ApplyFade(value as ISolidColorBrush)));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        fadeSubscription?.Dispose();
        fadeSubscription = null;
    }

    private void ApplyFade(ISolidColorBrush? pageBackground)
    {
        if (FadeOverlay is null || pageBackground is null)
        {
            return;
        }

        var color = pageBackground.Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };

        foreach (var (offset, alpha) in FadeStops)
        {
            // Only the alpha varies; keeping the RGB constant avoids the muddy interpolation a fade
            // from Colors.Transparent would produce.
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B),
                offset));
        }

        FadeOverlay.Background = brush;
    }
}
