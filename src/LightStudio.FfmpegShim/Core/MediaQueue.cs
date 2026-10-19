#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;

namespace LightStudio.FfmpegShim.Core;

/// <summary>
/// Bounded thread-safe hand-off between two pipeline stages. Bounded both by item count and by
/// total bytes so a queue of 4K frames cannot grow to gigabytes while still holding enough small
/// packets to absorb decode jitter.
/// </summary>
/// <remarks>
/// Blocked producers and consumers are released by <see cref="Abort"/> (seek) and
/// <see cref="Close"/> (shutdown) so no thread has to be interrupted or torn down to reconfigure
/// the pipeline. Items dropped on the floor are handed back to the pool through the recycle
/// callback supplied at construction, so a flush never leaks native memory.
/// </remarks>
public sealed class MediaQueue<T> : IMediaFrameSource<T>, IFrameProcessor<T> where T : class, IMediaQueueItem
{
    private readonly object sync = new();
    private readonly Queue<T> items = new();
    private readonly Action<T> recycle;
    private readonly Action<T>? retain;
    private readonly bool stampSerial;
    private readonly int capacity;
    private readonly long byteCapacity;

    private long byteCount;
    private int serial;
    private int abortGeneration;
    private bool closed;
    private int version;
    private TaskCompletionSource? changed;

    /// <param name="recycle">Returns a dropped item to its pool.</param>
    /// <param name="retain">
    /// Takes an extra reference for <see cref="TryPeek"/>, so a concurrent <see cref="Flush"/>
    /// cannot recycle the item the caller is still looking at. Required only for queues that are
    /// peeked.
    /// </param>
    /// <param name="stampSerial">
    /// True for packet queues, whose serial is authoritative. Frame queues carry the serial of the
    /// packet the frame was decoded from, so they must not be re-stamped.
    /// </param>
    public MediaQueue(
        string name,
        int capacity,
        long byteCapacity,
        Action<T> recycle,
        Action<T>? retain = null,
        bool stampSerial = true)
    {
        Name = name;
        this.capacity = capacity;
        this.byteCapacity = byteCapacity;
        this.recycle = recycle;
        this.retain = retain;
        this.stampSerial = stampSerial;
    }

    public string Name { get; }

    /// <summary>The number of items the queue holds before it reports full.</summary>
    public int Capacity => capacity;

    public long ByteCapacity => byteCapacity;

    public int Version
    {
        get { lock (sync) return version; }
    }

    public Task WaitForChangeAsync(int observedVersion, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (closed || version != observedVersion)
                return Task.CompletedTask;

            changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return changed.Task.WaitAsync(cancellationToken);
        }
    }

    public bool TryRead(out T frame) => TryDequeueNow(out frame);

    public bool TryWrite(T item)
    {
        lock (sync)
        {
            if (closed || IsFullLocked())
                return false;
            if (stampSerial)
                item.Serial = serial;
            items.Enqueue(item);
            byteCount += item.SizeInBytes;
            NotifyChangedLocked();
            return true;
        }
    }

    public bool TryPeek(out T frame) => TryPeek(out frame, wait: false);

    public bool ContainsEndOfStream(int frameSerial) =>
        Any(frame => frame.IsEndOfStream && frame.Serial == frameSerial);

    /// <summary>Serial of the run currently being produced. Bumped by every <see cref="Flush"/>.</summary>
    public int Serial
    {
        get { lock (sync) return serial; }
    }

    public int Count
    {
        get { lock (sync) return items.Count; }
    }

    public long ByteCount
    {
        get { lock (sync) return Volatile.Read(ref byteCount); }
    }

    /// <summary>True while <see cref="Enqueue"/> would block. Only meaningful to the sole producer.</summary>
    public bool IsFull
    {
        get { lock (sync) return IsFullLocked(); }
    }

    /// <summary>0..1 fill level against the tighter of the two budgets; drives the buffering UI.</summary>
    public double Occupancy
    {
        get
        {
            lock (sync)
            {
                double byCount = capacity > 0 ? (double)items.Count / capacity : 0;
                double byBytes = byteCapacity > 0 ? (double)byteCount / byteCapacity : 0;
                return Math.Clamp(Math.Max(byCount, byBytes), 0, 1);
            }
        }
    }

    /// <summary>
    /// Stamps <paramref name="item"/> with the current serial and appends it, blocking while the
    /// queue is full. Returns false when the wait was cut short by a flush or shutdown, in which
    /// case the item has already been recycled.
    /// </summary>
    /// <param name="keepOnAbort">
    /// Leaves a rejected item to the caller rather than recycling it, for a producer that means to
    /// offer it again once whatever interrupted the wait has been dealt with.
    /// </param>
    public bool Enqueue(T item, bool keepOnAbort = false)
        => Enqueue(item, CancellationToken.None, keepOnAbort);

    public bool Enqueue(T item, CancellationToken cancellationToken, bool keepOnAbort = false)
    {
        lock (sync)
        {
            int generation = abortGeneration;
            while (!closed && !cancellationToken.IsCancellationRequested &&
                   generation == abortGeneration && IsFullLocked())
                Monitor.Wait(sync, 10);

            if (closed || cancellationToken.IsCancellationRequested || generation != abortGeneration)
            {
                if (!keepOnAbort)
                    recycle(item);
                return false;
            }

            if (stampSerial)
                item.Serial = serial;
            items.Enqueue(item);
            byteCount += item.SizeInBytes;
            NotifyChangedLocked();
            return true;
        }
    }

    /// <summary>
    /// Removes and returns the head, blocking while empty. Returns false when interrupted by a
    /// flush or shutdown so the caller can re-evaluate its state and wait again.
    /// </summary>
    public bool TryDequeue(out T item)
        => TryDequeue(out item, CancellationToken.None);

    public bool TryDequeue(out T item, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            int generation = abortGeneration;
            while (!closed && !cancellationToken.IsCancellationRequested &&
                   generation == abortGeneration && items.Count == 0)
                Monitor.Wait(sync, 10);

            if (closed || cancellationToken.IsCancellationRequested || items.Count == 0)
            {
                item = null!;
                return false;
            }

            item = items.Dequeue();
            byteCount -= item.SizeInBytes;
            NotifyChangedLocked();
            return true;
        }
    }

    /// <summary>
    /// Removes and returns the head without ever blocking. Used by the audio pull callback, which
    /// may run on a device's realtime thread where waiting is not allowed.
    /// </summary>
    public bool TryDequeueNow(out T item)
    {
        lock (sync)
        {
            if (closed || items.Count == 0)
            {
                item = null!;
                return false;
            }

            item = items.Dequeue();
            byteCount -= item.SizeInBytes;
            NotifyChangedLocked();
            return true;
        }
    }

    /// <summary>
    /// Returns the head without removing it, blocking while empty when <paramref name="wait"/> is
    /// set. The video scheduler peeks so it can decide to wait for the frame's presentation time
    /// without taking ownership of it. The item is retained for the caller, which must pass it back
    /// to <see cref="DropHead"/> or release it itself.
    /// </summary>
    public bool TryPeek(out T item, bool wait)
    {
        if (retain is null)
            throw new NotSupportedException("This queue does not support retained peeks.");

        lock (sync)
        {
            int generation = abortGeneration;
            while (wait && !closed && generation == abortGeneration && items.Count == 0)
                Monitor.Wait(sync, 10);

            if (closed || items.Count == 0)
            {
                item = null!;
                return false;
            }

            item = items.Peek();
            retain(item);
            return true;
        }
    }

    /// <summary>
    /// Removes <paramref name="expected"/> from the head and releases both the queue's reference
    /// and the one taken by <see cref="TryPeek"/>. If a flush already removed it, only the peek
    /// reference is released.
    /// </summary>
    public void DropHead(T expected)
    {
        if (retain is null)
            throw new NotSupportedException("This queue does not support retained peeks.");

        bool removed = false;
        lock (sync)
        {
            if (items.Count > 0 && ReferenceEquals(items.Peek(), expected))
            {
                items.Dequeue();
                byteCount -= expected.SizeInBytes;
                removed = true;
                NotifyChangedLocked();
            }
        }

        if (removed)
            recycle(expected);

        // Balance the reference TryPeek took.
        recycle(expected);
    }

    /// <summary>
    /// Drops everything, starts a new run and releases anybody blocked on the queue.
    /// Returns the new serial.
    /// </summary>
    public int Flush() => FlushCore(null);

    /// <summary>Drops everything and starts the run numbered <paramref name="newSerial"/>.</summary>
    public int Flush(int newSerial) => FlushCore(newSerial);

    private int FlushCore(int? requestedSerial)
    {
        List<T> drained = new();
        int newSerial;
        lock (sync)
        {
            while (items.Count > 0)
                drained.Add(items.Dequeue());
            byteCount = 0;
            newSerial = serial = requestedSerial ?? serial + 1;
            abortGeneration++;
            NotifyChangedLocked();
        }

        foreach (var item in drained)
            recycle(item);
        return newSerial;
    }

    /// <summary>Whether anything queued satisfies <paramref name="match"/>.</summary>
    public bool Any(Func<T, bool> match)
    {
        lock (sync)
        {
            foreach (var item in items)
            {
                if (match(item))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Cuts back to the last item <paramref name="anchor"/> accepts, dropping what is in front of it
    /// and starting a new run for the rest, so the stage reading this queue resets around the
    /// discontinuity instead of being handed a run it cannot begin decoding. Nothing is touched
    /// unless something after the anchor satisfies <paramref name="beyond"/>, which is how a caller
    /// states that the queue really does reach past the point it is cutting to.
    /// </summary>
    /// <remarks>
    /// This is what lets a seek into material already fetched cost nothing: the queue is repositioned
    /// where <see cref="Flush"/> would have emptied it and made the source fetch it all again.
    /// </remarks>
    /// <returns>Whether the queue was cut.</returns>
    public bool CutTo(Func<T, bool> anchor, Func<T, bool> beyond) => CutToCore(anchor, beyond, null);

    /// <summary>Like <see cref="CutTo(Func{T, bool}, Func{T, bool})"/>, starting the run numbered <paramref name="newSerial"/>.</summary>
    public bool CutTo(Func<T, bool> anchor, Func<T, bool> beyond, int newSerial) => CutToCore(anchor, beyond, newSerial);

    private bool CutToCore(Func<T, bool> anchor, Func<T, bool> beyond, int? requestedSerial)
    {
        List<T> dropped = new();
        lock (sync)
        {
            if (closed)
                return false;

            int anchorIndex = -1;
            int beyondIndex = -1;
            int index = 0;
            foreach (T item in items)
            {
                if (anchor(item))
                    anchorIndex = index;
                if (beyond(item))
                    beyondIndex = index;
                index++;
            }

            if (anchorIndex < 0 || beyondIndex <= anchorIndex)
                return false;

            for (int i = 0; i < anchorIndex; i++)
            {
                T item = items.Dequeue();
                byteCount -= item.SizeInBytes;
                dropped.Add(item);
            }

            RestampLocked(requestedSerial ?? serial + 1);
        }

        foreach (var item in dropped)
            recycle(item);
        return true;
    }

    /// <summary>
    /// Starts a new run over what is already queued, without discarding any of it. Pairs with
    /// <see cref="CutTo"/> on the other queue of a stream, whose serials have to move together.
    /// </summary>
    public int Restamp()
    {
        lock (sync)
            return RestampLocked(serial + 1);
    }

    /// <summary>Starts the run numbered <paramref name="newSerial"/> over what is already queued.</summary>
    public int Restamp(int newSerial)
    {
        lock (sync)
            return RestampLocked(newSerial);
    }

    /// <summary>Wakes blocked producers and consumers without discarding anything.</summary>
    public void Abort()
    {
        lock (sync)
        {
            abortGeneration++;
            NotifyChangedLocked();
        }
    }

    public void Close()
    {
        List<T> drained = new();
        lock (sync)
        {
            closed = true;
            while (items.Count > 0)
                drained.Add(items.Dequeue());
            byteCount = 0;
            NotifyChangedLocked();
        }

        foreach (var item in drained)
            recycle(item);
    }

    private bool IsFullLocked() =>
        items.Count >= capacity || (byteCapacity > 0 && byteCount >= byteCapacity);

    private int RestampLocked(int newSerial)
    {
        serial = newSerial;
        if (stampSerial)
        {
            foreach (var item in items)
                item.Serial = newSerial;
        }

        NotifyChangedLocked();
        return newSerial;
    }

    private void NotifyChangedLocked()
    {
        version++;
        TaskCompletionSource? previous = changed;
        changed = null;
        previous?.TrySetResult();
        Monitor.PulseAll(sync);
    }
}
