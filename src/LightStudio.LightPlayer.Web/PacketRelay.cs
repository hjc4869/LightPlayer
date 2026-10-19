using System.Runtime.Versioning;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Reads a packet source on the page and relays its packets to their demuxer in a media worker. Batches stay
/// within a byte window the worker reopens as it consumes them. When the worker moves the source it starts a
/// new generation, and this side moves its source to match before relaying again.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class PacketRelay : IAsyncDisposable
{
    private const long WindowBytes = 8 * 1024 * 1024;
    private const int BatchBytes = 512 * 1024;
    private const int TurnMilliseconds = 4;

    private readonly MediaWorkerConnection worker;
    private readonly int demuxer;
    private readonly PacketRelayWriter batch = new();
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource? wake;
    private Task? loop;
    private long sent;
    private long consumed;
    private bool started;

    // Set after the source ended or failed; only a move of the source has anything new to read.
    private bool idle;
    private int disposed;

    public PacketRelay(MediaWorkerConnection worker, int demuxer, IRelayablePacketSource source)
    {
        this.worker = worker;
        this.demuxer = demuxer;
        Source = source;
    }

    public IRelayablePacketSource Source { get; }

    public int Generation { get; private set; }

    public void Start()
    {
        started = true;
        loop ??= RunAsync(lifetime.Token);
        Wake();
    }

    /// <summary>Moves the source where the worker moved its demuxer, starting <paramref name="generation"/>.</summary>
    public void Reposition(double position, int generation)
    {
        Generation = generation;
        batch.Reset();
        idle = false;
        Source.Seek(position);
        Wake();
    }

    public void OnConsumed(long total)
    {
        if (total <= consumed)
            return;
        consumed = total;
        Wake();
    }

    public void Stop()
    {
        lifetime.Cancel();
        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Stop();
        if (loop is not null)
            await loop;
        if (Source is IAsyncDisposable asynchronous)
            await asynchronous.DisposeAsync();
        else
            Source.Dispose();
        lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task next = Fill(cancellationToken);
                Flush();
                await next;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Web.MediaWorker", "The packet relay stopped.", exception);
        }
    }

    /// <summary>Reads into the batch until the window is full, the source has nothing yet or the turn is spent.</summary>
    /// <returns>What to wait for before reading again.</returns>
    private Task Fill(CancellationToken cancellationToken)
    {
        long turn = Environment.TickCount64;
        while (true)
        {
            if (!started || idle || sent + batch.Length - consumed >= WindowBytes)
                return Wait();

            PacketReadStatus status;
            try
            {
                status = Source.Read(batch);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Web.MediaWorker", "The relayed packet source failed.", exception);
                batch.WriteFault(exception.Message);
                idle = true;
                continue;
            }

            switch (status)
            {
                case PacketReadStatus.WouldBlock:
                    return Task.WhenAny(Source.WaitForPacketsAsync(), Wait());

                case PacketReadStatus.EndOfFile:
                    batch.WriteEnd();
                    idle = true;
                    break;

                case PacketReadStatus.Reconfigured:
                    batch.WriteReconfigure(Source.ReconfigurationPositionSeconds, WireRelaySource.Describe(Source));
                    break;

                case PacketReadStatus.Fatal:
                    batch.WriteFault("The media source failed while reading.");
                    idle = true;
                    break;
            }

            if (batch.Length >= BatchBytes)
                Flush();

            // A real event-loop turn, so a deep backlog cannot hold up rendering and input.
            if (Environment.TickCount64 - turn >= TurnMilliseconds)
                return Task.Delay(1, cancellationToken);
        }
    }

    private void Flush()
    {
        if (batch.Length == 0)
            return;
        sent += batch.Length;
        worker.PostRelay(demuxer, Generation, batch.Written);
        batch.Reset();
    }

    private Task Wait() => (wake ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void Wake()
    {
        TaskCompletionSource? waiting = wake;
        wake = null;
        waiting?.TrySetResult();
    }
}
