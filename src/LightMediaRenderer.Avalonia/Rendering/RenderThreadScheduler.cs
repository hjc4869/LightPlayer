using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// Gives platform code a way onto the compositor's render thread.
/// </summary>
/// <remarks>
/// Graphics resources belong to the thread that owns the rendering context, and on WebAssembly that
/// is a hard boundary rather than a convention: Avalonia renders on a dedicated web worker holding
/// the only WebGL context, so a texture uploaded from anywhere else does not exist as far as the
/// renderer is concerned. Avalonia exposes no scheduler for that thread -- <c>PostServerJob</c> and
/// <c>InvokeServerJobAsync</c> are internal -- but a custom composition visual is called back on it,
/// which is enough to capture the thread's synchronization context and to run work from inside a
/// paint, where backends that bind their context per pass have it bound.
/// </remarks>
public static class RenderThreadScheduler
{
    // A visual can only reach the compositor it is currently attached to, and a backend can have
    // one per top level, so every attached visual carries its own probe.
    private static readonly Dictionary<Visual, CompositionCustomVisual> hosts = [];
    private static volatile Func<bool>? insideRenderPass;
    private static Visual? renderPassVisual;
    private static SynchronizationContext? captured;
    private static readonly TaskCompletionSource<SynchronizationContext> contextReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool reached;

    /// <summary>
    /// The render thread's context, or null when that thread has no context to post to. Only
    /// meaningful once <see cref="IsReached"/> is true.
    /// </summary>
    public static SynchronizationContext? Context => Volatile.Read(ref captured);

    internal static void DisposeOnRenderThread(Action dispose)
    {
        if (Context is { } scheduler && Environment.CurrentManagedThreadId != ThreadId)
            scheduler.Post(static state => ((Action)state!).Invoke(), dispose);
        else
            dispose();
    }

    /// <summary>
    /// Waits until an attached visual has reached the render thread. A decoder opened before its
    /// page is attached uses this instead of permanently choosing the caller's JavaScript context.
    /// </summary>
    public static Task<SynchronizationContext> WaitForContextAsync(CancellationToken cancellationToken = default)
    {
        SynchronizationContext? context = Context;
        return context is not null
            ? Task.FromResult(context)
            : contextReady.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Whether the render thread has called back at least once.</summary>
    public static bool IsReached => reached;

    /// <summary>Managed id of the render thread, for telling it apart from the caller's.</summary>
    public static int ThreadId { get; private set; }

    /// <summary>
    /// Runs on the render thread from inside a paint, which on some backends is the only time the
    /// graphics context is bound. Return false to stop being called. A repaint is forced on every
    /// frame for as long as this is set, so it should be cleared as soon as it has what it needs.
    /// </summary>
    public static Func<bool>? InsideRenderPass
    {
        get => insideRenderPass;
        set
        {
            insideRenderPass = value;
            if (value is not null)
                RequestRenderPasses();
        }
    }

    /// <summary>
    /// Which view's paints <see cref="InsideRenderPass"/> may run inside. Where each top level has
    /// a graphics context of its own (the browser), work meant for one of them must not be run
    /// under another; null leaves it to whichever attached visual paints first.
    /// </summary>
    /// <remarks>Written on the UI thread and read on the render thread.</remarks>
    public static Visual? RenderPassVisual
    {
        get => Volatile.Read(ref renderPassVisual);
        set => Volatile.Write(ref renderPassVisual, value);
    }

    /// <summary>
    /// Starts the heartbeat that reaches the render thread. Nothing happens until
    /// <paramref name="visual"/> belongs to a compositor.
    /// </summary>
    public static void Attach(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        if (ElementComposition.GetElementVisual(visual) is not { } element)
            return;

        if (hosts.TryGetValue(visual, out CompositionCustomVisual? existing))
        {
            // A probe belongs to the compositor that made it, so moving between top levels on a
            // backend with one compositor each leaves the old one behind.
            if (ReferenceEquals(existing.Compositor, element.Compositor))
                return;

            hosts.Remove(visual);
        }

        CompositionCustomVisual created = element.Compositor.CreateCustomVisual(new RenderThreadProbe(visual));

        // A zero-sized visual is never painted, and a paint is the only place the graphics context
        // is guaranteed to be bound.
        created.Size = new Vector(1, 1);
        ElementComposition.SetElementChildVisual(visual, created);
        hosts[visual] = created;
        created.SendHandlerMessage(RenderThreadProbe.Start);
    }

    public static void Detach(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        if (!hosts.Remove(visual, out CompositionCustomVisual? probe))
            return;

        if (ReferenceEquals(ElementComposition.GetElementChildVisual(visual), probe))
            ElementComposition.SetElementChildVisual(visual, null);
    }

    /// <summary>
    /// Runs once on the render thread the first time it is reached, before anything else needs it.
    /// Set this during startup; it is the only way to get work onto that thread eagerly.
    /// </summary>
    public static Action? RenderThreadInitializer { get; set; }

    internal static bool WantsRenderPass => insideRenderPass is not null;

    /// <summary>
    /// Runs the pending work from a paint already known to be the right one: the picture is being
    /// drawn, so this is the view that owns it and the context bound is the one it draws with.
    /// </summary>
    public static void EnterRenderPass() => RunRenderPass();

    internal static void EnterRenderPass(Visual owner)
    {
        // A probe paints on every attached view, and only one of them holds the picture.
        if (RenderPassVisual is { } pinned && !ReferenceEquals(pinned, owner))
            return;

        RunRenderPass();
    }

    internal static void Capture()
    {
        if (reached)
            return;

        if (SynchronizationContext.Current is not { } context)
            return;

        ThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref captured, context);
        reached = true;

        try
        {
            RenderThreadInitializer?.Invoke();
        }
        catch (Exception exception)
        {
            // A platform that cannot set itself up here simply does without the render thread.
            AppLog.Warning("Video.Rendering", "Render-thread initialization failed.", exception);
        }

        contextReady.TrySetResult(context);
    }

    private static void RunRenderPass()
    {
        Func<bool>? hook = insideRenderPass;
        if (hook is null)
            return;

        try
        {
            if (!hook())
                insideRenderPass = null;
        }
        catch (Exception exception)
        {
            AppLog.Error("Video.Rendering", "The render-pass hook failed.", exception);
            insideRenderPass = null;
        }
    }

    /// <summary>Wakes the heartbeat, which stops itself again whenever nothing needs painting.</summary>
    private static void RequestRenderPasses()
    {
        // Composition objects are owned by the UI thread, and the caller is typically not on it.
        Dispatcher.UIThread.Post(static () =>
        {
            RenderPassVisual?.InvalidateVisual();

            foreach (CompositionCustomVisual visual in hosts.Values)
                visual.SendHandlerMessage(RenderThreadProbe.Start);
        });
    }
}

/// <summary>Draws nothing; it exists only to be called back on the render thread.</summary>
internal sealed class RenderThreadProbe(Visual owner) : CompositionCustomVisualHandler
{
    public static readonly object Start = new();

    public override void OnMessage(object message)
    {
        if (ReferenceEquals(message, Start))
            RegisterForNextAnimationFrameUpdate();
    }

    public override void OnAnimationFrameUpdate()
    {
        RenderThreadScheduler.Capture();

        // The clock is only kept running while something still needs a paint.
        if (RenderThreadScheduler.WantsRenderPass)
        {
            RegisterForNextAnimationFrameUpdate();
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext drawingContext) =>
        RenderThreadScheduler.EnterRenderPass(owner);

    public override Rect GetRenderBounds() => new(0, 0, 1, 1);
}
