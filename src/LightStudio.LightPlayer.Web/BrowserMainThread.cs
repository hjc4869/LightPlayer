using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Marshals JavaScript calls onto the thread that imported the modules.
/// </summary>
/// <remarks>
/// Module bindings belong to the importing thread. Other managed threads post through its captured
/// context; without one, the threaded runtime dispatches their calls to the JavaScript thread itself.
/// A single-threaded runtime can call directly without a startup synchronization context.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class BrowserMainThread
{
    private static SynchronizationContext? context;
    private static int ownerThread;

    /// <summary>Records the calling thread as the one that owns the JavaScript bindings.</summary>
    public static void Capture()
    {
        context = SynchronizationContext.Current;
        ownerThread = Environment.CurrentManagedThreadId;
    }

    public static bool IsAvailable => ownerThread != 0;

    /// <summary>Whether the calling thread owns the JavaScript bindings; always true on a single-threaded runtime.</summary>
    public static bool IsOwnerThread => ownerThread != 0 && Environment.CurrentManagedThreadId == ownerThread;

    public static Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        if (ownerThread != 0 && Environment.CurrentManagedThreadId == ownerThread)
            return operation();
        var target = context;
        if (target is null)
        {
            return ownerThread != 0
                ? operation()
                : Task.FromException<T>(new InvalidOperationException("Browser interop is not initialized."));
        }

        if (SynchronizationContext.Current == target)
        {
            return operation();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.Post(_ => _ = ForwardAsync(operation, completion), null);
        return completion.Task;
    }

    public static Task InvokeAsync(Func<Task> operation) =>
        InvokeAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        });

    private static async Task ForwardAsync<T>(Func<Task<T>> operation, TaskCompletionSource<T> completion)
    {
        try
        {
            completion.SetResult(await operation().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}
