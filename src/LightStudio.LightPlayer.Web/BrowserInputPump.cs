using System;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Keeps Avalonia's dispatcher loop ticking so that queued browser input gets dispatched.
/// </summary>
/// <remarks>
/// On the threaded runtime Avalonia runs its UI thread on a managed thread of its own and hands it
/// browser input through <c>ManualRawEventGrouperDispatchQueue</c>, whose <c>Add</c> only enqueues:
/// the dispatcher polls that queue before it waits, and nothing wakes it when input arrives. The
/// queue is therefore drained on whatever wake-up happens to come next, which while the app is
/// otherwise idle can be seconds away; the UI then looks frozen even though it lays out and renders
/// normally. This timer is the wake-up the queue does not send, so its interval is the worst-case
/// input latency.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class BrowserInputPump
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    public static void Start() =>
        DispatcherTimer.Run(static () => true, Interval, DispatcherPriority.Input);
}
