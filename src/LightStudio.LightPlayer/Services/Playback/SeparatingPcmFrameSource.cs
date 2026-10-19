using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.Playback;

internal sealed class SeparatingPcmFrameSource : IPcmFrameSource
{
    private readonly FfmpegPcmFrameSource inner;
    private readonly IAudioSeparationService service;
    private readonly FfmpegPcmResampler resampler;
    private readonly CancellationToken cancellationToken;
    private readonly TimeSpan? endPosition;
    private readonly float[] scratch = new float[32768];
    private readonly int bufferCapacity;
    private readonly long prebufferFrames;
    private Channel<PcmFrame> frames = null!;
    private CancellationTokenSource producerCancellation = null!;
    private Task producer = Task.CompletedTask;
    private TaskCompletionSource prebuffered = null!;
    private IAudioSeparationSession? session;
    private TimeSpan startPosition;
    private long emittedFrames;
    private long producedFrames;
    private long separatedFrames;
    private bool inputEnded;
    private bool disposed;

    internal SeparatingPcmFrameSource(Stream stream, TimeSpan start, TimeSpan? end, int outputRate,
        IAudioSeparationService service, IAudioSeparationSession session, CancellationToken cancellationToken)
    {
        this.service = service;
        this.session = session;
        this.cancellationToken = cancellationToken;
        endPosition = end;
        try
        {
            inner = new FfmpegPcmFrameSource(stream, start, new AudioFormat((uint)session.SampleRate, 2, 24));
            Format = new AudioFormat((uint)(outputRate > 0 ? outputRate : session.SampleRate), 2, 24);
            resampler = new FfmpegPcmResampler(session.SampleRate, (int)Format.SampleRate);
            startPosition = inner.Position;
            prebufferFrames = Math.Max(1, (long)session.OutputBlockFrames * Format.SampleRate / session.SampleRate);
            bufferCapacity = Math.Max(2, (int)Math.Ceiling(2d * session.OutputBlockFrames / (scratch.Length / 2)) + 2);
            StartProducer();
        }
        catch
        {
            session.Dispose();
            inner?.Dispose();
            resampler?.Dispose();
            throw;
        }
    }

    public AudioFormat Format { get; }
    public string? Title => inner.Title;
    public TimeSpan Duration => inner.Duration;
    public TimeSpan Position => startPosition + TimeSpan.FromSeconds((double)Interlocked.Read(ref emittedFrames) / Format.SampleRate);
    public string? LastError => inner.LastError;

    public Task WaitForReadyAsync(CancellationToken token) => prebuffered.Task.WaitAsync(token);

    public TimeSpan Seek(TimeSpan target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StopProducer();
        startPosition = inner.Seek(target);
        emittedFrames = 0;
        producedFrames = 0;
        separatedFrames = 0;
        inputEnded = false;
        session?.Reset();
        resampler.Reset();
        StartProducer();
        return startPosition;
    }

    public bool TryReadFrame(TimeSpan maxDuration, out PcmFrame? frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        frame = null;
        if (maxDuration <= TimeSpan.Zero)
            return true;
        if (!frames.Reader.TryRead(out var buffered))
        {
            if (!frames.Reader.Completion.IsCompleted)
                return false;
            frames.Reader.Completion.GetAwaiter().GetResult();
            return true;
        }
        var data = buffered.Data;
        var framesToEmit = (int)Math.Min(data.Length / Format.BlockAlign, maxDuration.TotalSeconds * Format.SampleRate);
        if (framesToEmit <= 0)
        {
            buffered.Dispose();
            return true;
        }
        if (framesToEmit * Format.BlockAlign != data.Length)
        {
            int length = framesToEmit * Format.BlockAlign;
            buffered.Trim(length, Format.GetDuration(length));
        }
        Interlocked.Add(ref emittedFrames, framesToEmit);
        frame = buffered;
        return true;
    }

    private void StartProducer()
    {
        frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(bufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prebuffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        producer = Task.Run(ProduceAsync);
    }

    private async Task ProduceAsync()
    {
        var token = producerCancellation.Token;
        try
        {
            while (await ProduceFrameAsync(token).ConfigureAwait(false) is { } frame)
            {
                await frames.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                if (producedFrames >= prebufferFrames || session is { } separator && separatedFrames >= separator.OutputBlockFrames)
                    prebuffered.TrySetResult();
            }
            frames.Writer.TryComplete();
            prebuffered.TrySetResult();
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            frames.Writer.TryComplete(exception);
            prebuffered.TrySetCanceled(token);
        }
        catch (Exception exception)
        {
            AppLog.Error("AudioSeparation", "Preparing separated audio failed.", exception);
            frames.Writer.TryComplete(exception);
            prebuffered.TrySetException(exception);
        }
    }

    private void StopProducer()
    {
        producerCancellation.Cancel();
        producer.GetAwaiter().GetResult();
        producerCancellation.Dispose();
        while (frames.Reader.TryRead(out PcmFrame? frame))
            frame.Dispose();
    }

    private async ValueTask<PcmFrame?> ProduceFrameAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            byte[] data;
            if (session is { } separator)
            {
                int frames;
                try
                {
                    frames = await separator.ReadAsync(scratch, token).ConfigureAwait(false);
                    service.ReportDevice(separator.ActiveDeviceId, separator);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && !token.IsCancellationRequested)
                {
                    service.ReportFailure(exception, separator);
                    separator.Dispose();
                    session = null;
                    inner.Seek(startPosition + TimeSpan.FromSeconds((double)producedFrames / Format.SampleRate));
                    inputEnded = false;
                    resampler.Reset();
                    continue;
                }
                if (frames > 0)
                {
                    separatedFrames += frames;
                    data = resampler.Convert(scratch.AsSpan(0, frames * 2));
                }
                else if (separator.IsFinished)
                {
                    data = resampler.Convert([]);
                    if (data.Length == 0)
                        return null;
                }
                else
                {
                    using var input = ReadInput();
                    if (input is null)
                        separator.CompleteInput();
                    else
                        separator.Append(Unpack(input.Data.Span));
                    continue;
                }
            }
            else
            {
                using var input = ReadInput();
                data = resampler.Convert(input is null ? [] : Unpack(input.Data.Span));
                if (input is null && data.Length == 0)
                    return null;
            }
            if (data.Length == 0)
                continue;
            producedFrames += data.Length / Format.BlockAlign;
            return new PcmFrame(data, Format.GetDuration(data.Length));
        }
    }

    private PcmFrame? ReadInput()
    {
        if (inputEnded)
            return null;
        var frame = inner.ReadFrame(endPosition is { } end ? end - inner.Position : TimeSpan.MaxValue);
        inputEnded = frame is null;
        return frame;
    }

    private static float[] Unpack(ReadOnlySpan<byte> data)
    {
        var samples = new float[data.Length / 3];
        for (var index = 0; index < samples.Length; index++)
        {
            var offset = index * 3;
            var sample = (data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) >> 8;
            samples[index] = sample / 8388608f;
        }
        return samples;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        StopProducer();
        session?.Dispose();
        resampler.Dispose();
        inner.Dispose();
    }
}