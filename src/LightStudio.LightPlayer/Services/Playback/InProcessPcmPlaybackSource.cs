using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Avalonia.Audio;
using LightStudio.FfmpegShim;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.Playback;

internal sealed class InProcessPcmPlaybackSource(Func<CancellationToken, IPcmFrameSource> createSource) : IPcmPlaybackSource
{
    public static IPcmPlaybackSource ForStream(
        Stream stream, TimeSpan startPosition, TimeSpan? endPosition,
        Func<int> resolveSampleRate, IAudioSeparationService? separation) => new InProcessPcmPlaybackSource(token =>
    {
        int forcedRate = resolveSampleRate();
        AudioFormat? format = forcedRate > 0 ? new AudioFormat((uint)forcedRate, 2, 24) : null;
        if (separation is { Status.IsAvailable: true, Track: not AudioSeparationTrack.Original })
        {
            var session = separation.CreateSessionAsync(token).GetAwaiter().GetResult();
            if (session is not null)
                return new SeparatingPcmFrameSource(stream, startPosition, endPosition, forcedRate, separation, session, token);
        }
        return new FfmpegPcmFrameSource(stream, startPosition, format);
    });

    private const int FrameCapacity = 32;
    private const long ByteCapacity = 8L * 1024 * 1024;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly ConcurrentQueue<(PcmFrame Frame, TimeSpan EndPosition)> frames = new();
    private readonly ConcurrentQueue<(TimeSpan Position, TaskCompletionSource<TimeSpan> Completion, CancellationToken Cancellation)> seeks = new();
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? cancellation;
    private Task? disposal;
    private Exception? failure;
    private PcmFrame? inFlight;
    private long bufferedBytes;
    private long positionTicks;
    private volatile bool ended;
    private bool started;
    private bool disposed;

    public AudioFormat Format { get; private set; }
    public string? Title { get; private set; }
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref positionTicks));
    public string? LastError { get; private set; }
    public bool UsesAudioSeparation { get; private set; }
    public bool RequiresRestartOnSeek => UsesAudioSeparation;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
            {
                started = true;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                new Thread(Run) { Name = "music-decode", IsBackground = true }.Start();
            }
            return opened.Task;
        }
    }

    public bool TryRead(TimeSpan maximumMediaDuration, out AudioOutputReadResult block)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Interlocked.Exchange(ref inFlight, null)?.Dispose();
            if (failure is { } exception)
                ExceptionDispatchInfo.Capture(exception).Throw();
            if (maximumMediaDuration <= TimeSpan.Zero)
            {
                block = AudioOutputReadResult.Complete;
                return true;
            }
            if (!frames.TryDequeue(out var ready))
            {
                block = ended ? AudioOutputReadResult.Complete : default;
                return ended;
            }

            Interlocked.Add(ref bufferedBytes, -ready.Frame.Data.Length);
            int length = ready.Frame.Data.Length;
            if (ready.Frame.Duration > maximumMediaDuration)
                length = Format.AlignByteCount((int)Math.Min(length, maximumMediaDuration.TotalSeconds * Format.ByteRate));
            if (length <= 0)
            {
                ready.Frame.Dispose();
                block = AudioOutputReadResult.Complete;
                wake.Set();
                return true;
            }
            TimeSpan duration = Format.GetDuration(length);
            Interlocked.Exchange(ref positionTicks, (ready.EndPosition - ready.Frame.Duration + duration).Ticks);
            inFlight = ready.Frame;
            block = AudioOutputReadResult.FromBuffer(ready.Frame.Data[..length], duration);
            wake.Set();
            return true;
        }
    }

    public Task<TimeSpan> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is { } exception)
                return Task.FromException<TimeSpan>(exception);
            var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            seeks.Enqueue((position, completion, cancellationToken));
            wake.Set();
            return completion.Task;
        }
    }

    private void Run()
    {
        CancellationToken token = cancellation!.Token;
        IPcmFrameSource? source = null;
        try
        {
            token.ThrowIfCancellationRequested();
            source = createSource(token);
            Format = source.Format;
            Title = source.Title;
            Duration = source.Duration;
            Interlocked.Exchange(ref positionTicks, source.Position.Ticks);
            UsesAudioSeparation = source is SeparatingPcmFrameSource;
            if (source is SeparatingPcmFrameSource separated)
                separated.WaitForReadyAsync(token).GetAwaiter().GetResult();

            while (!token.IsCancellationRequested)
            {
                while (seeks.TryDequeue(out var seek))
                {
                    try
                    {
                        seek.Cancellation.ThrowIfCancellationRequested();
                        lock (gate)
                        {
                            ReleaseFrames();
                            Interlocked.Exchange(ref bufferedBytes, 0);
                            ended = false;
                        }
                        TimeSpan actual = source.Seek(seek.Position);
                        Interlocked.Exchange(ref positionTicks, actual.Ticks);
                        seek.Completion.TrySetResult(actual);
                    }
                    catch (OperationCanceledException exception)
                    {
                        seek.Completion.TrySetCanceled(exception.CancellationToken);
                    }
                    catch (Exception exception)
                    {
                        seek.Completion.TrySetException(exception);
                    }
                }

                if (!ended && frames.Count < FrameCapacity && Interlocked.Read(ref bufferedBytes) < ByteCapacity &&
                    source.TryReadFrame(TimeSpan.MaxValue, out PcmFrame? frame))
                {
                    if (frame is null)
                    {
                        LastError = source.LastError;
                        ended = true;
                    }
                    else
                    {
                        Interlocked.Add(ref bufferedBytes, frame.Data.Length);
                        frames.Enqueue((frame, source.Position));
                    }
                    opened.TrySetResult();
                    continue;
                }
                wake.WaitOne(10);
            }
            opened.TrySetCanceled(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            opened.TrySetCanceled(token);
        }
        catch (Exception exception)
        {
            failure = exception;
            opened.TrySetException(exception);
            AppLog.Error("Audio.Decoding", "The PCM decode worker failed.", exception);
        }
        finally
        {
            try { source?.Dispose(); }
            catch (Exception exception)
            {
                failure ??= exception;
                AppLog.Error("Audio.Decoding", "The PCM source failed to close.", exception);
            }
            while (seeks.TryDequeue(out var seek))
                seek.Completion.TrySetCanceled(token);
            ended = true;
            stopped.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            return new ValueTask(disposal ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        cancellation?.Cancel();
        wake.Set();
        if (started)
            await stopped.Task.ConfigureAwait(false);
        ReleaseFrames();
        cancellation?.Dispose();
        wake.Dispose();
    }

    private void ReleaseFrames()
    {
        Interlocked.Exchange(ref inFlight, null)?.Dispose();
        while (frames.TryDequeue(out var ready))
            ready.Frame.Dispose();
    }
}