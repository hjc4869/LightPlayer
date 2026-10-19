using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightMediaRenderer.Avalonia.Audio;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Threading;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

public sealed class MediaPlayer : IDisposable, IAsyncDisposable
{
    private readonly MediaPlayerOptions options;
    private readonly PlaybackDispatcher dispatcher = new();
    private readonly SemaphoreSlim lifetimeGate = new(1, 1);
    private readonly SemaphoreSlim seekGate = new(1, 1);
    private readonly SemaphoreSlim audioSetup = new(1, 1);
    private readonly MasterClock clock = new();
    private readonly StatisticsCollector statistics = new();
    private readonly object disposalGate = new();
    private IMediaDecodeSession? session;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? seekCancellation;
    private VideoScheduler? videoScheduler;
    private AudioRenderer? audioRenderer;
    private IVideoSink? videoSink;
    private Task? watchdog;
    private Task? disposal;
    private TaskCompletionSource? pendingStep;
    private volatile PlaybackState state = PlaybackState.Stopped;
    private volatile PlaybackStallReason stallReason;
    private PlaybackStallReason stallCandidate;
    private long stallCandidateSince;
    private long openGeneration;
    private long resetGeneration;
    private int pendingSeeks;
    private bool playing;
    private bool seeking;
    private bool previewReady;
    private bool videoEnded;
    private bool audioEnded;
    private bool disposed;
    private double lastKnownPosition;
    private double requestedSpeed = 1;
    private float requestedVolume = 1;
    private VideoOutputRequest? requestedVideoOutput;

    public MediaPlayer(MediaPlayerOptions? options = null) => this.options = options ?? MediaPlayerOptions.Default;

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<PlaybackStallReason>? StallChanged;
    public event EventHandler? EndReached;
    public event EventHandler? AudioTracksChanged;
    public event EventHandler<AudioTrack?>? AudioTrackChanged;
    public event EventHandler? VideoRenditionsChanged;
    public event EventHandler<VideoRendition?>? VideoRenditionChanged;
    public event EventHandler? SubtitleTracksChanged;
    public event EventHandler<SubtitleTrack?>? SubtitleTrackChanged;
    public event EventHandler<AssDocument?>? SubtitlesChanged;
    public event EventHandler<BitmapSubtitleDocument?>? BitmapSubtitlesChanged;
    public event EventHandler? ChaptersChanged;

    public MediaInfo? Info => session?.Info;
    public TimeSpan Duration => Info?.Duration ?? TimeSpan.Zero;
    public bool HasVideo => session?.HasVideo == true;
    public bool HasAudio => session?.HasAudio == true;
    public bool CanSeek => session?.CanSeek == true;
    public double BufferedSeconds => session?.BufferedSeconds ?? 0;
    public IReadOnlyList<AudioTrack> AudioTracks => session?.AudioTracks ?? Array.Empty<AudioTrack>();
    public AudioTrack? ActiveAudioTrack => session?.ActiveAudioTrack;
    public IReadOnlyList<VideoRendition> VideoRenditions => session?.VideoRenditions ?? Array.Empty<VideoRendition>();
    public VideoRendition? ActiveVideoRendition => session?.ActiveVideoRendition;
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => session?.SubtitleTracks ?? Array.Empty<SubtitleTrack>();
    public SubtitleTrack? ActiveSubtitleTrack => session?.ActiveSubtitleTrack;
    public AssDocument? Subtitles => session?.Subtitles;
    public BitmapSubtitleDocument? BitmapSubtitles => session?.BitmapSubtitles;
    public IReadOnlyList<MediaChapter> Chapters => Info?.Chapters ?? Array.Empty<MediaChapter>();
    public MediaChapter? CurrentChapter => FindChapter(Position);
    public PlaybackState State => state == PlaybackState.Playing && IsStarved() ? PlaybackState.Buffering : state;
    public PlaybackStallReason StallReason => stallReason;

    public TimeSpan Position
    {
        get
        {
            double position = seeking || pendingSeeks > 0 ? lastKnownPosition :
                state == PlaybackState.Paused && HasVideo ? clock.Video.Get() : clock.Get();
            if (!MediaTime.IsValid(position))
                position = lastKnownPosition;
            StreamingTimeline timeline = session?.Timeline ?? default;
            if (timeline.IsLive && timeline.EndSeconds > timeline.StartSeconds)
                position = Math.Clamp(position, timeline.StartSeconds, timeline.EndSeconds);
            else if (Duration.TotalSeconds > 0)
                position = Math.Clamp(position, 0, Duration.TotalSeconds);
            return timeline.IsLive ? TimeSpan.FromSeconds(position) : MediaTime.ToTimeSpan(position);
        }
    }

    public double Speed
    {
        get => requestedSpeed;
        set
        {
            requestedSpeed = Math.Clamp(value, 0.25, 4);
            Observe(dispatcher.InvokeAsync(() =>
            {
                clock.Speed = requestedSpeed;
                session?.SetSpeed(requestedSpeed);
                videoScheduler?.NotifyClockChanged();
                return Task.CompletedTask;
            }));
        }
    }

    public float Volume
    {
        get => requestedVolume;
        set
        {
            requestedVolume = Math.Clamp(value, 0, 1);
            Observe(dispatcher.InvokeAsync(() =>
            {
                if (audioRenderer is not null)
                    audioRenderer.Volume = requestedVolume;
                return Task.CompletedTask;
            }));
        }
    }

    public IVideoSink? VideoSink
    {
        get => videoSink;
        set
        {
            IVideoSink? previous = videoSink;
            videoSink = value;
            if (videoScheduler is not null)
                videoScheduler.Sink = value;
            if (previous is not null && !ReferenceEquals(previous, value))
                previous.ClearVideo();
        }
    }

    public MediaChapter? FindChapter(TimeSpan position) => Chapters.LastOrDefault(chapter => chapter.Start <= position);

    public void RequestVideoOutput(VideoOutputRequest? request)
    {
        requestedVideoOutput = request;
        session?.RequestVideoOutput(request);
    }

    public Task OpenAsync(string path) => OpenAsync(MediaDecodeSessionFactory.Create(new(path, options)));

    public Task OpenAsync(Func<Stream> openStream, string? name = null) => OpenAsync(MediaDecodeSessionFactory.IsRegistered
        ? MediaDecodeSessionFactory.Create(new(name ?? "media", options, _ => Task.FromResult(openStream())))
        : new MediaDecodeSession(options, () => new LocalMediaSourceProvider(openStream, name, options.OpenFile)));

    public Task OpenAsync(IMediaSourceProvider provider) =>
        OpenAsync(new MediaDecodeSession(options, provider, ownsProvider: false));

    public Task OpenOwnedAsync(IMediaSourceProvider provider) =>
        OpenAsync(new MediaDecodeSession(options, provider));

    public Task OpenOwnedAsync(Func<IMediaSourceProvider> createProvider) =>
        OpenAsync(new MediaDecodeSession(options, createProvider));

    public void Open(string path) => Observe(OpenAsync(path));
    public void Open(Func<Stream> openStream, string? name = null) => Observe(OpenAsync(openStream, name));
    public void Open(IMediaSourceProvider provider) => Observe(OpenAsync(provider));

    public Task OpenAsync(IMediaDecodeSession opened, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(opened);
        ObjectDisposedException.ThrowIf(disposed, this);
        long generation = ++openGeneration;
        CancelCurrentSession();
        await lifetimeGate.WaitAsync();
        try
        {
            await CloseSessionAsync();
            if (generation != openGeneration || disposed)
            {
                await opened.DisposeAsync();
                throw new OperationCanceledException();
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = cancellation.Token;
            session = opened;
            Attach(opened);
            opened.SetSpeed(requestedSpeed);
            opened.RequestVideoOutput(requestedVideoOutput);
            SetState(PlaybackState.Opening);
            await opened.OpenAsync(token);
            token.ThrowIfCancellationRequested();
            clock.Reset(opened.Serial);
            clock.SetExternal(0, opened.Serial);
            clock.Speed = requestedSpeed;
            clock.Pause();
            statistics.Reset();
            await EnsureAudioAsync(opened, token);
            token.ThrowIfCancellationRequested();
            clock.Preferred = opened.HasAudio ? ClockSource.Audio : ClockSource.Video;
            videoScheduler = new VideoScheduler(opened.VideoFrames, clock, statistics, () => opened.Serial, options.Sync)
            {
                Sink = videoSink,
                HoldClockAsync = hold => SetPlaybackHoldAsync(opened, hold),
                GetBufferCapacity = seconds => GetVideoBufferCapacity(opened, seconds),
                IsAudioPlaybackPending = IsWaitingForAudioPlayback,
            };
            videoScheduler.EndOfStream += OnVideoEnded;
            videoScheduler.Failed += OnSchedulerFailed;
            long reset = resetGeneration;
            if (opened.HasVideo)
                videoScheduler.StepFrame(() => Observe(dispatcher.InvokeAsync(() => FinishSeekAsync(opened, reset))));
            videoScheduler.Start();
            watchdog = PollAsync(opened, token);
            SetState(PlaybackState.Paused);
            PublishTracks();
            opened.SelectSubtitleTrack(opened.SubtitleTracks.FirstOrDefault(track => track.IsDefault));
        }
        catch
        {
            await CloseSessionAsync();
            if (generation == openGeneration && !disposed)
                SetState(cancellationToken.IsCancellationRequested ? PlaybackState.Stopped : PlaybackState.Error);
            throw;
        }
        finally { lifetimeGate.Release(); }
    });

    private async Task EnsureAudioAsync(IMediaDecodeSession opened, CancellationToken token)
    {
        await audioSetup.WaitAsync(token);
        try { await OpenAudioAsync(opened, token); }
        finally { audioSetup.Release(); }
    }

    private async Task OpenAudioAsync(IMediaDecodeSession opened, CancellationToken token)
    {
        if (!opened.HasAudio || audioRenderer is not null)
            return;
        var format = new AudioFormat((uint)opened.AudioFormat.SampleRate, (uint)opened.AudioFormat.ChannelCount, (uint)opened.AudioFormat.BitsPerSample);
        IAudioOutputDevice device;
        try
        {
            device = await AudioOutputDeviceFactory.CreateAsync(format, options.AudioDeviceBuffer, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLog.Warning("Audio.Output", "The audio device is unavailable; continuing with silent output.", exception);
            ErrorOccurred?.Invoke(this, exception.Message);
            device = new NullAudioOutputDevice(format, options.AudioDeviceBuffer);
        }
        if (token.IsCancellationRequested || !ReferenceEquals(session, opened))
        {
            await device.DisposeAsync();
            throw new OperationCanceledException(token);
        }
        audioRenderer = new AudioRenderer(opened.AudioFrames, device, clock, statistics, () => opened.Serial, options.AudioDeviceBuffer)
        {
            Volume = requestedVolume,
        };
        audioRenderer.EndOfStream += OnAudioEnded;
        audioRenderer.DeviceFailed += OnDeviceFailed;
        audioRenderer.ClockChanged += OnAudioClockChanged;
        audioRenderer.Start();
    }

    public void Play() => Observe(PlayAsync());
    public Task PlayAsync() => dispatcher.InvokeAsync(async () =>
    {
        if (session is null || session.IsFaulted || state == PlaybackState.Opening)
            return;
        playing = true;
        if (state == PlaybackState.EndOfStream)
            await SeekCoreAsync(TimeSpan.Zero);
        SetState(PlaybackState.Playing);
        await ResumePlaybackAsync();
    });

    public void Pause() => Observe(PauseAsync());
    public Task PauseAsync() => dispatcher.InvokeAsync(PauseCoreAsync);

    private async Task PauseCoreAsync()
    {
        playing = false;
        videoScheduler?.Pause();
        clock.Pause();
        if (session is not null)
            SetState(PlaybackState.Paused);
        if (audioRenderer is { } audio)
            await audio.HoldAsync();
    }

    public void TogglePause() => Observe(dispatcher.InvokeAsync(async () =>
    {
        if (playing)
            await PauseCoreAsync();
        else
            await PlayAsync();
    }));

    public void StepFrame() => Observe(StepFrameAsync());
    public Task StepFrameAsync() => dispatcher.InvokeAsync(async () =>
    {
        await PauseCoreAsync();
        if (videoScheduler is null || !HasVideo)
            return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingStep?.TrySetCanceled();
        pendingStep = completion;
        videoScheduler.StepFrame(() => completion.TrySetResult());
        try { await completion.Task.WaitAsync(cancellation!.Token); }
        finally
        {
            if (ReferenceEquals(pendingStep, completion))
                pendingStep = null;
        }
    });

    public void Seek(TimeSpan position) => Observe(SeekAsync(position));
    public Task SeekAsync(TimeSpan position) => dispatcher.InvokeAsync(() => SeekCoreAsync(position));

    private async Task SeekCoreAsync(TimeSpan position)
    {
        IMediaDecodeSession? current = session;
        if (current?.CanSeek != true)
            return;
        StreamingTimeline timeline = current.Timeline;
        double target = Math.Max(timeline.IsLive ? timeline.StartSeconds : 0, position.TotalSeconds);
        if (timeline.IsLive && timeline.EndSeconds > timeline.StartSeconds)
            target = Math.Min(target, timeline.EndSeconds);
        else if (Duration.TotalSeconds > 0)
            target = Math.Min(target, Duration.TotalSeconds);
        lastKnownPosition = target;
        seekCancellation?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation!.Token);
        seekCancellation = request;
        pendingSeeks++;
        try
        {
            await seekGate.WaitAsync(request.Token);
            try { await current.SeekAsync(target, request.Token); }
            finally { seekGate.Release(); }
        }
        finally
        {
            pendingSeeks--;
            if (ReferenceEquals(seekCancellation, request))
                seekCancellation = null;
            request.Dispose();
            if (ReferenceEquals(session, current) && pendingSeeks == 0 && previewReady)
                await FinishSeekAsync(current, resetGeneration);
        }
    }

    private Task BeforeResetAsync(IMediaDecodeSession owner, MediaDecodeReset reset) => dispatcher.InvokeAsync(async () =>
    {
        if (!ReferenceEquals(session, owner) || cancellation?.IsCancellationRequested != false)
            return;
        resetGeneration++;
        pendingStep?.TrySetCanceled();
        seeking = true;
        previewReady = false;
        videoScheduler?.Pause();
        clock.Pause();
        if (audioRenderer is { } audio)
        {
            await audio.HoldAsync();
            await audio.FlushDeviceAsync();
        }
    });

    private Task AfterResetAsync(IMediaDecodeSession owner, MediaDecodeReset reset) => dispatcher.InvokeAsync(async () =>
    {
        if (!ReferenceEquals(session, owner) || cancellation?.IsCancellationRequested != false)
            return;
        if (!reset.Succeeded)
        {
            lastKnownPosition = reset.Position;
            previewReady = true;
            await FinishSeekAsync(owner, resetGeneration);
            return;
        }
        await EnsureAudioAsync(owner, cancellation.Token);
        if (!ReferenceEquals(session, owner) || cancellation?.IsCancellationRequested != false)
            return;
        clock.Preferred = owner.HasAudio ? ClockSource.Audio : ClockSource.Video;
        clock.Reset(reset.Serial);
        clock.SetExternal(reset.Position, reset.Serial);
        lastKnownPosition = reset.Position;
        statistics.AudioVideoDrift = 0;
        statistics.VideoLatency = 0;
        videoEnded = false;
        audioEnded = false;
        videoScheduler?.RequireBuffering();
        long generation = resetGeneration;
        if (owner.HasVideo && videoScheduler is not null)
            videoScheduler.StepFrame(() => Observe(dispatcher.InvokeAsync(() => FinishSeekAsync(owner, generation))));
        else if (!owner.HasVideo)
            await FinishSeekAsync(owner, generation);
    });

    private async Task FinishSeekAsync(IMediaDecodeSession owner, long generation)
    {
        if (!ReferenceEquals(session, owner) || generation != resetGeneration || cancellation?.IsCancellationRequested != false)
            return;
        previewReady = true;
        if (pendingSeeks != 0)
            return;
        seeking = false;
        await ResumePlaybackAsync();
    }

    private async Task ResumePlaybackAsync()
    {
        IMediaDecodeSession? current = session;
        if (!playing || seeking || pendingSeeks != 0 || current is null || cancellation?.IsCancellationRequested != false)
            return;
        if (current.HasVideo && !videoEnded && videoScheduler is { } scheduler)
            await scheduler.ResumeAsync();
        else
            await SetPlaybackHoldAsync(current, false);
    }

    private async Task SetPlaybackHoldAsync(IMediaDecodeSession owner, bool hold)
    {
        if (!ReferenceEquals(session, owner) || !playing || cancellation?.IsCancellationRequested != false)
            return;
        hold |= seeking || pendingSeeks > 0 || owner.HasVideo && !videoEnded && videoScheduler?.IsBuffering == true;
        if (hold)
        {
            clock.Pause();
            if (audioRenderer is { } audio)
                await audio.HoldAsync();
        }
        else
        {
            if (!audioEnded && owner.HasAudio && audioRenderer is { } audio)
                await audio.ReleaseAsync();
            if (ReferenceEquals(session, owner) && playing && !seeking && pendingSeeks == 0)
                clock.Resume();
        }
    }

    private int GetVideoBufferCapacity(IMediaDecodeSession owner, double secondsWithoutProgress)
    {
        if (secondsWithoutProgress >= options.Sync.ResyncProgressTimeoutSeconds &&
            owner.VideoFrames.TryPeek(out VideoFrame frame))
        {
            bool surface = frame.HasSurface || frame.Format == VideoPixelFormat.ExternalTexture || frame.Resource is not null || frame.PlaneSource is not null;
            frame.Release();
            if (surface)
                return owner.VideoFrames.Count;
        }
        return owner.VideoBufferBlockedByAudio ? 0 : int.MaxValue;
    }

    private bool IsWaitingForAudioPlayback() => HasAudio && !audioEnded && audioRenderer?.IsWaitingForPlayback == true;
    private bool IsStarved() => HasVideo
        ? !videoEnded && (videoScheduler?.IsBuffering == true || IsWaitingForAudioPlayback())
        : HasAudio && !audioEnded && (session?.AudioFrames.Count ?? 0) == 0;

    private async Task PollAsync(IMediaDecodeSession owner, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                owner.UpdatePlaybackPosition(Position.TotalSeconds);
                PlaybackStallReason candidate = seeking || pendingSeeks > 0 ? PlaybackStallReason.Seeking :
                    playing && IsStarved() ? PlaybackStallReason.Buffering : PlaybackStallReason.None;
                if (candidate != stallCandidate)
                {
                    stallCandidate = candidate;
                    stallCandidateSince = Environment.TickCount64;
                }
                if (candidate == PlaybackStallReason.None || Environment.TickCount64 - stallCandidateSince >= options.StallReportDelay.TotalMilliseconds)
                    SetStall(candidate);
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void SelectAudioTrack(AudioTrack track) => Observe(SelectAudioTrackAsync(track));
    public Task SelectAudioTrackAsync(AudioTrack track) => ChangeTrackAsync(current => current.SelectAudioTrackAsync(track, cancellation!.Token));
    public void SelectVideoRendition(VideoRendition rendition) => Observe(SelectVideoRenditionAsync(rendition));
    public Task SelectVideoRenditionAsync(VideoRendition rendition) => ChangeTrackAsync(current => current.SelectVideoRenditionAsync(rendition, cancellation!.Token));

    private Task ChangeTrackAsync(Func<IMediaDecodeSession, Task> operation) => dispatcher.InvokeAsync(async () =>
    {
        IMediaDecodeSession current = session ?? throw new InvalidOperationException("No media is open.");
        pendingSeeks++;
        try
        {
            await seekGate.WaitAsync(cancellation!.Token);
            try { await operation(current); }
            finally { seekGate.Release(); }
        }
        finally
        {
            pendingSeeks--;
            if (ReferenceEquals(session, current) && pendingSeeks == 0 && previewReady)
                await FinishSeekAsync(current, resetGeneration);
        }
    });

    public Task<IReadOnlyList<AudioTrack>> AddExternalAudioAsync(string path) =>
        dispatcher.InvokeAsync(() => RequireSession().AddExternalAudioAsync(path));
    public Task<IReadOnlyList<SubtitleTrack>> AddExternalSubtitleAsync(string path) =>
        dispatcher.InvokeAsync(() => RequireSession().AddExternalSubtitleAsync(path));
    public Task<IReadOnlyList<SubtitleTrack>> AddSubtitleFileAsync(string path, byte[] content) =>
        dispatcher.InvokeAsync(() => RequireSession().AddSubtitleFileAsync(path, content));
    public async Task<SubtitleTrack> AddSubtitleScriptAsync(string path, Stream script)
    {
        using var bytes = new MemoryStream();
        await script.CopyToAsync(bytes);
        return (await AddSubtitleFileAsync(path, bytes.ToArray()))[0];
    }
    public void SelectSubtitleTrack(SubtitleTrack? track) => Observe(dispatcher.InvokeAsync(() =>
    {
        RequireSession().SelectSubtitleTrack(track);
        return Task.CompletedTask;
    }));
    private IMediaDecodeSession RequireSession() => session ?? throw new InvalidOperationException("No media is open.");

    public VideoFrame? CaptureFrame() => videoScheduler?.AcquireLastPresentedFrame();
    public PlaybackStatistics GetStatistics()
    {
        statistics.Sample();
        return (session?.GetStatistics() ?? PlaybackStatistics.Empty) with
        {
            State = State,
            Position = Position,
            MasterClock = clock.Active,
            RenderFps = statistics.RenderFps,
            PresentedVideoFrames = statistics.PresentedVideoFrames,
            DroppedVideoFrames = statistics.DroppedVideoFrames,
            LateVideoFrames = statistics.LateVideoFrames,
            AudioLatency = audioRenderer?.Latency ?? TimeSpan.Zero,
            VideoLatency = TimeSpan.FromSeconds(statistics.VideoLatency),
            AudioVideoDrift = TimeSpan.FromSeconds(statistics.AudioVideoDrift),
            AudioUnderruns = audioRenderer?.UnderrunCount ?? 0,
            Speed = requestedSpeed,
        };
    }

    public void Stop() => Observe(StopAsync());
    public Task StopAsync() => dispatcher.InvokeAsync(async () =>
    {
        openGeneration++;
        CancelCurrentSession();
        await lifetimeGate.WaitAsync();
        try
        {
            await CloseSessionAsync();
            SetState(PlaybackState.Stopped);
            PublishTracks();
        }
        finally { lifetimeGate.Release(); }
    });

    private void CancelCurrentSession()
    {
        playing = false;
        clock.Pause();
        cancellation?.Cancel();
        seekCancellation?.Cancel();
        pendingStep?.TrySetCanceled();
        session?.CancelPendingOperations();
        videoScheduler?.Pause();
    }

    private async Task CloseSessionAsync()
    {
        IMediaDecodeSession? closing = session;
        session = null;
        cancellation?.Cancel();
        closing?.CancelPendingOperations();
        if (videoScheduler is { } video)
        {
            video.EndOfStream -= OnVideoEnded;
            video.Failed -= OnSchedulerFailed;
            await video.StopAsync();
            videoScheduler = null;
        }
        if (audioRenderer is { } audio)
        {
            audio.EndOfStream -= OnAudioEnded;
            audio.ClockChanged -= OnAudioClockChanged;
            audio.DeviceFailed -= OnDeviceFailed;
            await audio.StopAsync();
            await audio.Device.DisposeAsync();
            audioRenderer = null;
        }
        if (watchdog is not null)
        {
            await watchdog;
            watchdog = null;
        }
        videoSink?.ClearVideo();
        if (closing is not null)
        {
            Detach(closing);
            await closing.DisposeAsync();
        }
        cancellation?.Dispose();
        cancellation = null;
        lastKnownPosition = 0;
        seeking = false;
        previewReady = false;
        videoEnded = false;
        audioEnded = false;
        clock.Reset(0);
        SetStall(PlaybackStallReason.None);
    }

    private void Attach(IMediaDecodeSession owner)
    {
        owner.BeforeResetAsync = reset => BeforeResetAsync(owner, reset);
        owner.AfterResetAsync = reset => AfterResetAsync(owner, reset);
        owner.ErrorOccurred += OnDecodeError;
        owner.AudioTracksChanged += OnTracksChanged;
        owner.AudioTrackChanged += OnAudioTrackChanged;
        owner.VideoRenditionsChanged += OnTracksChanged;
        owner.VideoRenditionChanged += OnVideoRenditionChanged;
        owner.SubtitleTracksChanged += OnTracksChanged;
        owner.SubtitleTrackChanged += OnSubtitleTrackChanged;
        owner.SubtitlesChanged += OnSubtitlesChanged;
        owner.BitmapSubtitlesChanged += OnBitmapSubtitlesChanged;
    }

    private void Detach(IMediaDecodeSession owner)
    {
        owner.BeforeResetAsync = null;
        owner.AfterResetAsync = null;
        owner.ErrorOccurred -= OnDecodeError;
        owner.AudioTracksChanged -= OnTracksChanged;
        owner.AudioTrackChanged -= OnAudioTrackChanged;
        owner.VideoRenditionsChanged -= OnTracksChanged;
        owner.VideoRenditionChanged -= OnVideoRenditionChanged;
        owner.SubtitleTracksChanged -= OnTracksChanged;
        owner.SubtitleTrackChanged -= OnSubtitleTrackChanged;
        owner.SubtitlesChanged -= OnSubtitlesChanged;
        owner.BitmapSubtitlesChanged -= OnBitmapSubtitlesChanged;
    }

    private void DispatchNotification(object? sender, Action notification) => dispatcher.Post(state =>
    {
        if (ReferenceEquals(sender, session))
            notification();
    }, null);

    private void OnTracksChanged(object? sender, EventArgs args) => DispatchNotification(sender, PublishTracks);
    private void OnAudioTrackChanged(object? sender, AudioTrack? track) => DispatchNotification(sender, () => AudioTrackChanged?.Invoke(this, track));
    private void OnVideoRenditionChanged(object? sender, VideoRendition? rendition) => DispatchNotification(sender, () => VideoRenditionChanged?.Invoke(this, rendition));
    private void OnSubtitleTrackChanged(object? sender, SubtitleTrack? track) => DispatchNotification(sender, () => SubtitleTrackChanged?.Invoke(this, track));
    private void OnSubtitlesChanged(object? sender, AssDocument? document) => DispatchNotification(sender, () => SubtitlesChanged?.Invoke(this, document));
    private void OnBitmapSubtitlesChanged(object? sender, BitmapSubtitleDocument? document) => DispatchNotification(sender, () => BitmapSubtitlesChanged?.Invoke(this, document));
    private void OnDecodeError(object? sender, string message) => DispatchNotification(sender, () =>
    {
        if (session?.IsFaulted == true)
            FailPlayback(message);
        else
            ErrorOccurred?.Invoke(this, message);
    });
    private void OnSchedulerFailed(object? sender, Exception exception) => FailPlayback(exception.Message);
    private void FailPlayback(string message)
    {
        playing = false;
        cancellation?.Cancel();
        clock.Pause();
        videoScheduler?.Pause();
        session?.CancelPendingOperations();
        if (audioRenderer is { } audio)
            Observe(audio.HoldAsync());
        SetState(PlaybackState.Error);
        ErrorOccurred?.Invoke(this, message);
    }
    private void OnDeviceFailed(object? sender, string message) => ErrorOccurred?.Invoke(this, message);
    private void OnAudioClockChanged(object? sender, EventArgs args) => videoScheduler?.NotifyClockChanged();
    private void OnVideoEnded(object? sender, EventArgs args) { videoEnded = true; CheckFinished(); }
    private void OnAudioEnded(object? sender, EventArgs args) { audioEnded = true; CheckFinished(); }

    private void CheckFinished()
    {
        if (session is null || HasVideo && !videoEnded || HasAudio && !audioEnded || state == PlaybackState.EndOfStream)
            return;
        playing = false;
        clock.Pause();
        SetState(PlaybackState.EndOfStream);
        EndReached?.Invoke(this, EventArgs.Empty);
    }

    private void PublishTracks()
    {
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
        AudioTrackChanged?.Invoke(this, ActiveAudioTrack);
        VideoRenditionsChanged?.Invoke(this, EventArgs.Empty);
        VideoRenditionChanged?.Invoke(this, ActiveVideoRendition);
        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
        SubtitleTrackChanged?.Invoke(this, ActiveSubtitleTrack);
        SubtitlesChanged?.Invoke(this, Subtitles);
        BitmapSubtitlesChanged?.Invoke(this, BitmapSubtitles);
        ChaptersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(PlaybackState value)
    {
        if (state == value)
            return;
        state = value;
        StateChanged?.Invoke(this, State);
    }

    private void SetStall(PlaybackStallReason value)
    {
        if (stallReason == value)
            return;
        stallReason = value;
        StallChanged?.Invoke(this, value);
    }

    private void Observe(Task operation) => _ = ObserveAsync(operation);
    private async Task ObserveAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Error("Playback.Core", "A playback operation failed.", exception);
            ErrorOccurred?.Invoke(this, exception.Message);
        }
    }

    public void Dispose() => Observe(DisposeAsync().AsTask());
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposal ??= dispatcher.InvokeAsync(async () =>
            {
                disposed = true;
                await StopAsync();
            });
            return new ValueTask(disposal);
        }
    }
}