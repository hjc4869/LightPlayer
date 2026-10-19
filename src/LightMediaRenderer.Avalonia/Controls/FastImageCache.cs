using System;
using System.Collections.Generic;

namespace LightMediaRenderer.Avalonia.Controls;

public sealed class FastImageCache(long budgetInBytes = FastImageCache.DefaultBudgetInBytes) : IDisposable
{
    public const long DefaultBudgetInBytes = 128L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<object, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> order = new();
    private long size;
    private bool disposed;

    public long BudgetInBytes { get; } = budgetInBytes > 0
        ? budgetInBytes
        : throw new ArgumentOutOfRangeException(nameof(budgetInBytes));

    public long SizeInBytes
    {
        get
        {
            lock (gate)
                return size;
        }
    }

    public bool TryGet(object key, int pixelSize, out FastImageSource? image)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelSize);
        lock (gate)
        {
            image = null;
            if (disposed || !entries.TryGetValue(key, out var node))
                return false;

            if (node.Value.Content.IsLost || node.Value.PixelSize < pixelSize)
            {
                Remove(node);
                return false;
            }

            if (node.Value.Content.TryAddRef() is not { } reference)
            {
                Remove(node);
                return false;
            }

            order.Remove(node);
            order.AddFirst(node);
            image = new FastImageSource(reference);
            return true;
        }
    }

    public void Set(object key, FastImageSource image, int pixelSize)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelSize);
        lock (gate)
        {
            if (disposed || image.Content is not { IsLost: false } content || content.SizeInBytes > BudgetInBytes)
                return;

            int availableSize = Math.Max(pixelSize, Math.Min(content.PixelSize.Width, content.PixelSize.Height));
            if (entries.TryGetValue(key, out var existing))
            {
                if (!existing.Value.Content.IsLost && existing.Value.PixelSize >= availableSize)
                    return;
                Remove(existing);
            }

            if (content.TryAddRef() is not { } reference)
                return;

            while (size > BudgetInBytes - content.SizeInBytes && order.Last is { } last)
                Remove(last);

            entries.Add(key, order.AddFirst(new Entry(key, reference, availableSize)));
            size += content.SizeInBytes;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            while (order.Last is { } last)
                Remove(last);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Clear();
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        order.Remove(node);
        entries.Remove(node.Value.Key);
        size -= node.Value.Content.SizeInBytes;
        node.Value.Content.Release();
    }

    private readonly record struct Entry(object Key, FastImageContent Content, int PixelSize);
}