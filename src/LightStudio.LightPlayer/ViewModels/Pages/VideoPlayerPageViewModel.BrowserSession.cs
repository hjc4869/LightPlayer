using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>Plays videos a browser extension redirected here and keeps the page's element in step.</summary>
public sealed partial class VideoPlayerPageViewModel
{
    /// <summary>Above the four reports a second the page side counts on while playing.</summary>
    private static readonly TimeSpan BrowserStateInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How far outside a buffered range a position still counts as inside it.</summary>
    private const double BrowserRangeTolerance = 0.1;

    /// <summary>How long state reports wait for the engine to reach what an element command asked for.</summary>
    private const long BrowserAwaitedStateMilliseconds = 1000;

    /// <summary>Buffered media ahead of the position that counts as enough to play through.</summary>
    private const double BrowserEnoughDataSeconds = 2;

    private IBrowserVideoSession? browserSession;
    private HttpMediaSourceProvider? browserFileProvider;
    private AppendMediaSourceProvider? browserSource;
    private DispatcherTimer? browserStateTimer;
    private long browserCommandAck;
    // The volume and mute of the page's element after its last volume command; null while the
    // application's own volume applies.
    private (double Level, bool Muted)? browserPageVolume;
    // The last command of each kind, so a new source's start state does not undo a newer command.
    private long browserPlayCommand;
    private long browserSeekCommand;
    private long browserRateCommand;
    // The engine state the last element command asked for. Reports that arrive before it, within a
    // second, are the engine catching up with earlier commands, not the app's own controls.
    private PlaybackState? browserAwaitedState;
    private long browserAwaitedAt;
    private bool browserMediaReady;
    private bool browserPaused = true;
    private bool browserRateZero;
    private long browserRateZeroAt;
    private bool browserEnded;
    private double browserStartSeconds;
    private double? browserSeekTarget;
    private long browserSeekIssuedAt;
    private Size videoSurfaceSize;
    private double videoSurfaceScaling = 1;
    private Size videoPictureSize;

    /// <summary>
    /// Plays a redirected video in place of whatever this page was showing. The page's element
    /// commands drive the player until the session ends, and the player's state goes back as reports.
    /// </summary>
    public async Task OpenBrowserSessionAsync(IBrowserVideoSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ReleaseBrowserSession();
        AppendMediaSourceProvider? source = session.File is null ? session.TakeSource() : null;
        browserSession = session;
        browserSource = source;
        browserCommandAck = 0;
        browserPlayCommand = 0;
        browserSeekCommand = 0;
        browserRateCommand = 0;
        browserAwaitedState = null;
        browserMediaReady = false;
        browserPaused = session.StartPaused;
        browserEnded = false;
        browserStartSeconds = session.StartSeconds;
        browserSeekTarget = null;
        session.CommandReceived += OnBrowserCommand;
        session.Ended += OnBrowserSessionEnded;
        session.SourceReset += OnBrowserSourceReset;
        session.SourceReplaced += OnBrowserSourceReplaced;

        // A session that ended while it was being handed over still delivers its end, which closes the video.
        if (session.IsEnded || (session.File is null && source is null))
        {
            source?.Dispose();
            Post(() =>
            {
                if (!ReferenceEquals(browserSession, session))
                    return;
                DetachBrowserSession();
                CloseCommand.Execute(null);
            });
            return;
        }

        if (browserStateTimer is null)
        {
            browserStateTimer = new DispatcherTimer { Interval = BrowserStateInterval };
            browserStateTimer.Tick += (_, _) => ReportBrowserState();
        }

        browserStateTimer.Start();
        SetBrowserRate(session.StartRate);
        ReportBrowserState();
        if (source is not null)
            await OpenBrowserSourceAsync(session, source, reopen: false).ConfigureAwait(true);
        else
            await OpenStreamCoreAsync(session.File!, session).ConfigureAwait(true);
    }

    /// <summary>
    /// Called by the view as the video surface changes: its size in device-independent pixels, the
    /// display scaling, and the size of the picture on it.
    /// </summary>
    public void UpdateVideoSurface(Size size, double scaling, Size pictureSize)
    {
        if (size == videoSurfaceSize && scaling == videoSurfaceScaling && pictureSize == videoPictureSize)
            return;

        videoSurfaceSize = size;
        videoSurfaceScaling = scaling > 0 ? scaling : 1;
        videoPictureSize = pictureSize;
        ReportBrowserState();
    }

    /// <summary>The length the transport shows: the page's timeline for media it pushes, which can change after opening.</summary>
    private double PlayerDurationSeconds => browserSource is { } source
        ? source.Timeline is { IsLive: false } timeline ? timeline.EndSeconds : 0
        : Player.Duration.TotalSeconds;

    /// <summary>Opens media the page pushes; the player owns the provider from here on.</summary>
    private async Task OpenBrowserSourceAsync(IBrowserVideoSession session, AppendMediaSourceProvider source, bool reopen)
    {
        await openSync.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!ReferenceEquals(browserSource, source))
            {
                source.Dispose();
                return;
            }

            if (reopen)
                DetachStreamProvider();
            else
                PrepareStreamOpen(source.Title);
            ResetStreamingTimeline();
            currentStreamProvider = source;
            source.TimelineChanged += OnStreamingTimelineChanged;
            source.LoadStageChanged += OnStreamingLoadStageChanged;
            Volatile.Write(ref streamOpenInProgress, 1);
            IsStreamLoading = true;
            UpdateStreamingLoadStage(source.LoadStage);
            bool handed = false;
            try
            {
                await playbackController.MusicOutputReleased.ConfigureAwait(true);
                if (disposed || !ReferenceEquals(browserSource, source))
                    return;
                handed = true;
                await Player.OpenOwnedAsync(source).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(currentStreamProvider, source))
                    DetachStreamProvider();
                return;
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(currentStreamProvider, source))
                    DetachStreamProvider();
                if (!ReferenceEquals(browserSession, session))
                    return;
                string detail = AppErrorReporter.LogAndGetMessage("Video.Playback", "Could not open the browser's media.", exception);
                ErrorMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Resources.VideoPlayer_StreamOpenFailedFormat,
                    source.Description,
                    detail);
                EndBrowserSession(BrowserSessionEndReason.Error, detail);
                return;
            }
            finally
            {
                if (!handed)
                    source.Dispose();
                Volatile.Write(ref streamOpenInProgress, 0);
                source.LoadStageChanged -= OnStreamingLoadStageChanged;
                IsStreamLoading = false;
                StreamLoadingText = string.Empty;
            }

            FinishStreamOpen(source.Timeline);
            if (ReferenceEquals(browserSession, session) && ReferenceEquals(browserSource, source))
                StartBrowserPlayback(session.StartSeconds);
        }
        finally
        {
            openSync.Release();
        }
    }

    /// <summary>Called once the redirected media is open: moves to the page's position and plays unless paused.</summary>
    private void StartBrowserPlayback(double startSeconds)
    {
        browserMediaReady = true;
        double target = browserSeekTarget ?? startSeconds;
        browserSeekTarget = null;

        // Play first, then seek, as the library path does.
        if (!browserPaused && !browserRateZero && (PlatformServices.CanStartPlayback?.Invoke() ?? true))
            PlayForBrowser();
        if (target > 0)
            SeekForBrowser(target);
        ReportBrowserState();
    }

    private void WatchBrowserFile(HttpMediaSourceProvider provider)
    {
        browserFileProvider = provider;
        provider.AccessDenied += OnBrowserFileAccessDenied;
    }

    private void OnBrowserFileAccessDenied(object? sender, HttpStatusCode status) => Post(() =>
    {
        if (browserSession is null || !ReferenceEquals(sender, browserFileProvider))
            return;

        string detail = string.Format(CultureInfo.InvariantCulture, "HTTP {0} ({1})", (int)status, status);
        AppLog.Warning("Video.Playback", $"The redirected file was refused with {detail}.");
        HttpMediaSourceProvider provider = browserFileProvider!;
        EndBrowserSession(BrowserSessionEndReason.Unauthorized, detail);
        if (Volatile.Read(ref streamOpenInProgress) != 0)
            provider.CancelPendingOperations();
        else
            Player.Stop();
        ErrorMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Resources.VideoPlayer_StreamOpenFailedFormat,
            "HTTP media",
            detail);
    });

    private void OnBrowserCommand(object? sender, BrowserElementCommand command)
    {
        if (!ReferenceEquals(sender, browserSession))
            return;

        switch (command.Kind)
        {
            case BrowserElementCommandKind.Play:
                browserPaused = false;
                if (browserMediaReady)
                {
                    if (!browserRateZero)
                        PlayForBrowser();
                    if (browserEnded)
                        SeekForBrowser(SeekMinimumSeconds);
                }
                browserEnded = false;
                break;
            case BrowserElementCommandKind.Pause:
                browserPaused = true;
                if (browserMediaReady)
                    PauseForBrowser();
                break;
            case BrowserElementCommandKind.Seek:
                browserEnded = false;
                if (browserMediaReady)
                {
                    SeekForBrowser(command.Value);
                }
                else
                {
                    browserSeekTarget = Math.Max(0, command.Value);
                }
                break;
            case BrowserElementCommandKind.Rate:
                SetBrowserRate(command.Value);
                break;
            case BrowserElementCommandKind.Volume:
                ApplyBrowserVolume(command.Value, command.Muted);
                break;
        }

        switch (command.Kind)
        {
            case BrowserElementCommandKind.Play or BrowserElementCommandKind.Pause:
                browserPlayCommand = command.Number;
                break;
            case BrowserElementCommandKind.Seek:
                browserSeekCommand = command.Number;
                break;
            case BrowserElementCommandKind.Rate:
                browserRateCommand = command.Number;
                break;
        }

        browserCommandAck = command.Number;
        ReportBrowserState();
    }

    private void SeekForBrowser(double seconds)
    {
        double maximum = SeekMaximumSeconds;
        if (browserSource is { } source && !IsLiveStream)
            maximum = double.IsFinite(source.Duration) && source.Duration > 0 ? source.Duration : double.MaxValue;
        double target = Math.Clamp(seconds, SeekMinimumSeconds, Math.Max(SeekMinimumSeconds, maximum));
        browserSeekTarget = target;
        browserSeekIssuedAt = Environment.TickCount64;
        Player.Seek(TimeSpan.FromSeconds(target));
    }

    /// <summary>
    /// The element's playback rate. A rate of 0 holds the picture while the element stays unpaused, as
    /// a page's player sets it while it buffers; a positive rate afterwards resumes playback.
    /// </summary>
    private void SetBrowserRate(double rate)
    {
        if (!double.IsFinite(rate))
            return;

        if (rate <= 0)
        {
            browserRateZero = true;
            browserRateZeroAt = Environment.TickCount64;
            if (browserMediaReady && !browserPaused)
                Player.Pause();
            return;
        }

        bool held = browserRateZero;
        browserRateZero = false;
        double value = Math.Clamp(rate, 0.25, 4);
        if (SpeedOptions.FirstOrDefault(option => Math.Abs(option.Value - value) < 0.001) is { } match)
            SelectedSpeedOption = match;
        Speed = value;
        if (held && browserMediaReady && !browserPaused && !browserEnded)
            Player.Play();
    }

    // The page's volume applies to this session only: neither the application's own volume nor the
    // level a mute returns to changes.
    private void ApplyBrowserVolume(double value, bool muted)
    {
        double level = double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : browserPageVolume?.Level ?? playbackController.Volume;
        browserPageVolume = (level, muted);
        ApplyVolumeToPlayer();
    }

    private void PlayForBrowser()
    {
        browserAwaitedState = PlaybackState.Playing;
        browserAwaitedAt = Environment.TickCount64;
        Player.Play();
    }

    private void PauseForBrowser()
    {
        browserAwaitedState = PlaybackState.Paused;
        browserAwaitedAt = Environment.TickCount64;
        Player.Pause();
    }

    private void OnBrowserPlayerStateChanged(PlaybackState state)
    {
        if (browserSession is null || !browserMediaReady)
            return;

        if (browserAwaitedState is { } awaited)
        {
            if (Environment.TickCount64 - browserAwaitedAt > BrowserAwaitedStateMilliseconds)
            {
                browserAwaitedState = null;
            }
            else if (state is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Paused)
            {
                bool reached = awaited == PlaybackState.Paused
                    ? state == PlaybackState.Paused
                    : state is PlaybackState.Playing or PlaybackState.Buffering;
                if (!reached)
                {
                    ReportBrowserState();
                    return;
                }

                browserAwaitedState = null;
            }
        }

        // The app's own transport controls reach the page through these settled states.
        switch (state)
        {
            case PlaybackState.Playing or PlaybackState.Buffering:
                // Playback that started just before a rate of 0 was applied is held again.
                if (browserRateZero && Environment.TickCount64 - browserRateZeroAt < 500)
                {
                    Player.Pause();
                    break;
                }

                browserPaused = false;
                browserEnded = false;
                browserRateZero = false;
                break;
            case PlaybackState.Paused or PlaybackState.Stopped:
                // A rate of 0 pauses the player while the element plays on.
                if (!browserRateZero || state == PlaybackState.Stopped)
                    browserPaused = true;
                break;
            case PlaybackState.Error:
                // The message arrives in a notification posted right after this one.
                IBrowserVideoSession? failed = browserSession;
                Post(() =>
                {
                    if (ReferenceEquals(browserSession, failed))
                        EndBrowserSession(BrowserSessionEndReason.Error, ErrorMessage);
                });
                break;
        }

        ReportBrowserState();
    }

    private void OnBrowserEndReached()
    {
        if (browserSession is null)
            return;

        browserEnded = true;
        browserPaused = true;
        ReportBrowserState();
    }

    private void ReportBrowserState()
    {
        if (browserSession is not { IsEnded: false } session)
            return;

        AppendMediaSourceProvider? source = browserSource;
        IReadOnlyList<MediaTimeRange> buffered = source?.Buffered ?? Array.Empty<MediaTimeRange>();
        bool engineSeeking = Player.StallReason == PlaybackStallReason.Seeking || Player.State == PlaybackState.Seeking;
        double position = Player.Position.TotalSeconds;
        long sinceSeek = Environment.TickCount64 - browserSeekIssuedAt;

        // Pushed media can take any time to arrive at a seek target; a file is given up on after a while.
        // Playback may already have moved on past the target by the first report after the seek.
        if (browserMediaReady && browserSeekTarget is { } target && !engineSeeking && sinceSeek > 250 &&
            (source is null
                ? Math.Abs(position - target) < 1 || sinceSeek > 3000
                : position > target - 1 && FindRange(buffered, target) is not null))
        {
            browserSeekTarget = null;
            if (source is not null && source.IsEnded && target >= SourceEnd(source, buffered) - EndTolerance())
                browserEnded = true;
        }

        bool seeking = browserSeekTarget is not null || (browserMediaReady && engineSeeking);
        double duration;
        bool ended;
        int readyState;
        int videoWidth;
        int videoHeight;
        if (source is null)
        {
            duration = Player.Duration.TotalSeconds;
            ended = browserEnded;
            readyState = !browserMediaReady ? 0
                : seeking ? 1
                : Player.StallReason == PlaybackStallReason.Buffering || Player.State == PlaybackState.Buffering ? 2
                : 4;
            var stream = Player.Info?.DefaultVideoStream;
            videoWidth = stream is null ? 0 : (int)Math.Round(stream.DisplaySize.Width);
            videoHeight = stream is null ? 0 : (int)Math.Round(stream.DisplaySize.Height);
        }
        else
        {
            // The element ends whenever its position rests at the end of a complete source.
            duration = SourceEnd(source, buffered);
            ended = !seeking && source.IsEnded &&
                (browserEnded || (browserMediaReady && position >= duration - EndTolerance()));
            readyState = SourceReadyState(source, buffered, position, seeking);
            AppendedTrack? track = source.Tracks.FirstOrDefault(each => each.Kind == "video");
            videoWidth = track?.Width ?? 0;
            videoHeight = track?.Height ?? 0;
        }

        if (videoPictureSize.Width > 0 && videoPictureSize.Height > 0 && browserMediaReady)
        {
            videoWidth = (int)Math.Round(videoPictureSize.Width);
            videoHeight = (int)Math.Round(videoPictureSize.Height);
        }

        var statistics = browserMediaReady ? Player.GetStatistics() : null;
        session.ReportState(new BrowserElementState
        {
            Ack = browserCommandAck,
            Time = !browserMediaReady ? browserSeekTarget ?? browserStartSeconds
                : ended && duration > 0 && double.IsFinite(duration) ? duration
                : browserSeekTarget ?? position,
            Paused = browserPaused || ended,
            Rate = browserRateZero ? 0 : Speed,
            Volume = browserPageVolume?.Level ?? (IsMuted ? volumeBeforeMute : Volume),
            Muted = browserPageVolume?.Muted ?? IsMuted,
            ReadyState = readyState,
            Seeking = seeking,
            Ended = ended,
            Duration = source is not null ? (double.IsFinite(source.Duration) ? source.Duration : null)
                : browserMediaReady && duration > 0 ? duration
                : null,
            VideoWidth = videoWidth,
            VideoHeight = videoHeight,
            TotalFrames = statistics is null ? 0 : statistics.PresentedVideoFrames + statistics.DroppedVideoFrames,
            DroppedFrames = statistics?.DroppedVideoFrames ?? 0,
            ViewWidth = videoSurfaceSize.Width,
            ViewHeight = videoSurfaceSize.Height,
            DevicePixelRatio = videoSurfaceScaling,
        });
    }

    /// <summary>
    /// Nothing until every buffer has an initialization segment, metadata until the player is open and
    /// the position is buffered, then how much is buffered ahead of it and whether the player waits.
    /// </summary>
    private int SourceReadyState(AppendMediaSourceProvider source, IReadOnlyList<MediaTimeRange> buffered, double position, bool seeking)
    {
        if (!source.IsReady)
            return 0;
        if (!browserMediaReady || seeking || FindRange(buffered, position) is not { } range)
            return 1;
        if (source.IsEnded && range.End >= buffered[^1].End)
            return 4;
        if (Player.StallReason == PlaybackStallReason.Buffering || Player.State == PlaybackState.Buffering)
            return 2;

        double ahead = range.End - position;
        return ahead < BrowserRangeTolerance ? 2 : ahead < BrowserEnoughDataSeconds ? 3 : 4;
    }

    private static MediaTimeRange? FindRange(IReadOnlyList<MediaTimeRange> ranges, double time)
    {
        foreach (MediaTimeRange range in ranges)
        {
            if (time >= range.Start - BrowserRangeTolerance && time <= range.End + BrowserRangeTolerance)
                return range;
        }

        return null;
    }

    /// <summary>Where a source ends: its duration, else the end of what is buffered.</summary>
    private static double SourceEnd(AppendMediaSourceProvider source, IReadOnlyList<MediaTimeRange> buffered)
    {
        double duration = source.Duration;
        if (double.IsFinite(duration))
            return duration;
        return buffered.Count > 0 ? buffered[^1].End : 0;
    }

    /// <summary>A position within a frame and a half of the end rests at the end.</summary>
    private double EndTolerance()
    {
        double frameRate = Player.Info?.DefaultVideoStream?.FrameRate ?? 0;
        return frameRate > 0 ? 1.5 / frameRate : 0.05;
    }

    private void OnBrowserSourceReset(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, browserSession) || browserSession is not { } session)
            return;

        // Commands sent after the new source's open reached this thread first; they stand.
        bool seekOwned = browserSeekCommand > session.StartCommand;
        browserEnded = false;
        if (!seekOwned)
        {
            browserSeekTarget = null;
            browserStartSeconds = session.StartSeconds;
        }

        if (browserSource is { } provider)
            MediaTitle = provider.Title;
        if (browserRateCommand <= session.StartCommand)
            SetBrowserRate(session.StartRate);
        if (browserPlayCommand <= session.StartCommand)
            browserPaused = session.StartPaused;
        if (browserMediaReady)
        {
            if (browserPaused)
                PauseForBrowser();
            else if (!browserRateZero)
                PlayForBrowser();
            if (!seekOwned && session.StartSeconds > 0)
                SeekForBrowser(session.StartSeconds);
        }

        ReportBrowserState();
    }

    private void OnBrowserSourceReplaced(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, browserSession) || browserSession is not { } session ||
            session.TakeSource() is not { } next)
        {
            return;
        }

        AppLog.Information("Video.Playback", "The browser's new media has other tracks; opening it again.");
        browserSource = next;
        browserMediaReady = false;
        browserEnded = false;
        browserSeekTarget = null;
        _ = OpenBrowserSourceAsync(session, next, reopen: true);
    }

    private void OnBrowserSessionEnded(object? sender, BrowserSessionEndReason reason)
    {
        if (!ReferenceEquals(sender, browserSession))
            return;

        AppLog.Information("Video.Playback", $"The browser ended the redirected video: {reason}.");
        DetachBrowserSession();

        // A replacement opens on this page right after; anything else takes the video away.
        if (reason != BrowserSessionEndReason.Replaced)
            CloseCommand.Execute(null);
    }

    /// <summary>Ends the redirect from the app's side, so the page resumes its own video where this one is.</summary>
    private void EndBrowserSession(BrowserSessionEndReason reason, string? message = null)
    {
        if (browserSession is not { } session)
            return;

        ReportBrowserState();
        DetachBrowserSession();
        session.End(reason, message);
    }

    private void ReleaseBrowserSession() => EndBrowserSession(BrowserSessionEndReason.Stopped);

    private void DetachBrowserSession()
    {
        if (browserSession is not { } session)
            return;

        session.CommandReceived -= OnBrowserCommand;
        session.Ended -= OnBrowserSessionEnded;
        session.SourceReset -= OnBrowserSourceReset;
        session.SourceReplaced -= OnBrowserSourceReplaced;
        browserSession = null;
        browserSource = null;
        browserStateTimer?.Stop();
        if (browserFileProvider is { } provider)
            provider.AccessDenied -= OnBrowserFileAccessDenied;
        browserFileProvider = null;
        browserMediaReady = false;
        browserRateZero = false;
        browserSeekTarget = null;
        if (browserPageVolume is not null)
        {
            browserPageVolume = null;
            ApplyVolumeToPlayer();
        }
    }
}
