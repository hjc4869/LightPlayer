using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Draws an AppKit switch track. While pressed, the knob becomes a lens that shows the track minified
/// beneath it, and the track outside the lens keeps its full size.
/// </summary>
public sealed class MacSwitchTrack : Control
{
    private const double KnobInset = 2;
    private const double RestingLensScale = 0.63;
    private const double MinifiedWidthScale = 0.86;
    private const double MinifiedHeightScale = 0.75;

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<MacSwitchTrack>();

    /// <summary>Horizontal offset of the knob from its leading position.</summary>
    public static readonly StyledProperty<double> KnobOffsetProperty =
        AvaloniaProperty.Register<MacSwitchTrack, double>(nameof(KnobOffset));

    /// <summary>How far the lens has formed, from 0 at rest to 1 while pressed.</summary>
    public static readonly StyledProperty<double> LensProgressProperty =
        AvaloniaProperty.Register<MacSwitchTrack, double>(nameof(LensProgress));

    /// <summary>Width of the knob the lens forms around.</summary>
    public static readonly StyledProperty<double> KnobWidthProperty =
        AvaloniaProperty.Register<MacSwitchTrack, double>(nameof(KnobWidth), 26);

    /// <summary>Size of the fully formed lens.</summary>
    public static readonly StyledProperty<Size> LensSizeProperty =
        AvaloniaProperty.Register<MacSwitchTrack, Size>(nameof(LensSize), new Size(42, 27));

    static MacSwitchTrack()
    {
        AffectsRender<MacSwitchTrack>(BackgroundProperty, KnobOffsetProperty, LensProgressProperty, KnobWidthProperty, LensSizeProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public double KnobOffset
    {
        get => GetValue(KnobOffsetProperty);
        set => SetValue(KnobOffsetProperty, value);
    }

    public double LensProgress
    {
        get => GetValue(LensProgressProperty);
        set => SetValue(LensProgressProperty, value);
    }

    public double KnobWidth
    {
        get => GetValue(KnobWidthProperty);
        set => SetValue(KnobWidthProperty, value);
    }

    public Size LensSize
    {
        get => GetValue(LensSizeProperty);
        set => SetValue(LensSizeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Background is not { } brush)
        {
            return;
        }

        var track = new Rect(Bounds.Size);
        var shape = new RoundedRect(track, track.Height / 2);
        var progress = double.IsFinite(LensProgress) ? Math.Clamp(LensProgress, 0, 1) : 0;
        if (progress <= 0)
        {
            context.DrawRectangle(brush, null, shape);
            return;
        }

        var offset = double.IsFinite(KnobOffset) ? KnobOffset : 0;
        var center = new Point(KnobInset + offset + KnobWidth / 2, track.Height / 2);
        var lensScale = RestingLensScale + (1 - RestingLensScale) * progress;
        var lensSize = new Size(LensSize.Width * lensScale, LensSize.Height * lensScale);
        var lens = new Rect(center - new Point(lensSize.Width / 2, lensSize.Height / 2), lensSize);
        var lensGeometry = new RectangleGeometry(lens, lens.Height / 2, lens.Height / 2);

        using (context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                   new RectangleGeometry(track), lensGeometry)))
        {
            context.DrawRectangle(brush, null, shape);
        }

        using (context.PushGeometryClip(lensGeometry))
        {
            using (context.PushOpacity(1 - progress))
            {
                context.DrawRectangle(brush, null, shape);
            }

            var widthScale = 1 + (MinifiedWidthScale - 1) * progress;
            var heightScale = 1 + (MinifiedHeightScale - 1) * progress;
            var minified = new Rect(
                center.X - (center.X - track.X) * widthScale,
                center.Y - (center.Y - track.Y) * heightScale,
                track.Width * widthScale,
                track.Height * heightScale);
            using (context.PushOpacity(progress))
            {
                context.DrawRectangle(brush, null, new RoundedRect(minified, minified.Height / 2));
            }
        }
    }
}
