using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LightStudio.FfmpegShim.Engine;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// One media worker as seen from the page: commands go out as JSON calls, and the frames, status and
/// notifications the worker produces come back in batches routed to the components they belong to.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class MediaWorkerConnection
{
    internal const string ModuleName = "BrowserMediaWorker";
    private static readonly Dictionary<int, MediaWorkerConnection> connections = [];
    private static Task? initialization;
    private static Task<MediaWorkerConnection>? shared;
    private static int nextPayload;
    private readonly Dictionary<int, IWorkerComponent> components = [];
    private readonly List<WireAllowance> allowances = [];
    private readonly List<WirePosition> positions = [];
    private readonly List<WireSpeed> speeds = [];
    private bool updatesScheduled;

    private MediaWorkerConnection(int id) => Id = id;

    public int Id { get; }

    /// <summary>Set once the worker stopped; every later command fails with it.</summary>
    public Exception? Failure { get; private set; }

    internal static Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    private static async Task InitializeCoreAsync()
    {
        await BrowserAssets.ImportAsync(ModuleName, "media/media-worker-host.js");
        await Initialize(typeof(MediaWorkerConnection).Assembly.GetName().Name!);
    }

    internal static Task<MediaWorkerConnection> GetAsync()
    {
        if (shared is { IsCompleted: false } || shared is { IsCompletedSuccessfully: true, Result.Failure: null })
            return shared;
        return shared = StartAsync();
    }

    private static async Task<MediaWorkerConnection> StartAsync()
    {
        await InitializeAsync();
        var connection = new MediaWorkerConnection(StartWorker((int)AppLogLevel.Information));
        connections.Add(connection.Id, connection);
        return connection;
    }

    internal void Register(int component, IWorkerComponent target) => components[component] = target;

    internal void Unregister(int component) => components.Remove(component);

    internal async Task<TResult> CallAsync<TArguments, TResult>(
        string method,
        TArguments arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
            throw new InvalidOperationException("The media worker stopped.", Failure);
        cancellationToken.ThrowIfCancellationRequested();
        string json = await Call(Id, method, JsonSerializer.Serialize(arguments, argumentType));
        return JsonSerializer.Deserialize(json, resultType)
            ?? throw new InvalidDataException($"The media worker returned no result for '{method}'.");
    }

    internal Task<string> CallRawAsync(string method, string arguments) =>
        Failure is not null ? Task.FromException<string>(new InvalidOperationException("The media worker stopped.", Failure))
            : Call(Id, method, arguments);

    /// <summary>
    /// Runs a command that returns decoded frames besides its JSON. The frames are retained for the
    /// renderer and belong to the caller, which releases them like any imported frame.
    /// </summary>
    internal async Task<(TResult Value, BrowserFrameInfo[] Frames)> CallWithFramesAsync<TArguments, TResult>(
        string method,
        TArguments arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType)
    {
        if (Failure is not null)
            throw new InvalidOperationException("The media worker stopped.", Failure);
        string json = await Call(Id, method, JsonSerializer.Serialize(arguments, argumentType));
        using JsonDocument document = JsonDocument.Parse(json);
        BrowserFrameInfo[] frames = document.RootElement.GetProperty("frames").Deserialize(WorkerBatchJsonContext.Default.BrowserFrameInfoArray)
            ?? [];
        try
        {
            TResult value = document.RootElement.GetProperty("value").Deserialize(resultType)
                ?? throw new InvalidDataException($"The media worker returned no result for '{method}'.");
            return (value, frames);
        }
        catch
        {
            foreach (BrowserFrameInfo frame in frames)
                DropFrame(frame);
            throw;
        }
    }

    /// <summary>Runs a command whose result may carry bytes besides its JSON, such as a decoded picture.</summary>
    internal Task<(TResult? Value, byte[]? Payload)> CallWithPayloadAsync<TArguments, TResult>(
        string method,
        TArguments arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType)
        where TResult : class =>
        CallWithPayloadAsync(method, arguments, argumentType, resultType, static (token, length) =>
        {
            byte[] payload = new byte[length];
            TakePayload(token, payload);
            return payload;
        });

    /// <summary>
    /// Like <see cref="CallWithPayloadAsync{TArguments, TResult}"/>, with the bytes copied into native memory that
    /// belongs to the caller, so large pictures do not pass through the managed heap.
    /// </summary>
    internal Task<(TResult? Value, NativePayload? Payload)> CallWithNativePayloadAsync<TArguments, TResult>(
        string method,
        TArguments arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType)
        where TResult : class =>
        CallWithPayloadAsync(method, arguments, argumentType, resultType, static (token, length) =>
        {
            var payload = new NativePayload(length);
            try
            {
                TakePayload(token, payload.Span);
                return payload;
            }
            catch
            {
                payload.Dispose();
                throw;
            }
        });

    private async Task<(TResult? Value, TPayload? Payload)> CallWithPayloadAsync<TArguments, TResult, TPayload>(
        string method,
        TArguments arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType,
        Func<int, int, TPayload> take)
        where TResult : class
        where TPayload : class
    {
        if (Failure is not null)
            throw new InvalidOperationException("The media worker stopped.", Failure);
        int token = nextPayload = nextPayload == int.MaxValue ? 1 : nextPayload + 1;
        try
        {
            string json = await CallWithPayload(Id, token, method, JsonSerializer.Serialize(arguments, argumentType));
            TResult? value = JsonSerializer.Deserialize(json, resultType);
            int length = PayloadLength(token);
            return length < 0 ? (value, null) : (value, take(token, length));
        }
        finally
        {
            ReleasePayload(token);
        }
    }

    internal void PostAllowance(int decoder, long allowance)
    {
        allowances.Add(new WireAllowance(decoder, allowance));
        ScheduleUpdates();
    }

    internal void PostPosition(int demuxer, double seconds)
    {
        positions.RemoveAll(position => position.Demuxer == demuxer);
        positions.Add(new WirePosition(demuxer, seconds));
        ScheduleUpdates();
    }

    internal void PostSpeed(int decoder, double speed)
    {
        speeds.RemoveAll(entry => entry.Decoder == decoder);
        speeds.Add(new WireSpeed(decoder, speed));
        ScheduleUpdates();
    }

    /// <summary>Sends packets read on the page to a relayed demuxer, tagged with the generation they belong to.</summary>
    internal void PostRelay(int demuxer, int generation, Span<byte> batch)
    {
        if (Failure is null)
            PostRelay(Id, demuxer, generation, batch);
    }

    /// <summary>Registers a source the worker will read and returns its exact length.</summary>
    internal Task<double> RegisterSourceAsync(int source, string kind, string path, double length) =>
        RegisterSource(Id, source, kind, path, length);

    /// <summary>Registers bytes the worker will read from its own copy of them.</summary>
    internal double RegisterBytes(int source, ReadOnlyMemory<byte> bytes)
    {
        if (Failure is not null)
            throw new InvalidOperationException("The media worker stopped.", Failure);
        return RegisterBytes(Id, source, MemoryMarshal.AsMemory(bytes).Span);
    }

    internal static void CancelSourceReads(int source, bool canceled) => CancelSource(source, canceled);

    internal static void ForgetSourceRegistration(int source) => ForgetSource(source);

    internal void CancelPhotoTiles(int session, bool gain, int operation)
    {
        if (Failure is null) CancelPhotoTiles(Id, session, gain, operation);
    }

    private void ScheduleUpdates()
    {
        if (updatesScheduled)
            return;
        updatesScheduled = true;
        _ = FlushUpdatesAsync();
    }

    private async Task FlushUpdatesAsync()
    {
        // Coalesces everything posted in one turn of the page into one message.
        await Task.Yield();
        updatesScheduled = false;
        if (Failure is not null)
            return;
        var updates = new WirePumpUpdates
        {
            Allowances = allowances.ToArray(),
            Positions = positions.ToArray(),
            Speeds = speeds.ToArray(),
        };
        allowances.Clear();
        positions.Clear();
        speeds.Clear();
        PostUpdates(Id, JsonSerializer.Serialize(updates, EngineWireContext.Default.WirePumpUpdates));
    }

    [JSExport]
    internal static void OnBatch(int worker, string json)
    {
        if (!connections.TryGetValue(worker, out MediaWorkerConnection? connection))
            return;
        WorkerBatch batch;
        try
        {
            batch = JsonSerializer.Deserialize(json, WorkerBatchJsonContext.Default.WorkerBatch)
                ?? throw new InvalidDataException("The media worker sent an empty batch.");
        }
        catch (Exception exception)
        {
            AppLog.Error("Web.MediaWorker", "Could not read a media worker batch.", exception);
            return;
        }

        connection.Deliver(batch);
    }

    [JSExport]
    internal static void OnWorkerFailed(int worker, string message)
    {
        if (!connections.Remove(worker, out MediaWorkerConnection? connection))
            return;
        connection.Failure = new InvalidOperationException(message);
        AppLog.Error("Web.MediaWorker", $"The media worker stopped: {message}");
        foreach (IWorkerComponent component in connection.components.Values.ToArray())
            component.OnWorkerFailed(connection.Failure);
    }

    private void Deliver(WorkerBatch batch)
    {
        foreach (BrowserFrameInfo frame in batch.Frames)
        {
            if (components.TryGetValue(frame.Output, out IWorkerComponent? component))
                component.OnFrame(frame);
            else
                DropFrame(frame);
        }

        foreach (WireDemuxerStatus status in batch.Demuxers)
        {
            if (components.TryGetValue(status.Demuxer, out IWorkerComponent? component))
                component.OnStatus(status.Status);
        }

        foreach (WireDecoderStatus status in batch.Decoders)
        {
            if (components.TryGetValue(status.Decoder, out IWorkerComponent? component))
                component.OnDiagnostics(status.Diagnostics);
        }

        foreach (WireRelayStatus status in batch.Relays ?? [])
        {
            if (components.TryGetValue(status.Demuxer, out IWorkerComponent? component))
                component.OnRelayStatus(status.Consumed);
        }

        foreach (WireNotification notification in batch.Notifications)
        {
            if (components.TryGetValue(notification.Component, out IWorkerComponent? component))
                component.OnNotification(notification);
        }
    }

    /// <summary>Lets go of a frame nobody is waiting for.</summary>
    internal static void DropFrame(BrowserFrameInfo frame)
    {
        if (frame.Resource != 0)
            _ = BrowserVideoPlanes.ReleaseAsync(frame.Resource);
        if (frame.Enhancement?.Resource > 0)
            _ = BrowserVideoPlanes.ReleaseAsync(frame.Enhancement.Resource);
    }

    /// <summary>
    /// Copies a sound frame of the batch being delivered out of the worker's memory. Only valid while
    /// <see cref="OnBatch"/> runs, before the worker is told to reuse the frame.
    /// </summary>
    internal void CopyAudio(BrowserFrameInfo frame, Span<byte> destination) => CopyAudio(Id, frame.Address, frame.Length, destination);

    [JSImport("initialize", ModuleName)]
    private static partial Task Initialize(string assembly);

    [JSImport("startWorker", ModuleName)]
    private static partial int StartWorker(int appLogLevel);

    [JSImport("call", ModuleName)]
    private static partial Task<string> Call(int worker, string method, string arguments);

    [JSImport("cancelPhotoTiles", ModuleName)]
    private static partial void CancelPhotoTiles(int worker, int session, bool gain, int operation);

    [JSImport("callWithPayload", ModuleName)]
    private static partial Task<string> CallWithPayload(int worker, int token, string method, string arguments);

    [JSImport("payloadLength", ModuleName)]
    private static partial int PayloadLength(int token);

    [JSImport("takePayload", ModuleName)]
    private static partial void TakePayload(int token, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("releasePayload", ModuleName)]
    private static partial void ReleasePayload(int token);

    [JSImport("postUpdates", ModuleName)]
    private static partial void PostUpdates(int worker, string json);

    [JSImport("postRelay", ModuleName)]
    private static partial void PostRelay(int worker, int demuxer, int generation, [JSMarshalAs<JSType.MemoryView>] Span<byte> batch);

    [JSImport("registerSource", ModuleName)]
    private static partial Task<double> RegisterSource(int worker, int source, string kind, string path, double length);

    [JSImport("registerBytes", ModuleName)]
    private static partial double RegisterBytes(int worker, int source, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("cancelSource", ModuleName)]
    private static partial void CancelSource(int source, bool canceled);

    [JSImport("forgetSource", ModuleName)]
    private static partial void ForgetSource(int source);

    [JSImport("copyAudio", ModuleName)]
    private static partial void CopyAudio(int worker, double address, int length, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
}

/// <summary>Bytes a worker result carried, in native memory that is freed on disposal.</summary>
internal sealed unsafe class NativePayload : IDisposable
{
    private void* address;

    public NativePayload(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        address = NativeMemory.Alloc((nuint)Math.Max(1, length));
    }

    public int Length { get; }

    public IntPtr Address => address is null ? throw new ObjectDisposedException(nameof(NativePayload)) : (IntPtr)address;

    public Span<byte> Span => new((void*)Address, Length);

    public void Dispose()
    {
        if (address is null)
            return;
        NativeMemory.Free(address);
        address = null;
    }
}

/// <summary>A page-side proxy for something the worker runs.</summary>
internal interface IWorkerComponent
{
    void OnFrame(BrowserFrameInfo frame) => MediaWorkerConnection.DropFrame(frame);

    void OnStatus(DemuxerStatus status)
    {
    }

    void OnDiagnostics(LightStudio.FfmpegShim.Decoding.VideoDecoderDiagnostics diagnostics)
    {
    }

    /// <summary>The worker has taken this many bytes of relayed batches in total.</summary>
    void OnRelayStatus(long consumed)
    {
    }

    void OnNotification(WireNotification notification);

    void OnWorkerFailed(Exception failure);
}

internal sealed record WorkerBatch(
    BrowserFrameInfo[] Frames,
    WireDemuxerStatus[] Demuxers,
    WireDecoderStatus[] Decoders,
    WireNotification[] Notifications,
    WireRelayStatus[]? Relays);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(WorkerBatch))]
internal sealed partial class WorkerBatchJsonContext : JsonSerializerContext;
