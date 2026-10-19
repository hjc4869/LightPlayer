using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Threading;

internal sealed class PlaybackDispatcher : SynchronizationContext
{
    private readonly SynchronizationContext? host = Current;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
    private int scheduled;

    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        pending.Enqueue((callback, state));
        Schedule();
    }

    public Task InvokeAsync(Func<Task> operation) => InvokeAsync(async () =>
    {
        await operation();
        return true;
    });

    public Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async state =>
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref scheduled, 1) != 0)
            return;
        if (host is not null)
            host.Post(static state => ((PlaybackDispatcher)state!).Dispatch(), this);
        else
            ThreadPool.QueueUserWorkItem(static state => state.Dispatch(), this, preferLocal: false);
    }

    private void Dispatch()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            for (int count = 0; count < 32 && pending.TryDequeue(out var work); count++)
            {
                try
                {
                    work.Callback(work.State);
                }
                catch (Exception exception)
                {
                    AppLog.Error("Playback.Dispatch", "A playback continuation failed.", exception);
                }
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
            Volatile.Write(ref scheduled, 0);
            if (!pending.IsEmpty)
                Schedule();
        }
    }
}