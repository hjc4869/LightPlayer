using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Themes;

/// <summary>Touch metrics for the Apple UI style, added by <see cref="App"/> where <see cref="Services.PlatformServices.UsesTouchChrome"/> is set.</summary>
/// <remarks>
/// Scroll bars show only while their viewer scrolls, as iOS scroll indicators do: each scroll marks the
/// viewer <c>scrollActive</c> until it has been still for <see cref="IndicatorDelay"/>.
/// </remarks>
internal sealed class AppleTouchControls : Styles
{
    private static readonly TimeSpan IndicatorDelay = TimeSpan.FromSeconds(1);
    private static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> IndicatorTimers = new();

    static AppleTouchControls()
    {
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>(static (viewer, _) => ShowIndicators(viewer));
    }

    public AppleTouchControls() => AvaloniaXamlLoader.Load(this);

    private static void ShowIndicators(ScrollViewer viewer)
    {
        viewer.Classes.Set("scrollActive", true);
        var timer = IndicatorTimers.GetValue(viewer, static target =>
        {
            var weak = new WeakReference<ScrollViewer>(target);
            var created = new DispatcherTimer { Interval = IndicatorDelay };
            created.Tick += (_, _) =>
            {
                created.Stop();
                if (weak.TryGetTarget(out var owner))
                {
                    owner.Classes.Set("scrollActive", false);
                }
            };
            return created;
        });
        timer.Stop();
        timer.Start();
    }
}
