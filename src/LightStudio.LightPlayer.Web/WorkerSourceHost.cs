using System.Buffers;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// App streams the media worker reads through the source bridge. Each window request is served by an
/// async read into a reused buffer, which the page script copies straight into the worker's memory.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WorkerSourceHost
{
    internal const string ModuleName = MediaWorkerConnection.ModuleName;
    private const int YieldIntervalMilliseconds = 4;

    private static readonly Dictionary<int, Source> sources = [];
    private static int nextSource;

    /// <summary>Returns an id no other worker source uses.</summary>
    public static int AllocateId() => ++nextSource;

    /// <summary>Takes ownership of a seekable stream with an exact length under <paramref name="id"/>.</summary>
    public static void Register(int id, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length > 9007199254740991L)
            throw new NotSupportedException("The media worker requires a seekable source with an exact byte length.");

        sources.Add(id, new Source(stream));
    }

    /// <summary>Fails the worker's current and future waits on the source until it is cleared again.</summary>
    public static void SetCanceled(int id, bool canceled) => CancelSource(id, canceled);

    /// <summary>Cancels reads in flight and disposes the stream once no read is using it.</summary>
    public static async Task ReleaseAsync(int id)
    {
        if (!sources.Remove(id, out Source? source))
            return;

        source.Cancellation.Cancel();
        await source.Gate.WaitAsync();
        try
        {
            await source.Stream.DisposeAsync();
        }
        finally
        {
            source.Gate.Release();
            source.Cancellation.Dispose();
            if (source.Buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(source.Buffer);
        }
    }

    [JSExport]
    internal static async Task ReadSourceAsync(int id, int request, double position, int length)
    {
        if (!sources.TryGetValue(id, out Source? source))
        {
            FailSourceRead(request, $"Source {id} is closed.");
            return;
        }

        try
        {
            if (!double.IsFinite(position) || position < 0 || position != Math.Truncate(position) || length <= 0)
                throw new InvalidDataException($"Invalid source window {position}+{length}.");

            await source.Gate.WaitAsync(source.Cancellation.Token);
            try
            {
                int count = await FillAsync(source, (long)position, length, source.Cancellation.Token);
                CompleteSourceRead(request, source.Buffer.AsSpan(0, count));
            }
            finally
            {
                source.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (source.Cancellation.IsCancellationRequested)
        {
            FailSourceRead(request, "The source was closed.");
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.MediaWorker", $"Source {id} failed to read {length} bytes at {position}.", exception);
            FailSourceRead(request, exception.Message);
        }
    }

    private static async Task<int> FillAsync(Source source, long position, int length, CancellationToken cancellationToken)
    {
        if (source.Buffer.Length < length)
        {
            byte[] previous = source.Buffer;
            source.Buffer = ArrayPool<byte>.Shared.Rent(length);
            if (previous.Length > 0)
                ArrayPool<byte>.Shared.Return(previous);
        }

        if (source.Stream.Position != position)
            source.Stream.Seek(position, SeekOrigin.Begin);

        int count = 0;
        long started = Environment.TickCount64;
        while (count < length)
        {
            int read = await source.Stream.ReadAsync(source.Buffer.AsMemory(count, length - count), cancellationToken);
            if (read == 0)
                break;

            count += read;
            if (count < length && Environment.TickCount64 - started >= YieldIntervalMilliseconds)
            {
                // A stream that completes synchronously would otherwise hold the UI thread for the whole window.
                await Task.Delay(1, cancellationToken);
                started = Environment.TickCount64;
            }
        }

        return count;
    }

    [JSImport("completeSourceRead", ModuleName)]
    private static partial void CompleteSourceRead(int request, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("failSourceRead", ModuleName)]
    private static partial void FailSourceRead(int request, string message);

    [JSImport("cancelSource", ModuleName)]
    private static partial void CancelSource(int id, bool canceled);

    private sealed class Source(Stream stream)
    {
        public Stream Stream { get; } = stream;

        public byte[] Buffer { get; set; } = [];

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
