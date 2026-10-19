using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Android implementation of <see cref="ISystemMediaControls"/> built on the framework
/// <see cref="MediaSession"/> (API 21+). It publishes "now playing" metadata and transport state to
/// the OS (lock screen, media notification, hardware/Bluetooth media keys) and raises the transport
/// events the shared playback controller listens to. While an item is loaded it runs
/// <see cref="MediaPlaybackService"/> as a foreground service so playback continues in the background.
/// </summary>
internal sealed class AndroidSystemMediaControls : ISystemMediaControls, IDisposable
{
    private const string ChannelId = "im.hjc.LightPlayer.playback";

    /// <summary>Set to the live instance so <see cref="MediaPlaybackService"/> can route transport actions back.</summary>
    public static AndroidSystemMediaControls Instance { get; private set; }

    /// <summary>
    /// Whether an activity of this app is currently resumed. Android 12+ (API 31) rejects
    /// <c>startForegroundService()</c> from the background with
    /// <c>ForegroundServiceStartNotAllowedException</c>, and the shared playback controller publishes
    /// the restored "now playing" item while the process is still binding, from inside
    /// <c>Application.OnCreate</c>, before any activity exists. Starting the service there tears the
    /// process down before the first frame, which the user sees as the app hanging on the splash
    /// window.
    /// </summary>
    private static volatile bool appInForeground;

    private readonly Context context;
    private readonly MediaSession session;
    private readonly object gate = new();

    private string title = string.Empty;
    private string artist = string.Empty;
    private string album = string.Empty;
    private Bitmap artwork;
    private long positionMs;
    private long durationMs;
    private bool isPlaying;
    private bool hasItem;
    private bool serviceRunning;
    private bool foregroundStartPending;
    private bool disposed;

    /// <summary>
    /// Set once the user dismissed the media notification (or transport was stopped) while paused.
    /// The session is deactivated and every remote resume request is ignored until playback is
    /// started again from inside the app; otherwise a headset/Bluetooth route change, which the OS
    /// turns into a play media-button event aimed at the last active session, silently restarts
    /// audio in the background.
    /// </summary>
    private volatile bool transportDismissed;

    public AndroidSystemMediaControls(Context context)
    {
        this.context = context.ApplicationContext ?? context;
        session = new MediaSession(this.context, "LightPlayer");
        session.SetCallback(new TransportCallback(this));
        session.Active = true;
        Instance = this;
        UpdateSession();
    }

    public event Action PlayPauseRequested;

    public event Action NextRequested;

    public event Action PreviousRequested;

    public event Action<TimeSpan> SeekRequested;

    public void SetNowPlaying(string title, string artist, string album)
    {
        lock (gate)
        {
            this.title = title ?? string.Empty;
            this.artist = artist ?? string.Empty;
            this.album = album ?? string.Empty;
            // A new track invalidates the previous artwork until it is resolved.
            artwork?.Recycle();
            artwork = null;
        }

        UpdateSession();
        RefreshForeground();
    }

    public void SetPlaybackState(bool isPlaying, bool hasItem)
    {
        lock (gate)
        {
            this.isPlaying = isPlaying;
            this.hasItem = hasItem;
        }

        // Playback started again (necessarily from inside the app, since a dismissed transport
        // cannot resume), so the session becomes a legitimate media-button target once more.
        if (isPlaying && transportDismissed)
        {
            transportDismissed = false;
            session.Active = true;
        }

        UpdateSession();

        if (hasItem)
        {
            RefreshForeground();
        }
        else
        {
            StopForeground();
        }
    }

    public void SetArtwork(byte[] artworkContent)
    {
        var bitmap = LoadBitmap(artworkContent);
        lock (gate)
        {
            artwork?.Recycle();
            artwork = bitmap;
        }

        UpdateSession();
        RefreshForeground();
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
        bool durationChanged;
        lock (gate)
        {
            var pos = (long)Math.Max(0, position.TotalMilliseconds);
            var dur = (long)Math.Max(0, duration.TotalMilliseconds);
            durationChanged = dur != durationMs;
            positionMs = pos;
            durationMs = dur;
        }

        // The seek-bar length is carried by the session metadata, so it only needs a
        // rebuild when the duration actually changes (a new track). The position is
        // published on every tick through the (cheap) playback state, from which the OS
        // interpolates using the reported speed. Neither path touches the foreground
        // notification, which reads position/duration straight from the session.
        if (durationChanged)
        {
            UpdateMetadata();
        }

        UpdatePlaybackState();
    }

    // ---- Transport callbacks routed from the session / notification (any thread) ----

    internal void RaisePlayPause()
    {
        if (!IsPlaying && transportDismissed)
        {
            return;
        }

        PlayPauseRequested?.Invoke();
    }

    /// <summary>
    /// Handles an explicit "play" from a remote transport (media keys, headset/Bluetooth reconnect,
    /// lock screen). It is a no-op while already playing and while the transport is dismissed, so a
    /// route change can never resume audio the user did not ask for.
    /// </summary>
    internal void RaisePlay()
    {
        if (IsPlaying || transportDismissed)
        {
            return;
        }

        PlayPauseRequested?.Invoke();
    }

    /// <summary>Handles an explicit "pause" from a remote transport; ignored when already paused.</summary>
    internal void RaisePause()
    {
        if (!IsPlaying)
        {
            return;
        }

        PlayPauseRequested?.Invoke();
    }

    internal void RaiseNext() => NextRequested?.Invoke();

    internal void RaisePrevious() => PreviousRequested?.Invoke();

    internal void RaiseSeek(TimeSpan position) => SeekRequested?.Invoke(position);

    /// <summary>
    /// Handles a session "stop" request. This fires when a remote transport sends stop and, more
    /// commonly, when the user swipes the media notification away while paused (the only state in
    /// which it is dismissible). It must never resume playback: a stop while playing is translated
    /// into a pause, while a stop when already paused dismisses the transport and leaves the paused
    /// playback state untouched.
    /// </summary>
    internal void RaiseStop()
    {
        if (IsPlaying)
        {
            PlayPauseRequested?.Invoke();
        }
        else
        {
            DismissTransport();
        }
    }

    private bool IsPlaying
    {
        get
        {
            lock (gate)
            {
                return isPlaying;
            }
        }
    }

    /// <summary>
    /// Tears down the notification and deactivates the session so the OS stops routing media-button
    /// events (including the play the audio stack replays on headset/Bluetooth reconnect) here.
    /// </summary>
    private void DismissTransport()
    {
        if (transportDismissed)
        {
            return;
        }

        transportDismissed = true;
        session.Active = false;
        StopForeground();
    }

    private void UpdateSession()
    {
        UpdateMetadata();
        UpdatePlaybackState();
    }

    private void UpdateMetadata()
    {
        string t, a, al;
        Bitmap art;
        long dur;
        lock (gate)
        {
            t = title;
            a = artist;
            al = album;
            art = artwork;
            dur = durationMs;
        }

        var metadataBuilder = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, t)
            .PutString(MediaMetadata.MetadataKeyArtist, a)
            .PutString(MediaMetadata.MetadataKeyAlbum, al)
            .PutLong(MediaMetadata.MetadataKeyDuration, dur);
        if (art is not null)
        {
            metadataBuilder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, art);
        }

        session.SetMetadata(metadataBuilder.Build());
    }

    private void UpdatePlaybackState()
    {
        long pos;
        bool playing, item;
        lock (gate)
        {
            pos = positionMs;
            playing = isPlaying;
            item = hasItem;
        }

        const long actions = PlaybackState.ActionPlay
            | PlaybackState.ActionPause
            | PlaybackState.ActionPlayPause
            | PlaybackState.ActionSkipToNext
            | PlaybackState.ActionSkipToPrevious
            | PlaybackState.ActionSeekTo
            | PlaybackState.ActionStop;
        var stateCode = !item
            ? PlaybackStateCode.Stopped
            : playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused;
        session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(actions)
            .SetState(stateCode, pos, playing ? 1.0f : 0.0f)
            .Build());
    }

    private void RefreshForeground()
    {
        if (disposed || transportDismissed)
        {
            return;
        }

        // Re-delivering to an already-running foreground service is always permitted; a *first*
        // start is not, unless the app is currently in the foreground. Remember the request instead
        // and replay it from OnAppForegroundChanged once an activity is resumed, which is what
        // happens right after a cold start restores the previous queue.
        if (!serviceRunning && !appInForeground)
        {
            foregroundStartPending = true;
            return;
        }

        // Starting (or re-delivering to) the foreground service re-runs OnStartCommand, which
        // re-asserts foreground with a freshly built notification reflecting current state.
        var intent = new Intent(context, typeof(MediaPlaybackService));
        try
        {
            context.StartForegroundService(intent);
            serviceRunning = true;
            foregroundStartPending = false;
        }
        catch (Java.Lang.IllegalStateException)
        {
            // ForegroundServiceStartNotAllowedException and friends. The OS decides the app is not
            // allowed to hold a foreground service right now (it may also have killed the service
            // behind our back, leaving serviceRunning stale). Never let that take down the process:
            // playback keeps working, only the notification is missing until the next attempt.
            serviceRunning = false;
            foregroundStartPending = true;
        }
        catch (Java.Lang.SecurityException)
        {
            serviceRunning = false;
            foregroundStartPending = true;
        }
    }

    private void StopForeground()
    {
        foregroundStartPending = false;
        if (!serviceRunning)
        {
            return;
        }

        serviceRunning = false;
        context.StopService(new Intent(context, typeof(MediaPlaybackService)));
    }

    /// <summary>
    /// Tracks whether the app has a resumed activity, and flushes a foreground-service start that
    /// was deferred while the app was in the background. Called from the activity's resume/pause.
    /// </summary>
    internal static void OnAppForegroundChanged(bool foreground)
    {
        appInForeground = foreground;
        if (foreground)
        {
            Instance?.FlushPendingForeground();
        }
    }

    /// <summary>Records that the service asserted foreground on its own (for example a sticky restart).</summary>
    internal void NotifyForegroundStarted()
    {
        serviceRunning = true;
        foregroundStartPending = false;
    }

    /// <summary>
    /// Records that the service went away, so the next refresh starts it again. If playback is not
    /// running the service can only have gone away because the notification was swiped or stopped,
    /// which counts as a dismissal even on the Android versions that do not deliver a session stop.
    /// </summary>
    internal void NotifyForegroundStopped()
    {
        serviceRunning = false;
        if (!IsPlaying)
        {
            DismissTransport();
        }
    }

    private void FlushPendingForeground()
    {
        if (disposed || !foregroundStartPending)
        {
            return;
        }

        bool item;
        lock (gate)
        {
            item = hasItem;
        }

        if (!item)
        {
            foregroundStartPending = false;
            return;
        }

        RefreshForeground();
    }

    /// <summary>Builds the media-style notification shown by <see cref="MediaPlaybackService"/>.</summary>
    public Notification BuildNotification()
    {
        EnsureChannel();

        string t, a;
        Bitmap art;
        bool playing;
        lock (gate)
        {
            t = title;
            a = artist;
            art = artwork;
            playing = isPlaying;
        }

        var builder = new Notification.Builder(context, ChannelId)
            .SetStyle(new Notification.MediaStyle()
                .SetMediaSession(session.SessionToken)
                .SetShowActionsInCompactView(0, 1, 2))
            .SetVisibility(NotificationVisibility.Public)
            .SetSmallIcon(context.ApplicationInfo!.Icon)
            .SetContentTitle(t)
            .SetContentText(a)
            .SetOngoing(playing);

        if (art is not null)
        {
            builder.SetLargeIcon(art);
        }

        builder.AddAction(BuildAction(global::Android.Resource.Drawable.IcMediaPrevious, "Previous", MediaPlaybackService.ActionPrevious));
        builder.AddAction(BuildAction(
            playing ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay,
            "Play/Pause",
            MediaPlaybackService.ActionPlayPause));
        builder.AddAction(BuildAction(global::Android.Resource.Drawable.IcMediaNext, "Next", MediaPlaybackService.ActionNext));

        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is not null)
        {
            builder.SetContentIntent(PendingIntent.GetActivity(
                context,
                0,
                launch,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        }

        return builder.Build();
    }

    private Notification.Action BuildAction(int iconRes, string title, string action)
    {
        var intent = new Intent(context, typeof(MediaPlaybackService)).SetAction(action);
        var pending = PendingIntent.GetService(
            context,
            action.GetHashCode(),
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var icon = Icon.CreateWithResource(context, iconRes);
        return new Notification.Action.Builder(icon, title, pending).Build();
    }

    private void EnsureChannel()
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        if (manager.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
        {
            LockscreenVisibility = NotificationVisibility.Public,
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private static Bitmap LoadBitmap(byte[] artworkContent)
    {
        if (artworkContent is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return BitmapFactory.DecodeByteArray(artworkContent, 0, artworkContent.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopForeground();
        session.Active = false;
        session.Release();
        lock (gate)
        {
            artwork?.Recycle();
            artwork = null;
        }

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private sealed class TransportCallback(AndroidSystemMediaControls owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.RaisePlay();

        public override void OnPause() => owner.RaisePause();

        public override void OnSkipToNext() => owner.RaiseNext();

        public override void OnSkipToPrevious() => owner.RaisePrevious();

        public override void OnSeekTo(long pos) => owner.RaiseSeek(TimeSpan.FromMilliseconds(Math.Max(0, pos)));

        public override void OnStop() => owner.RaiseStop();
    }
}
