using System.Threading;
using System.Threading.Tasks;

namespace LightMediaRenderer.Avalonia.Threading;

internal sealed class AsyncSignal
{
    private readonly object gate = new();
    private TaskCompletionSource? changed;
    private int version;

    public int Version => Volatile.Read(ref version);

    public void Pulse()
    {
        lock (gate)
        {
            version++;
            TaskCompletionSource? previous = changed;
            changed = null;
            previous?.TrySetResult();
        }
    }

    public Task WaitAsync(int observedVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (version != observedVersion)
                return Task.CompletedTask;
            changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return changed.Task.WaitAsync(cancellationToken);
        }
    }
}