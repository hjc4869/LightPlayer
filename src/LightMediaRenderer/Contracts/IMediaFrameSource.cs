using System.Threading;
using System.Threading.Tasks;

namespace LightMediaRenderer.Contracts;

public interface IMediaFrameSource<T> where T : class, IMediaQueueItem
{
    int Count { get; }

    bool IsFull { get; }

    int Version { get; }

    /// <summary>Transfers one queued frame to the caller without waiting for data.</summary>
    bool TryRead(out T frame);

    /// <summary>Retains the head for the caller, which must release it or pass it to DropHead.</summary>
    /// <exception cref="System.NotSupportedException">The source does not support retained peeks.</exception>
    bool TryPeek(out T frame);

    /// <summary>Releases the peek reference and, if still queued, removes and releases the head.</summary>
    /// <exception cref="System.NotSupportedException">The source does not support retained peeks.</exception>
    void DropHead(T frame);

    bool ContainsEndOfStream(int serial);

    /// <summary>Waits for a mutation after the version captured before inspecting the queue.</summary>
    Task WaitForChangeAsync(int observedVersion, CancellationToken cancellationToken);
}