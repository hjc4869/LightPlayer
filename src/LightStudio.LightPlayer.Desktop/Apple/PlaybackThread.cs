using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Apple;

internal sealed class PlaybackThread : IDisposable, IAsyncDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly object lifetimeGate = new();
    private readonly Thread thread;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? periodicAction;
    private TimeSpan periodicInterval;
    private long nextPeriodicTimestamp;
    private bool disposed;

    public PlaybackThread(string name)
    {
        thread = new Thread(Run)
        {
            Name = name,
            IsBackground = true,
        };
        thread.Start();
    }

    public bool IsOnThread => Thread.CurrentThread == thread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!queue.TryAdd((static state => ((Action)state!).Invoke(), action)))
            {
                throw new ObjectDisposedException(nameof(PlaybackThread));
            }
        }
    }

    public void StartPeriodic(Action action, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        Invoke(() =>
        {
            periodicAction = action;
            periodicInterval = interval;
            nextPeriodicTimestamp = Stopwatch.GetTimestamp();
        });
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsOnThread)
        {
            action();
            return;
        }

        using var completed = new ManualResetEventSlim(false);
        Exception? error = null;
        Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait();

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested &&
                exception.CancellationToken == cancellationToken)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        Post(Execute);
        return completion.Task;
    }

    public void Dispose()
    {
        var completion = DisposeAsync();
        if (!IsOnThread)
        {
            completion.GetAwaiter().GetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate)
        {
            if (!disposed)
            {
                disposed = true;
                queue.CompleteAdding();
            }
        }

        return new ValueTask(stopped.Task);
    }

    private void Run()
    {
        try
        {
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var work, GetWaitTimeoutMilliseconds()))
                {
                    RunCallback(work.Callback, work.State);
                }

                if (periodicAction is not null && Stopwatch.GetTimestamp() >= nextPeriodicTimestamp)
                {
                    RunCallback(static state => ((Action)state!).Invoke(), periodicAction);
                    nextPeriodicTimestamp = Stopwatch.GetTimestamp()
                        + (long)(periodicInterval.TotalSeconds * Stopwatch.Frequency);
                }
            }
        }
        finally
        {
            queue.Dispose();
            stopped.TrySetResult();
        }
    }

    private int GetWaitTimeoutMilliseconds()
    {
        if (periodicAction is null)
        {
            return Timeout.Infinite;
        }

        var remainingTicks = nextPeriodicTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        var milliseconds = Math.Ceiling((double)remainingTicks * 1000 / Stopwatch.Frequency);
        return (int)Math.Clamp(milliseconds, 1, int.MaxValue);
    }

    private static void RunCallback(SendOrPostCallback callback, object? state)
    {
        try
        {
            callback(state);
        }
        catch (Exception ex)
        {
            AppLog.Error("Audio.CoreAudio", "An audio playback callback failed.", ex);
        }
    }
}