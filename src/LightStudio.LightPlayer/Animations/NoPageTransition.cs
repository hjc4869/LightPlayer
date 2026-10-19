using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;

namespace LightStudio.LightPlayer.Animations;

/// <summary>
/// A page transition that swaps pages instantly with no animation. Used by the narrow (mobile)
/// layout when switching between top-level views (tapping a navigation button or swiping between
/// library tabs), where an animated switch is undesirable.
/// </summary>
public sealed class NoPageTransition : IPageTransition
{
    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from is not null)
        {
            from.IsVisible = false;
            from.RenderTransform = null;
        }

        if (to is null)
        {
            return Task.CompletedTask;
        }

        to.IsVisible = true;
        to.RenderTransform = null;

        return Task.CompletedTask;
    }
}

