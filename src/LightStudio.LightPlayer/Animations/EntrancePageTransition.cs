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
/// Page transition when switching between top-level pages (Home, Music, Albums, Artists, Playlists,
/// Settings and Search). The incoming page slides up a short distance while fading in and the
/// outgoing page fades out in place. The motion is deliberately small and quick so that switching
/// root pages feels light compared with the drill-in transition used for detail pages.
/// </summary>
public sealed class EntrancePageTransition : IPageTransition
{
    /// <summary>Duration of the fade and slide.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// Distance, in device-independent pixels, the entering page travels upward as it settles into
    /// place. Kept small so only a sliver of vertical motion shows.
    /// </summary>
    public double VerticalOffset { get; set; } = 48d;

    /// <summary>Easing applied to both the fade and the slide.</summary>
    public Easing Easing { get; set; } = new CubicEaseOut();

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tasks = new List<Task>();

        // The outgoing page just fades out in place; only the entering page carries the slide so the
        // two pages never appear to move together. The entrance looks the same in both directions.
        if (from is not null)
        {
            tasks.Add(FadeAsync(from, fromOpacity: 1d, toOpacity: 0d, cancellationToken));
        }

        if (to is not null)
        {
            to.IsVisible = true;
            tasks.Add(SlideInAsync(to, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Drop the temporary render transforms and hide the page we left so nothing lingers at
        // animation priority for the next navigation.
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

    private Task FadeAsync(Visual visual, double fromOpacity, double toOpacity, CancellationToken cancellationToken)
    {
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

        return fade.RunAsync(visual, cancellationToken);
    }

    private Task SlideInAsync(Visual visual, CancellationToken cancellationToken)
    {
        var fade = new Animation
        {
            Duration = Duration,
            Easing = Easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            },
        };

        // Positive Y starts the page below its final spot; animating to 0 slides it upward into place.
        var slide = new Animation
        {
            Duration = Duration,
            Easing = Easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.YProperty, VerticalOffset) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.YProperty, 0d) } },
            },
        };

        return Task.WhenAll(
            fade.RunAsync(visual, cancellationToken),
            slide.RunAsync(visual, cancellationToken));
    }
}
