using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Animations;

/// <summary>
/// Horizontal push/pop transition used by the narrow (mobile) layout for hierarchical navigation
/// (drilling into and back out of album/artist detail and now-playing pages). Navigating forward
/// slides the incoming page in from the right while the outgoing page slides out to the left; a back
/// navigation reverses both directions, matching a left/right swipe.
/// </summary>
public sealed class SlidePageTransition : IPageTransition
{
    /// <summary>Duration of the horizontal slide.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(280);

    /// <summary>Easing applied to the slide.</summary>
    public Easing Easing { get; set; } = new CubicEaseOut();

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;
        return Begin(from, to, forward).RunAsync(cancellationToken);
    }

    internal PageTransitionSession Begin(Visual? from, Visual? to, bool forward)
    {
        double distance = Math.Max(0, ResolveDistance(from, to));
        var outgoingTransform = new TranslateTransform();
        var incomingTransform = new TranslateTransform();
        var outgoing = from is null ? null : new PageTransitionSession.VisualState(from, outgoingTransform);
        var incoming = to is null ? null : new PageTransitionSession.VisualState(to, incomingTransform);
        var session = new PageTransitionSession(distance > 0 ? Duration : TimeSpan.Zero, Easing, progress =>
        {
            outgoingTransform.X = (forward ? -distance : distance) * progress;
            incomingTransform.X = (forward ? distance : -distance) * (1 - progress);
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

    // The host presenter spans the full content area, so its width is the travel distance. Prefer it
    // over the pages' own bounds: a page that was only just inserted into the tree (the first detail
    // opened in a master-detail pane, for instance) has not been measured yet and still reports zero.
    private static double ResolveDistance(Visual? from, Visual? to)
    {
        var parent = (from ?? to)?.GetVisualParent();
        if (parent is not null && parent.Bounds.Width > 0d)
        {
            return parent.Bounds.Width;
        }

        if (from is not null && from.Bounds.Width > 0d)
        {
            return from.Bounds.Width;
        }

        return to?.Bounds.Width ?? 0d;
    }
}
