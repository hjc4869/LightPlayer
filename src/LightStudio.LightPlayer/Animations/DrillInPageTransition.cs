using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;

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

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;
        return Begin(from, to, forward).RunAsync(cancellationToken);
    }

    internal PageTransitionSession Begin(Visual? from, Visual? to, bool forward)
    {
        double exitScale = forward ? ExitScale : EnterScale;
        double enterScale = forward ? EnterScale : ExitScale;
        var outgoingTransform = new ScaleTransform();
        var incomingTransform = new ScaleTransform();
        var outgoing = from is null ? null : new PageTransitionSession.VisualState(from, outgoingTransform, centered: true);
        var incoming = to is null ? null : new PageTransitionSession.VisualState(to, incomingTransform, centered: true);
        var session = new PageTransitionSession(Duration, Easing, progress =>
        {
            outgoingTransform.ScaleX = outgoingTransform.ScaleY = 1 + (exitScale - 1) * progress;
            incomingTransform.ScaleX = incomingTransform.ScaleY = enterScale + (1 - enterScale) * progress;
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
        return session;
    }
}
