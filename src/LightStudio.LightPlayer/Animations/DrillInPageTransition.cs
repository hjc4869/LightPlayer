using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace LightStudio.LightPlayer.Animations;

/// <summary>
/// The incoming page fades in while scaling up slightly toward its final size and the
/// outgoing page fades out while scaling past it, giving the "drill in / drill out" feel.
/// When the transition is reversed (back navigation) the scale directions are swapped.
/// </summary>
public sealed class DrillInPageTransition : IPageTransition
{
    /// <summary>Duration of the fade and scale animation.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// Scale the entering page starts from when navigating forward (and the exiting page settles to
    /// when navigating back).
    /// </summary>
    public double EnterScale { get; set; } = 0.94;

    /// <summary>
    /// Scale the exiting page grows to when navigating forward (and the entering page starts from
    /// when navigating back). Slightly above 1 makes the leaving page appear to pass the viewer.
    /// </summary>
    public double ExitScale { get; set; } = 1.04;

    /// <summary>Easing applied to both the fade and the scale.</summary>
    public Easing Easing { get; set; } = new CubicEaseOut();

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tasks = new List<Task>();

        if (from is not null)
        {
            var exitScale = forward ? ExitScale : EnterScale;
            tasks.Add(AnimateAsync(from, fromOpacity: 1d, toOpacity: 0d, fromScale: 1d, toScale: exitScale, cancellationToken));
        }

        if (to is not null)
        {
            to.IsVisible = true;
            var enterScale = forward ? EnterScale : ExitScale;
            tasks.Add(AnimateAsync(to, fromOpacity: 0d, toOpacity: 1d, fromScale: enterScale, toScale: 1d, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Leave the visuals in a clean state: hide the page we left and drop the temporary
        // render transforms so nothing lingers at animation priority for the next navigation.
        if (from is not null)
        {
            from.IsVisible = false;
            from.RenderTransform = null;
        }

        if (to is not null)
        {
            to.RenderTransform = null;
        }
    }

    private Task AnimateAsync(Visual visual, double fromOpacity, double toOpacity, double fromScale, double toScale, CancellationToken cancellationToken)
    {
        // Scale from the centre so the page grows/shrinks about its middle rather than the corner.
        visual.RenderTransformOrigin = RelativePoint.Center;

        var fade = new Animation
        {
            Duration = Duration,
            Easing = Easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, fromOpacity) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, toOpacity) } },
            },
        };

        var scale = new Animation
        {
            Duration = Duration,
            Easing = Easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleXProperty, fromScale),
                        new Setter(ScaleTransform.ScaleYProperty, fromScale),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleXProperty, toScale),
                        new Setter(ScaleTransform.ScaleYProperty, toScale),
                    },
                },
            },
        };

        return Task.WhenAll(
            fade.RunAsync(visual, cancellationToken),
            scale.RunAsync(visual, cancellationToken));
    }
}
