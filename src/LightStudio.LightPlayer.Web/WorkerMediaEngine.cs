using System.Runtime.Versioning;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>A file the user granted this page, read by the worker without passing through the page.</summary>
public sealed record BrowserFileMediaInput(string Path) : MediaInput(Path);

/// <summary>An HTTP resource the worker reads in ranges itself; relative to the page when not absolute.</summary>
public sealed record UrlMediaInput(string Url) : MediaInput(Url);

/// <summary>
/// The engine in a media worker. Demuxers and decoders live in the worker and link there by reference;
/// the page holds proxies that send commands and receive frames in batches against credits.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WorkerMediaEngine : IMediaEngine
{
    /// <summary>The engine of the worker that runs playback.</summary>
    public static WorkerMediaEngine Main { get; } = new();

    public string Name => "media worker";

    public async ValueTask<IMediaDemuxer> OpenDemuxerAsync(MediaInput input, CancellationToken cancellationToken = default)
    {
        MediaWorkerConnection worker = await MediaWorkerConnection.GetAsync();
        if (input is AsyncPacketSourceMediaInput packets)
            return await OpenRelayAsync(worker, packets, cancellationToken);

        WorkerSource source = await WorkerSource.RegisterAsync(worker, input, readAhead: true, cancellationToken);
        try
        {
            WireOpenResult result;
            using (cancellationToken.Register(source.Cancel))
                result = await worker.CallAsync("open", source.Wire, EngineWireContext.Default.WireSource, EngineWireContext.Default.WireOpenResult);
            var demuxer = new WorkerDemuxer(worker, result, source);
            if (cancellationToken.IsCancellationRequested)
            {
                await demuxer.DisposeAsync();
                throw new OperationCanceledException(cancellationToken);
            }

            return demuxer;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !source.Owned)
        {
            await source.DisposeAsync();
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Opening the media was canceled.", exception, cancellationToken);
            throw;
        }
    }

    public async ValueTask<IVideoDecoderStage> CreateVideoDecoderAsync(
        IMediaDemuxer demuxer,
        VideoDecoderStageOptions options,
        CancellationToken cancellationToken = default)
    {
        WorkerDemuxer owner = Own(demuxer);
        VideoDecoderOptions decoder = options.Decoder;
        var request = new WireVideoDecoderRequest(owner.Id, decoder.ThreadCount, decoder.EnableHardwareDecoding,
            decoder.SoftwareFallbackReason ?? string.Empty, decoder.FramePoolSize, decoder.FramePoolByteCapacity,
            options.PacketCapacity, options.PacketByteCapacity)
        {
            RequireWebCodecs = decoder.EnableHardwareDecoding &&
                string.Equals(decoder.HardwareDeviceType, WebCodecsDeviceType, StringComparison.OrdinalIgnoreCase) &&
                RequireWebCodecs,
        };
        WireDecoderResult result = await owner.Worker.CallAsync("createVideoDecoder", request,
            EngineWireContext.Default.WireVideoDecoderRequest, EngineWireContext.Default.WireDecoderResult, cancellationToken);
        return new WorkerVideoDecoderStage(owner, result, options);
    }

    public async ValueTask<IAudioDecoderStage> CreateAudioDecoderAsync(
        IMediaDemuxer demuxer,
        int streamIndex,
        AudioDecoderStageOptions options,
        CancellationToken cancellationToken = default)
    {
        WorkerDemuxer owner = Own(demuxer);
        WireDecoderResult result = await owner.Worker.CallAsync("createAudioDecoder",
            new WireAudioDecoderRequest(owner.Id, streamIndex, options.Format, options.Speed, options.PacketCapacity, options.PacketByteCapacity),
            EngineWireContext.Default.WireAudioDecoderRequest, EngineWireContext.Default.WireDecoderResult, cancellationToken);
        return new WorkerAudioDecoderStage(owner, result);
    }

    public async ValueTask<string> ReadSubtitleScriptAsync(MediaInput input, int streamIndex, CancellationToken cancellationToken = default) =>
        await BrowserMediaOperations.ReadSubtitleScriptAsync(input, streamIndex, cancellationToken);

    public async ValueTask<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        MediaInput input,
        int streamIndex,
        bool preserveContainerTimestamps,
        CancellationToken cancellationToken = default) =>
        await BrowserMediaOperations.ReadBitmapSubtitlesAsync(input, streamIndex, preserveContainerTimestamps, cancellationToken);

    public async ValueTask<BitmapSubtitleDocument> DecodeBitmapSubtitlesAsync(
        BitmapSubtitlePackets packets,
        CancellationToken cancellationToken = default) =>
        await BrowserMediaOperations.DecodeBitmapSubtitlesAsync(packets, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The hardware device type that selects WebCodecs.</summary>
    public const string WebCodecsDeviceType = "webcodecs";

    /// <summary>Fails instead of using FFmpeg when WebCodecs cannot decode a stream, for validation tools.</summary>
    public static bool RequireWebCodecs { get; set; }

    private static WorkerDemuxer Own(IMediaDemuxer demuxer) => demuxer as WorkerDemuxer
        ?? throw new NotSupportedException("The demuxer belongs to another engine.");

    /// <summary>Reads the source on the page and relays its packets to a demuxer in the worker.</summary>
    private static async Task<IMediaDemuxer> OpenRelayAsync(MediaWorkerConnection worker, AsyncPacketSourceMediaInput input,
        CancellationToken cancellationToken)
    {
        IPacketSource opened = await input.Open(cancellationToken)
            ?? throw new InvalidOperationException("The source could not be opened.");
        if (opened is not IRelayablePacketSource source)
        {
            await ReleaseAsync(opened);
            throw new NotSupportedException($"The media worker cannot be fed by {opened.GetType().Name}.");
        }

        WireOpenResult result;
        try
        {
            result = await worker.CallAsync("openRelay", WireRelaySource.Describe(source), PacketRelayJsonContext.Default.WireRelaySource,
                EngineWireContext.Default.WireOpenResult);
        }
        catch
        {
            await ReleaseAsync(source);
            throw;
        }

        var demuxer = new WorkerDemuxer(worker, result, new PacketRelay(worker, result.Demuxer, source));
        if (cancellationToken.IsCancellationRequested)
        {
            await demuxer.DisposeAsync();
            throw new OperationCanceledException(cancellationToken);
        }

        return demuxer;
    }

    private static async ValueTask ReleaseAsync(IPacketSource source)
    {
        if (source is IAsyncDisposable asynchronous)
            await asynchronous.DisposeAsync();
        else
            source.Dispose();
    }
}

/// <summary>A source registered with a worker, released once the worker is done with it.</summary>
[SupportedOSPlatform("browser")]
internal sealed class WorkerSource : IAsyncDisposable
{
    private readonly bool pageStream;
    private int disposed;

    private WorkerSource(int id, string name, double length, bool readAhead, bool pageStream)
    {
        Wire = new WireSource(id, name, length) { ReadAhead = readAhead };
        this.pageStream = pageStream;
    }

    public WireSource Wire { get; }

    /// <summary>Whether the source now belongs to a demuxer, which releases it.</summary>
    public bool Owned { get; set; }

    public static async Task<WorkerSource> RegisterAsync(MediaWorkerConnection worker, MediaInput input, bool readAhead,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = WorkerSourceHost.AllocateId();
        switch (input)
        {
            case BrowserFileMediaInput file:
                return new WorkerSource(id, input.Name, await worker.RegisterSourceAsync(id, "file", file.Path, 0), readAhead, false);
            case UrlMediaInput url:
                return new WorkerSource(id, input.Name, await worker.RegisterSourceAsync(id, "url", url.Url, 0), readAhead, false);
            case MemoryMediaInput memory:
                return new WorkerSource(id, input.Name, worker.RegisterBytes(id, memory.Content), readAhead, false);
            case StreamMediaInput opened:
                Stream stream = await opened.Open(cancellationToken)
                    ?? throw new InvalidOperationException("The source could not be opened.");
                try
                {
                    WorkerSourceHost.Register(id, stream);
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }

                var source = new WorkerSource(id, input.Name, stream.Length, readAhead, true);
                try
                {
                    await worker.RegisterSourceAsync(id, "stream", input.Name, stream.Length);
                }
                catch
                {
                    await source.DisposeAsync();
                    throw;
                }

                return source;
            default:
                throw new NotSupportedException($"The media worker cannot read {input.GetType().Name}.");
        }
    }

    public void Cancel() => MediaWorkerConnection.CancelSourceReads(Wire.Id, true);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        MediaWorkerConnection.ForgetSourceRegistration(Wire.Id);
        if (pageStream)
            await WorkerSourceHost.ReleaseAsync(Wire.Id);
    }
}

[SupportedOSPlatform("browser")]
internal sealed class WorkerDemuxer : IMediaDemuxer, IWorkerComponent
{
    private const double PositionInterval = 0.25;
    private readonly WorkerSource? source;
    private readonly PacketRelay? relay;
    private DemuxerStatus status = DemuxerStatus.Empty;
    private double reportedPosition = double.NaN;
    private int disposed;

    public WorkerDemuxer(MediaWorkerConnection worker, WireOpenResult opened, WorkerSource source)
        : this(worker, opened)
    {
        this.source = source;
        source.Owned = true;
    }

    /// <summary>A demuxer fed by a source the page reads, through <paramref name="relay"/>.</summary>
    public WorkerDemuxer(MediaWorkerConnection worker, WireOpenResult opened, PacketRelay relay)
        : this(worker, opened)
    {
        this.relay = relay;
    }

    private WorkerDemuxer(MediaWorkerConnection worker, WireOpenResult opened)
    {
        Worker = worker;
        Id = opened.Demuxer;
        Info = WithCaseInsensitiveTags(opened.Info);
        VideoStreamIndex = opened.VideoStream;
        AudioStreamIndex = opened.AudioStream;
        CanSeek = opened.CanSeek;
        Renditions = opened.Renditions;
        worker.Register(Id, this);
    }

    public MediaWorkerConnection Worker { get; }
    public int Id { get; }
    public MediaInfo Info { get; private set; }
    public int VideoStreamIndex { get; private set; }
    public int AudioStreamIndex { get; private set; }
    public bool CanSeek { get; }
    public IReadOnlyList<VideoRendition> Renditions { get; }

    /// <summary>The worker's queues, with a relayed source's buffering as the page's source reports it.</summary>
    public DemuxerStatus Status => relay?.Source is not { } relayed ? status : status with
    {
        IsBuffered = true,
        BufferedSeconds = relayed.BufferedSeconds,
        HistoryStartSeconds = relayed.HistoryStartSeconds,
        EstimatedBandwidthBitsPerSecond = relayed.EstimatedBandwidthBitsPerSecond,
    };

    public event EventHandler<DemuxerNotification>? Notified;

    public async ValueTask LinkAsync(int streamIndex, IMediaDecoderStage? decoder, CancellationToken cancellationToken = default)
    {
        int target = decoder switch
        {
            null => 0,
            WorkerVideoDecoderStage video when ReferenceEquals(video.Worker, Worker) => video.Id,
            WorkerAudioDecoderStage audio when ReferenceEquals(audio.Worker, Worker) => audio.Id,
            _ => throw new NotSupportedException("The decoder belongs to another engine."),
        };
        await Worker.CallAsync("link", new WireLink(Id, streamIndex, target), EngineWireContext.Default.WireLink,
            EngineWireContext.Default.Boolean, cancellationToken);

        // A relayed source asks for its sound track itself, and the seek that follows a switch moves it there.
        if (decoder is WorkerAudioDecoderStage)
        {
            AudioStreamIndex = streamIndex;
            if (relay is not null && relay.Source.AudioStreamIndex != streamIndex)
                relay.Source.SelectAudioStream(streamIndex);
        }
        else if (decoder is null && streamIndex == AudioStreamIndex)
        {
            AudioStreamIndex = -1;
            relay?.Source.SelectAudioStream(-1);
        }
    }

    public void Start()
    {
        Observe(Worker.CallAsync("start", Id, EngineWireContext.Default.Int32, EngineWireContext.Default.Boolean));
        relay?.Start();
    }

    public async ValueTask<bool> SeekAsync(SeekRequest request, CancellationToken cancellationToken = default)
    {
        WireSeekResult result = await Worker.CallAsync("seek", new WireSeek(Id, request), EngineWireContext.Default.WireSeek,
            EngineWireContext.Default.WireSeekResult, cancellationToken);
        if (result.Succeeded && relay is not null && result.Generation != relay.Generation)
            relay.Reposition(request.Position, result.Generation);
        return result.Succeeded;
    }

    public async ValueTask SelectRenditionAsync(int videoStreamIndex, int audioStreamIndex, CancellationToken cancellationToken = default)
    {
        await Worker.CallAsync("rendition", new WireRendition(Id, videoStreamIndex, audioStreamIndex),
            EngineWireContext.Default.WireRendition, EngineWireContext.Default.Boolean, cancellationToken);
        VideoStreamIndex = videoStreamIndex;
        AudioStreamIndex = audioStreamIndex;
    }

    public void ReportPlaybackPosition(double seconds)
    {
        if (!Status.IsBuffered || Math.Abs(seconds - reportedPosition) < PositionInterval)
            return;
        reportedPosition = seconds;
        if (relay is not null)
            relay.Source.UpdatePlaybackPosition(seconds);
        else
            Worker.PostPosition(Id, seconds);
    }

    public void CancelPendingOperations()
    {
        source?.Cancel();
        relay?.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Worker.Unregister(Id);
        try
        {
            if (Worker.Failure is null)
                await Worker.CallAsync("dispose", Id, EngineWireContext.Default.Int32, EngineWireContext.Default.Boolean);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.MediaWorker", "The media worker could not close a demuxer.", exception);
        }
        finally
        {
            if (source is not null)
                await source.DisposeAsync();
            if (relay is not null)
                await relay.DisposeAsync();
        }
    }

    void IWorkerComponent.OnStatus(DemuxerStatus status) => this.status = status;

    void IWorkerComponent.OnRelayStatus(long consumed) => relay?.OnConsumed(consumed);

    void IWorkerComponent.OnNotification(WireNotification notification)
    {
        if (notification.Kind == WireNotificationKind.Reconfigured)
        {
            if (notification.Info is not null)
                Info = WithCaseInsensitiveTags(notification.Info);
            Notified?.Invoke(this, new DemuxerNotification(DemuxerNotificationKind.Reconfigured, notification.Message, Info,
                notification.Position));
            return;
        }

        Notified?.Invoke(this, new DemuxerNotification(DemuxerNotificationKind.Faulted, notification.Message,
            Exception: new IOException(notification.Message)));
    }

    void IWorkerComponent.OnWorkerFailed(Exception failure)
    {
        relay?.Stop();
        Notified?.Invoke(this, new DemuxerNotification(DemuxerNotificationKind.Faulted, failure.Message, Exception: failure));
    }

    private static MediaInfo WithCaseInsensitiveTags(MediaInfo info) => info with
    {
        Metadata = new Dictionary<string, string>(info.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    internal static void Observe(Task operation) => _ = ObserveAsync(operation);

    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.MediaWorker", "A media worker command failed.", exception);
        }
    }
}

/// <summary>
/// Puts frames from the worker into the session's queue and grants the worker room for more. The
/// worker may have sent at most the allowance in total, so a full queue never overflows.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WorkerFrameOutput<T> where T : class, IMediaQueueItem
{
    private readonly MediaWorkerConnection worker;
    private readonly int decoder;
    private readonly MediaQueue<T> queue;
    private readonly int capacity;
    private readonly Action<T> release;
    private readonly Queue<T> held = new();
    private readonly CancellationTokenSource lifetime = new();
    private long received;
    private long granted;
    private long largestFrameBytes;

    public WorkerFrameOutput(MediaWorkerConnection worker, int decoder, IFrameProcessor<T> output, int capacity, Action<T> release)
    {
        this.worker = worker;
        this.decoder = decoder;
        queue = output as MediaQueue<T> ?? throw new NotSupportedException("A worker decoder writes into a media queue.");
        this.capacity = Math.Max(1, capacity);
        this.release = release;
        granted = Capacity;
        _ = WatchAsync(lifetime.Token);
    }

    public int Capacity => queue.ByteCapacity <= 0 ? capacity : largestFrameBytes <= 0 ? 1 :
        (int)Math.Min(capacity, (queue.ByteCapacity - 1) / largestFrameBytes + 1);

    public void Accept(T frame)
    {
        // End-of-stream markers are sent outside the allowance.
        if (!frame.IsEndOfStream)
        {
            received++;
            largestFrameBytes = Math.Max(largestFrameBytes, frame.SizeInBytes);
        }
        held.Enqueue(frame);
        Drain();
    }

    /// <summary>Accounts for a frame the worker sent that could not be imported.</summary>
    public void Skip()
    {
        received++;
        Drain();
    }

    public void Close()
    {
        lifetime.Cancel();
        while (held.TryDequeue(out T? frame))
            release(frame);
    }

    private void Drain()
    {
        while (held.TryPeek(out T? frame))
        {
            int serial = queue.Serial;
            if (frame.Serial < serial)
            {
                held.Dequeue();
                release(frame);
                continue;
            }

            // A frame of the next run waits until the session has moved its queue there.
            if (frame.Serial > serial || !queue.TryWrite(frame))
                break;
            held.Dequeue();
        }

        // Frames received and neither queued nor held here are consumed; the worker may send a full
        // queue's worth beyond those.
        long allowance = received + Capacity - queue.Count - held.Count;
        if (allowance > granted)
        {
            granted = allowance;
            worker.PostAllowance(decoder, allowance);
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int version = queue.Version;
                Drain();
                await queue.WaitForChangeAsync(version, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Web.MediaWorker", "The frame queue watch stopped.", exception);
        }
    }
}

[SupportedOSPlatform("browser")]
internal sealed class WorkerVideoDecoderStage : IVideoDecoderStage, IWorkerComponent
{
    private readonly WorkerDemuxer demuxer;
    private readonly VideoDecoderStageOptions options;
    private readonly MediaStreamInfo? stream;
    private WorkerFrameOutput<VideoFrame>? output;
    private VideoDecoderDiagnostics workerDiagnostics;
    private VideoDecoderDiagnostics? deliveredDiagnostics;
    private BrowserFrameInfo? lastFrame;
    private VideoPixelFormat deliveredFormat;
    private int disposed;

    public WorkerVideoDecoderStage(WorkerDemuxer demuxer, WireDecoderResult created, VideoDecoderStageOptions options)
    {
        this.demuxer = demuxer;
        this.options = options;
        Id = created.Decoder;
        StreamIndex = created.Stream;
        CodecName = created.Codec;
        workerDiagnostics = created.Diagnostics;
        stream = demuxer.Info.Streams.FirstOrDefault(candidate => candidate.Index == created.Stream);
        demuxer.Worker.Register(Id, this);
    }

    public MediaWorkerConnection Worker => demuxer.Worker;
    public int Id { get; }
    public int StreamIndex { get; }
    public string CodecName { get; }

    public VideoDecoderDiagnostics Diagnostics
    {
        get
        {
            VideoDecoderDiagnostics diagnostics = workerDiagnostics;
            if (lastFrame is not { } frame)
                return diagnostics;
            bool texture = deliveredFormat == VideoPixelFormat.ExternalTexture;
            bool planar = VideoPlanarLayout.IsPlanar(deliveredFormat);
            bool webCodecs = frame.Delivery?.StartsWith("webcodecs", StringComparison.Ordinal) == true;
            // WebCodecs reports nothing about the stream it decodes, so the picture is described by the frames it delivered.
            if (webCodecs && deliveredDiagnostics is { } delivered)
            {
                diagnostics = delivered with
                {
                    Implementation = diagnostics.Implementation, CodecName = diagnostics.CodecName,
                    DecoderName = diagnostics.DecoderName, Profile = diagnostics.Profile, Level = diagnostics.Level,
                };
            }

            return diagnostics with
            {
                DecoderDescription = frame.Delivery ?? (planar ? "shared-worker-planar" : "shared-worker-packed"),
                DeliveryPath = texture ? VideoFrameDeliveryPath.ExternalTexture
                    : webCodecs ? planar ? VideoFrameDeliveryPath.CopiedPlanar : VideoFrameDeliveryPath.CopiedConverted
                    : planar ? VideoFrameDeliveryPath.SoftwarePlanar : VideoFrameDeliveryPath.SoftwareConverted,
                OutputFormat = deliveredFormat,
                ZeroCopyStatus = frame.Resource > 0 ? "decoded pixels are uploaded to renderer textures" : string.Empty,
                SamplePixelFormat = texture ? "external-texture" : frame.SourceFormat ?? frame.Format ?? "bgra",
                BitDepth = texture ? 8 : VideoPlanarLayout.For(deliveredFormat).BitDepth,
                RendererSurfaceKind = VideoFrameCapabilities.SupportedSurfaceKind,
                RendererMaxPlanarBytesPerSample = VideoFrameCapabilities.MaxPlanarBytesPerSample,
                IsHardwareAccelerationKnown = !webCodecs,
            };
        }
    }

    public event EventHandler<DecoderFault>? Faulted;

    public void SetOutput(IFrameProcessor<VideoFrame> processor)
    {
        int capacity = processor is MediaQueue<VideoFrame> queue ? queue.Capacity : 8;
        output = new WorkerFrameOutput<VideoFrame>(Worker, Id, processor, capacity, frame => frame.Release());
        WorkerDemuxer.Observe(Worker.CallAsync("setOutput", new WireOutput(Id, output.Capacity), EngineWireContext.Default.WireOutput,
            EngineWireContext.Default.Boolean));
    }

    public void RequestOutput(VideoOutputRequest? request)
    {
    }

    public async ValueTask<bool> RecoverAsync(VideoDecoderRecovery recovery, string reason, CancellationToken cancellationToken = default) =>
        await Worker.CallAsync("recover", new WireRecover(Id, recovery, reason), EngineWireContext.Default.WireRecover,
            EngineWireContext.Default.Boolean, cancellationToken);

    public void CancelPendingOperations()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Worker.Unregister(Id);
        output?.Close();
        try
        {
            if (Worker.Failure is null)
                await Worker.CallAsync("dispose", Id, EngineWireContext.Default.Int32, EngineWireContext.Default.Boolean);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.MediaWorker", "The media worker could not close a video decoder.", exception);
        }
    }

    void IWorkerComponent.OnFrame(BrowserFrameInfo frame)
    {
        if (output is null || Volatile.Read(ref disposed) != 0)
        {
            MediaWorkerConnection.DropFrame(frame);
            return;
        }

        if (frame.Kind == 4)
        {
            var marker = VideoFrame.CreateEndOfStreamMarker();
            marker.Serial = frame.Serial;
            output.Accept(marker);
            return;
        }

        VideoFrame video;
        try
        {
            video = BrowserMediaFrames.CreateVideo(frame, stream);
        }
        catch (Exception exception)
        {
            MediaWorkerConnection.DropFrame(frame);
            output.Skip();
            AppLog.Error("Web.MediaWorker", "Could not import a decoded frame.", exception);
            return;
        }

        lastFrame = frame;
        deliveredFormat = video.Format;
        if (frame.Delivery?.StartsWith("webcodecs", StringComparison.Ordinal) == true)
        {
            deliveredDiagnostics = new VideoDecoderDiagnostics
            {
                Width = video.Width, Height = video.Height, PixelAspectRatio = video.PixelAspectRatio, Rotation = video.Rotation,
                MirrorHorizontally = video.MirrorHorizontally, ColorSpace = video.ColorSpace, ColorPrimaries = video.ColorPrimaries,
                ColorTransfer = video.ColorTransfer, ColorRange = video.ColorRange, ChromaLocation = video.ChromaLocation,
                HdrPeakLuminance = video.HdrPeakLuminance,
                Hdr10PlusApplicationVersion = video.Hdr10Plus.ApplicationVersion,
                Hdr10PlusWindowCount = video.Hdr10Plus.WindowCount,
                Hdr10PlusTargetLuminance = video.Hdr10Plus.TargetedSystemDisplayMaximumLuminance,
                Hdr10PlusSceneAverageLuminance = video.Hdr10Plus.SceneAverageLuminance,
                Hdr10PlusAnchorCount = video.Hdr10Plus.Windows[0].AnchorCount,
                Hdr10PlusToneMapping = video.Hdr10Plus.Windows[0].ToneMapping,
            };
        }

        options.FrameDecoded?.Invoke();
        output.Accept(video);
    }

    void IWorkerComponent.OnDiagnostics(VideoDecoderDiagnostics diagnostics) => workerDiagnostics = diagnostics;

    void IWorkerComponent.OnNotification(WireNotification notification)
    {
        if (notification.Fault is not { } fault)
            return;
        Faulted?.Invoke(this, new DecoderFault(fault.Message, fault.Serial, fault.Diagnostics, fault.RequiredSoftwareFallbackReason,
            fault.CanRestart, fault.EverDecoded, fault.IsSoftware, fault.Fatal ? new InvalidOperationException(fault.Message) : null));
    }

    void IWorkerComponent.OnWorkerFailed(Exception failure) =>
        Faulted?.Invoke(this, new DecoderFault(failure.Message, -1, Diagnostics, string.Empty, false, false, true, failure));
}

[SupportedOSPlatform("browser")]
internal sealed class WorkerAudioDecoderStage : IAudioDecoderStage, IWorkerComponent
{
    private readonly WorkerDemuxer demuxer;
    private readonly AudioFramePool pool = new();
    private WorkerFrameOutput<AudioFrame>? output;
    private int disposed;

    public WorkerAudioDecoderStage(WorkerDemuxer demuxer, WireDecoderResult created)
    {
        this.demuxer = demuxer;
        Id = created.Decoder;
        StreamIndex = created.Stream;
        CodecName = created.Codec;
        OutputFormat = created.Format;
        demuxer.Worker.Register(Id, this);
    }

    public MediaWorkerConnection Worker => demuxer.Worker;
    public int Id { get; }
    public int StreamIndex { get; }
    public string CodecName { get; }
    public AudioStreamFormat OutputFormat { get; }

    public event EventHandler<DecoderFault>? Faulted;

    public void SetOutput(IFrameProcessor<AudioFrame> processor)
    {
        int capacity = processor is MediaQueue<AudioFrame> queue ? queue.Capacity : 32;
        output = new WorkerFrameOutput<AudioFrame>(Worker, Id, processor, capacity, frame => frame.Recycle());
        WorkerDemuxer.Observe(Worker.CallAsync("setOutput", new WireOutput(Id, output.Capacity), EngineWireContext.Default.WireOutput,
            EngineWireContext.Default.Boolean));
    }

    public void SetSpeed(double speed) => Worker.PostSpeed(Id, speed);

    public void CancelPendingOperations()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Worker.Unregister(Id);
        output?.Close();
        try
        {
            if (Worker.Failure is null)
                await Worker.CallAsync("dispose", Id, EngineWireContext.Default.Int32, EngineWireContext.Default.Boolean);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.MediaWorker", "The media worker could not close an audio decoder.", exception);
        }
    }

    void IWorkerComponent.OnFrame(BrowserFrameInfo frame)
    {
        if (output is null || Volatile.Read(ref disposed) != 0)
            return;
        if (frame.Kind == 4)
        {
            var marker = AudioFrame.CreateEndOfStreamMarker();
            marker.Serial = frame.Serial;
            output.Accept(marker);
            return;
        }

        if (frame.Length < 0)
        {
            AppLog.Error("Web.MediaWorker", "The media worker sent a sound frame of negative length.");
            output.Skip();
            return;
        }

        AudioFrame block = pool.Rent(frame.Length, frame.Pts, frame.Duration, frame.Serial);
        try
        {
            Worker.CopyAudio(frame, block.Span);
        }
        catch (Exception exception)
        {
            block.Recycle();
            AppLog.Error("Web.MediaWorker", "Could not copy sound out of the media worker.", exception);
            output.Skip();
            return;
        }

        output.Accept(block);
    }

    void IWorkerComponent.OnNotification(WireNotification notification)
    {
        if (notification.Fault is not { } fault)
            return;
        Faulted?.Invoke(this, new DecoderFault(fault.Message, fault.Serial, fault.Diagnostics, fault.RequiredSoftwareFallbackReason,
            fault.CanRestart, fault.EverDecoded, true, fault.Fatal ? new InvalidOperationException(fault.Message) : null));
    }

    void IWorkerComponent.OnWorkerFailed(Exception failure) =>
        Faulted?.Invoke(this, new DecoderFault(failure.Message, -1, VideoDecoderDiagnostics.None, string.Empty, false, false, true, failure));
}
