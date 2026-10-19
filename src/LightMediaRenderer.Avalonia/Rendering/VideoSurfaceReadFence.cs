using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Rendering;

internal sealed unsafe class VideoSurfaceReadFence
{
    private const uint SyncGpuCommandsComplete = 0x9117;
    private const uint AlreadySignaled = 0x911A;
    private const uint TimeoutExpired = 0x911B;
    private const uint ConditionSatisfied = 0x911C;
    private const int MaximumPendingReads = 8;

    private static readonly ConditionalWeakTable<EglContext, VideoSurfaceReadFence> contexts = new();
    private static readonly ConditionalWeakTable<Compositor, CompositionCustomVisual> pollTargets = new();

    private readonly EglContext context;
    private CompositionCustomVisual? pollTarget;
    private readonly Queue<(VideoFrame Frame, IntPtr Fence)> pending = new();
    private readonly delegate* unmanaged<uint, uint, IntPtr> fenceSync;
    private readonly delegate* unmanaged<IntPtr, uint, ulong, uint> clientWaitSync;
    private readonly delegate* unmanaged<IntPtr, void> deleteSync;
    private bool polling;
    private bool failed;

    private VideoSurfaceReadFence(EglContext context)
    {
        this.context = context;
        GlVersion version = context.Version;
        bool supported = version.Major > 3 ||
            (version.Major == 3 && (version.Type == GlProfileType.OpenGLES || version.Minor >= 2));
        if (supported)
        {
            fenceSync = (delegate* unmanaged<uint, uint, IntPtr>)context.GlInterface.GetProcAddress("glFenceSync");
            clientWaitSync = (delegate* unmanaged<IntPtr, uint, ulong, uint>)context.GlInterface.GetProcAddress("glClientWaitSync");
            deleteSync = (delegate* unmanaged<IntPtr, void>)context.GlInterface.GetProcAddress("glDeleteSync");
        }
    }

    public static CompositionCustomVisual? GetPollTarget(Compositor? compositor) => compositor is null
        ? null
        : pollTargets.GetValue(compositor, static owner => owner.CreateCustomVisual(new FencePoller()));

    public static void CompleteRead(ISkiaSharpApiLease lease, VideoFrame frame, CompositionCustomVisual? pollTarget)
    {
        if (lease.GrContext is not { } graphics)
            return;

        graphics.Flush(submit: true);
        using ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi();
        if (api?.Context is not EglContext egl)
        {
            graphics.Submit(synchronous: true);
            return;
        }

        VideoSurfaceReadFence queue = contexts.GetValue(egl, static context => new VideoSurfaceReadFence(context));
        queue.pollTarget ??= pollTarget;
        queue.Retain(frame);
    }

    private void Retain(VideoFrame frame)
    {
        ReleaseCompleted();
        if (failed || pollTarget is null || fenceSync == null || clientWaitSync == null || deleteSync == null)
        {
            context.GlInterface.Finish();
            return;
        }

        if (pending.Count >= MaximumPendingReads)
        {
            context.GlInterface.Finish();
            ReleaseAll(deleteFences: true);
        }

        IntPtr fence = fenceSync(SyncGpuCommandsComplete, 0);
        if (fence == IntPtr.Zero)
        {
            failed = true;
            AppLog.Warning("Video.Surface", "Could not create a GPU read fence; waiting for surface reads synchronously.");
            context.GlInterface.Finish();
            ReleaseAll(deleteFences: true);
            return;
        }

        pending.Enqueue((frame.AddRef(), fence));
        context.GlInterface.Flush();
        if (!polling)
        {
            polling = true;
            SchedulePoll();
        }
    }

    private void SchedulePoll()
    {
        CompositionCustomVisual target = pollTarget!;
        Dispatcher.UIThread.Post(() =>
            DispatcherTimer.RunOnce(() => target.SendHandlerMessage(this), TimeSpan.FromMilliseconds(8)));
    }

    private void Poll()
    {
        try
        {
            if (context.IsLost)
                ReleaseAll(deleteFences: false);
            else
            {
                using IDisposable current = context.EnsureCurrent();
                ReleaseCompleted();
            }
        }
        catch (PlatformGraphicsContextLostException)
        {
            ReleaseAll(deleteFences: false);
        }
        catch (ObjectDisposedException)
        {
            ReleaseAll(deleteFences: false);
        }
        finally
        {
            polling = pending.Count != 0;
            if (polling)
                SchedulePoll();
        }
    }

    private void ReleaseCompleted()
    {
        while (pending.TryPeek(out var read))
        {
            uint result = clientWaitSync(read.Fence, 0, 0);
            if (result == TimeoutExpired)
                return;
            if (result is not AlreadySignaled and not ConditionSatisfied)
            {
                failed = true;
                AppLog.Warning("Video.Surface", $"GPU read fence wait returned 0x{result:X}; waiting for surface reads synchronously.");
                context.GlInterface.Finish();
                ReleaseAll(deleteFences: true);
                return;
            }

            pending.Dequeue();
            deleteSync(read.Fence);
            read.Frame.Release();
        }
    }

    private void ReleaseAll(bool deleteFences)
    {
        while (pending.TryDequeue(out var read))
        {
            if (deleteFences)
                deleteSync(read.Fence);
            read.Frame.Release();
        }
    }

    private sealed class FencePoller : CompositionCustomVisualHandler
    {
        public override void OnMessage(object message) => ((VideoSurfaceReadFence)message).Poll();

        public override void OnRender(ImmediateDrawingContext drawingContext) { }
    }
}