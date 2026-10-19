#nullable enable
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Foreground service of type <c>mediaPlayback</c> that keeps the process alive and shows the
/// media-style notification while audio plays in the background. It owns no playback state of its
/// own: transport actions from the notification are forwarded to <see cref="AndroidSystemMediaControls"/>
/// (which drives the shared playback controller), and the notification content is built there too.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
internal sealed class MediaPlaybackService : Service
{
    public const string ActionPlayPause = "im.hjc.LightPlayer.action.PLAY_PAUSE";
    public const string ActionNext = "im.hjc.LightPlayer.action.NEXT";
    public const string ActionPrevious = "im.hjc.LightPlayer.action.PREVIOUS";

    public const int NotificationId = 0x11C3;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var controls = AndroidSystemMediaControls.Instance;
        if (controls is null)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        switch (intent?.Action)
        {
            case ActionPlayPause:
                controls.RaisePlayPause();
                break;
            case ActionNext:
                controls.RaiseNext();
                break;
            case ActionPrevious:
                controls.RaisePrevious();
                break;
        }

        // (Re)assert foreground with the current notification. Called on every transport/metadata
        // change, so the notification always reflects live state. Android 12+ can still refuse the
        // promotion between the start request and this callback (the app may have left the
        // foreground meanwhile); give the service up rather than letting the platform exception
        // reach the runtime and kill the whole process.
        try
        {
            StartForeground(NotificationId, controls.BuildNotification(), ForegroundService.TypeMediaPlayback);
        }
        catch (Java.Lang.RuntimeException)
        {
            controls.NotifyForegroundStopped();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Keep the controls' view of the service in sync for starts it did not make itself, such as
        // the sticky restart the system performs after killing the process.
        controls.NotifyForegroundStarted();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        AndroidSystemMediaControls.Instance?.NotifyForegroundStopped();
        base.OnDestroy();
    }
}
