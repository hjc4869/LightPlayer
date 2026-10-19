#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>
/// Runs the pumps in this process, one thread per demuxer and decoder, with blocking hand-offs
/// between them. Commands are executed on the thread that owns the component.
/// </summary>
public sealed class InProcessMediaEngine : IMediaEngine
{
    public string Name => "in-process";

    public async ValueTask<IMediaDemuxer> OpenDemuxerAsync(MediaInput input, CancellationToken cancellationToken = default)
    {
        IPacketSource source = await Task.Run(async () => await OpenSourceAsync(input, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return new Demuxer(source);
    }

    public async ValueTask<IVideoDecoderStage> CreateVideoDecoderAsync(
        IMediaDemuxer demuxer,
        VideoDecoderStageOptions options,
        CancellationToken cancellationToken = default)
    {
        Demuxer owner = Own(demuxer);
        IVideoDecoder decoder = await owner.Thread.InvokeAsync(
            () => VideoDecoderFactory.Create(owner.Pump.Source, options.Decoder), cancellationToken).ConfigureAwait(false);
        return new VideoStage(decoder, owner.Pump.Source, options);
    }

    public async ValueTask<IAudioDecoderStage> CreateAudioDecoderAsync(
        IMediaDemuxer demuxer,
        int streamIndex,
        AudioDecoderStageOptions options,
        CancellationToken cancellationToken = default)
    {
        Demuxer owner = Own(demuxer);
        IAudioDecoder decoder = await owner.Thread.InvokeAsync(() =>
        {
            var created = new FfmpegAudioDecoder(owner.Pump.Source, streamIndex, options.Format);
            try
            {
                created.Configure(options.Format, options.Speed);
                return (IAudioDecoder)created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        return new AudioStage(decoder, streamIndex, options);
    }

    public ValueTask<string> ReadSubtitleScriptAsync(MediaInput input, int streamIndex, CancellationToken cancellationToken = default) =>
        new(Task.Run(async () =>
        {
            using FfmpegDemuxer reader = await OpenContainerAsync(input, cancellationToken).ConfigureAwait(false);
            return FfmpegSubtitleReader.Read(reader, streamIndex, cancellationToken);
        }, cancellationToken));

    public ValueTask<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        MediaInput input,
        int streamIndex,
        bool preserveContainerTimestamps,
        CancellationToken cancellationToken = default) =>
        new(Task.Run(async () =>
        {
            using FfmpegDemuxer reader = await OpenContainerAsync(input, cancellationToken).ConfigureAwait(false);
            return FfmpegSubtitleReader.ReadBitmaps(reader, streamIndex, cancellationToken, preserveContainerTimestamps);
        }, cancellationToken));

    public ValueTask<BitmapSubtitleDocument> DecodeBitmapSubtitlesAsync(
        BitmapSubtitlePackets packets,
        CancellationToken cancellationToken = default) =>
        new(Task.Run(() => FfmpegSubtitleReader.ReadBitmaps(packets, cancellationToken), cancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Opens <paramref name="input"/> as a packet source. Blocks; call off the caller's context.</summary>
    public static async Task<IPacketSource> OpenSourceAsync(MediaInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (input is PacketSourceMediaInput packets)
            return packets.Open() ?? throw new InvalidOperationException("The source could not be opened.");
        if (input is AsyncPacketSourceMediaInput opened)
            return await opened.Open(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The source could not be opened.");
        return await OpenContainerAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FfmpegDemuxer> OpenContainerAsync(MediaInput input, CancellationToken cancellationToken)
    {
        Stream stream = input switch
        {
            StreamMediaInput opened => await opened.Open(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The source could not be opened."),
            MemoryMediaInput memory => MemoryMarshal.TryGetArray(memory.Content, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(memory.Content.ToArray(), writable: false),
            PacketSourceMediaInput packets => throw new NotSupportedException(
                $"'{packets.Name}' is read through its provider, not as a container."),
            AsyncPacketSourceMediaInput packets => throw new NotSupportedException(
                $"'{packets.Name}' is read through its provider, not as a container."),
            _ => throw new NotSupportedException($"The in-process engine cannot read {input.GetType().Name}."),
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FfmpegDemuxer.Open(stream, input.Name, leaveOpen: false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Demuxer Own(IMediaDemuxer demuxer) => demuxer as Demuxer
        ?? throw new NotSupportedException("The demuxer belongs to another engine.");

    private sealed class Demuxer : IMediaDemuxer
    {
        private readonly MediaPacketPool pool = new();
        private int disposed;

        public Demuxer(IPacketSource source)
        {
            Pump = new DemuxerPump(source, PumpMode.Blocking, pool, notification => Notified?.Invoke(this, notification));
            Thread = new PumpThread("media-demux", Pump.Step, token => Pump.Interrupt = token, Pump.AbortLinks,
                exception => Notified?.Invoke(this, new DemuxerNotification(DemuxerNotificationKind.Faulted,
                    exception.Message, Exception: exception)), idleMilliseconds: 5, running: false);
        }

        public DemuxerPump Pump { get; }

        public PumpThread Thread { get; }

        public MediaInfo Info => Pump.Source.Info;
        public int VideoStreamIndex => Pump.Source.VideoStreamIndex;
        public int AudioStreamIndex => Pump.Source.AudioStreamIndex;
        public bool CanSeek => Pump.Source.CanSeek;
        public IReadOnlyList<VideoRendition> Renditions =>
            (Pump.Source as IAdaptiveVideoPacketSource)?.VideoRenditions ?? Array.Empty<VideoRendition>();
        public DemuxerStatus Status => Pump.GetStatus();

        public event EventHandler<DemuxerNotification>? Notified;

        public ValueTask LinkAsync(int streamIndex, IMediaDecoderStage? decoder, CancellationToken cancellationToken = default)
        {
            PacketLink? link = decoder switch
            {
                null => null,
                VideoStage video => video.Input,
                AudioStage audio => audio.Input,
                _ => throw new NotSupportedException("The decoder belongs to another engine."),
            };
            return new ValueTask(Thread.InvokeAsync(() =>
            {
                Pump.Link(streamIndex, link);
                return true;
            }, cancellationToken));
        }

        public void Start() => Thread.Resume();

        public ValueTask<bool> SeekAsync(SeekRequest request, CancellationToken cancellationToken = default) =>
            new(Thread.InvokeAsync(() => Pump.Seek(request), cancellationToken));

        public ValueTask SelectRenditionAsync(int videoStreamIndex, int audioStreamIndex, CancellationToken cancellationToken = default) =>
            new(Thread.InvokeAsync(() =>
            {
                Pump.SelectRendition(videoStreamIndex, audioStreamIndex);
                return true;
            }, cancellationToken));

        public void ReportPlaybackPosition(double seconds) => Pump.ReportPlaybackPosition(seconds);

        public void CancelPendingOperations() => Thread.Interrupt();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return ValueTask.CompletedTask;
            return new ValueTask(Task.Run(() =>
            {
                Thread.Dispose();
                Pump.Dispose();
                pool.Clear();
            }));
        }
    }

    private sealed class VideoStage : IVideoDecoderStage
    {
        private readonly VideoDecoderPump pump;
        private readonly PumpThread thread;
        private readonly int streamIndex;
        private int disposed;

        public VideoStage(IVideoDecoder decoder, IPacketSource source, VideoDecoderStageOptions options)
        {
            streamIndex = decoder.StreamIndex;
            pump = new VideoDecoderPump(decoder, source,
                new PacketLink("video-packets", options.PacketCapacity, options.PacketByteCapacity),
                PumpMode.Blocking, options, fault => Faulted?.Invoke(this, fault));
            thread = new PumpThread("video-decode", pump.Step, token => pump.Interrupt = token, pump.Input.Queue.Abort,
                exception => Faulted?.Invoke(this, new DecoderFault(exception.Message, -1, pump.Diagnostics,
                    string.Empty, false, false, false, exception)), idleMilliseconds: 1, running: true);
        }

        public PacketLink Input => pump.Input;
        public int StreamIndex => streamIndex;
        public string CodecName => pump.Decoder.CodecName;
        public VideoDecoderDiagnostics Diagnostics => pump.Diagnostics;

        public event EventHandler<DecoderFault>? Faulted;

        public void SetOutput(IFrameProcessor<VideoFrame> output)
        {
            pump.SetOutput(output);
            thread.Wake();
        }

        public void RequestOutput(VideoOutputRequest? request) => pump.RequestOutput(request);

        public ValueTask<bool> RecoverAsync(VideoDecoderRecovery recovery, string reason, CancellationToken cancellationToken = default) =>
            new(thread.InvokeAsync(() => pump.Recover(recovery, reason), cancellationToken));

        public void CancelPendingOperations()
        {
            pump.CancelPendingOperations();
            thread.Interrupt();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return ValueTask.CompletedTask;
            pump.Input.Close();
            return new ValueTask(Task.Run(() =>
            {
                thread.Dispose();
                pump.Dispose();
            }));
        }
    }

    private sealed class AudioStage : IAudioDecoderStage
    {
        private readonly AudioDecoderPump pump;
        private readonly PumpThread thread;
        private int disposed;

        public AudioStage(IAudioDecoder decoder, int streamIndex, AudioDecoderStageOptions options)
        {
            StreamIndex = streamIndex;
            pump = new AudioDecoderPump(decoder,
                new PacketLink("audio-packets", options.PacketCapacity, options.PacketByteCapacity),
                PumpMode.Blocking, options, fault => Faulted?.Invoke(this, fault));
            thread = new PumpThread("audio-decode", pump.Step, token => pump.Interrupt = token, pump.Input.Queue.Abort,
                exception => Faulted?.Invoke(this, new DecoderFault(exception.Message, -1, VideoDecoderDiagnostics.None,
                    string.Empty, false, false, true, exception)), idleMilliseconds: 1, running: true);
        }

        public PacketLink Input => pump.Input;
        public int StreamIndex { get; }
        public string CodecName => pump.Decoder.CodecName;
        public AudioStreamFormat OutputFormat => pump.Decoder.OutputFormat;

        public event EventHandler<DecoderFault>? Faulted;

        public void SetOutput(IFrameProcessor<AudioFrame> output)
        {
            pump.SetOutput(output);
            thread.Wake();
        }

        public void SetSpeed(double speed) => pump.SetSpeed(speed);

        public void CancelPendingOperations()
        {
            pump.CancelPendingOperations();
            thread.Interrupt();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return ValueTask.CompletedTask;
            pump.Input.Close();
            return new ValueTask(Task.Run(() =>
            {
                thread.Dispose();
                pump.Dispose();
            }));
        }
    }
}

/// <summary>
/// A thread that steps one pump and runs the commands posted to it in between. A command interrupts
/// any hand-off the pump is blocked in, so it runs promptly even while queues are full.
/// </summary>
internal sealed class PumpThread : IDisposable
{
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly ManualResetEventSlim signal = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Func<PumpResult> step;
    private readonly Action<CancellationToken> setInterrupt;
    private readonly Action interruptWaits;
    private readonly Action<Exception> onError;
    private readonly int idleMilliseconds;
    private readonly Thread thread;
    private CancellationTokenSource interrupt = new();
    private volatile bool running;
    private volatile bool failed;

    public PumpThread(
        string name,
        Func<PumpResult> step,
        Action<CancellationToken> setInterrupt,
        Action interruptWaits,
        Action<Exception> onError,
        int idleMilliseconds,
        bool running)
    {
        this.step = step;
        this.setInterrupt = setInterrupt;
        this.interruptWaits = interruptWaits;
        this.onError = onError;
        this.idleMilliseconds = idleMilliseconds;
        this.running = running;
        setInterrupt(interrupt.Token);
        thread = new Thread(Run) { Name = name, IsBackground = true };
        thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stop.IsCancellationRequested)
        {
            completion.TrySetException(new ObjectDisposedException(thread.Name));
            return completion.Task;
        }

        commands.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        Interrupt();
        return completion.Task;
    }

    public void Resume()
    {
        running = true;
        signal.Set();
    }

    public void Wake() => signal.Set();

    /// <summary>Breaks the pump out of a blocked hand-off.</summary>
    public void Interrupt()
    {
        lock (gate)
            interrupt.Cancel();
        interruptWaits();
        signal.Set();
    }

    public void Dispose()
    {
        stop.Cancel();
        Interrupt();
        if (Thread.CurrentThread != thread)
            thread.Join();
        while (commands.TryDequeue(out Action? command))
            command();
        signal.Dispose();
        stop.Dispose();
        interrupt.Dispose();
    }

    private void Run()
    {
        CancellationToken token = stop.Token;
        while (!token.IsCancellationRequested)
        {
            RunCommands();
            PumpResult result = PumpResult.Idle;
            if (running && !failed)
            {
                try
                {
                    result = step();
                }
                catch (Exception exception)
                {
                    failed = true;
                    if (token.IsCancellationRequested)
                        AppLog.Warning("Media.Engine", $"{thread.Name} failed while closing.", exception);
                    else
                        onError(exception);
                }
            }

            if (result == PumpResult.Progressed)
                continue;

            try
            {
                signal.Wait(result == PumpResult.Idle && running && !failed ? idleMilliseconds : 50, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            signal.Reset();
        }
    }

    private void RunCommands()
    {
        if (commands.IsEmpty)
            return;

        // Fresh interrupt first, so a command posted while these run interrupts the next wait.
        lock (gate)
        {
            if (interrupt.IsCancellationRequested)
            {
                interrupt.Dispose();
                interrupt = new CancellationTokenSource();
                setInterrupt(interrupt.Token);
            }
        }

        while (commands.TryDequeue(out Action? command))
            command();
    }
}
