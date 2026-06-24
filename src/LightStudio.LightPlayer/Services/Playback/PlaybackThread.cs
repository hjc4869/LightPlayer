using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// A dedicated single-threaded message pump with its own
/// <see cref="SynchronizationContext"/>. The validated <see cref="PlaybackQueueController"/>
/// has single-thread affinity (its queue and decode loop must not be mutated
/// concurrently), so every interaction with it is funnelled onto this one
/// thread. Because the context is installed on the pump thread, <c>await</c>
/// continuations inside posted work flow back to the same thread, which keeps
/// the async playback loop, queue edits, and status polling strictly serialized.
/// </summary>
internal sealed class PlaybackThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly Thread thread;
    private readonly PumpSynchronizationContext context;
    private bool disposed;

    public PlaybackThread(string name)
    {
        context = new PumpSynchronizationContext(queue);
        thread = new Thread(Run)
        {
            Name = name,
            IsBackground = true,
        };
        thread.Start();
    }

    /// <summary>True when called from the pump thread.</summary>
    public bool IsOnThread => SynchronizationContext.Current == context;

    /// <summary>Queues an action to run on the pump thread.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            context.Post(static state => ((Action)state!).Invoke(), action);
        }
        catch (InvalidOperationException)
        {
            // The pump was completed concurrently; drop the work.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.CompleteAdding();
        if (thread.IsAlive && Thread.CurrentThread != thread)
        {
            thread.Join();
        }

        queue.Dispose();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        foreach (var (callback, state) in queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                // A failing continuation must never tear down the pump thread.
                System.Diagnostics.Debug.WriteLine($"Playback pump callback failed: {ex}");
            }
        }
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> queue;

        public PumpSynchronizationContext(BlockingCollection<(SendOrPostCallback, object?)> queue)
        {
            this.queue = queue;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!queue.IsAddingCompleted)
            {
                queue.Add((d, state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Synchronous Send is not supported by the playback pump.");

        public override SynchronizationContext CreateCopy() => this;
    }
}
