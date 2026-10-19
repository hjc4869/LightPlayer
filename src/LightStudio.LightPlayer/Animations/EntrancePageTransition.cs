using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;

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

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || from is null && to is null)
            return Task.CompletedTask;

        var incomingTransform = new TranslateTransform();
        var outgoing = from is null ? null : new PageTransitionSession.VisualState(from);
        var incoming = to is null ? null : new PageTransitionSession.VisualState(to, incomingTransform);
        var session = new PageTransitionSession(Duration, Easing, progress =>
        {
            incomingTransform.Y = VerticalOffset * (1 - progress);
            if (outgoing is not null)
                outgoing.Opacity = outgoing.OriginalOpacity * (1 - progress);
            if (incoming is not null)
                incoming.Opacity = incoming.OriginalOpacity * progress;
        }, commit =>
        {
            if (commit)
            {
                outgoing?.Complete(false);
                incoming?.Complete(true);
            }
            else
            {
                outgoing?.Dispose();
                incoming?.Dispose();
            }
        });
        session.Observe(from ?? to);
        session.Update(0);
        return session.RunAsync(cancellationToken);
    }
}
