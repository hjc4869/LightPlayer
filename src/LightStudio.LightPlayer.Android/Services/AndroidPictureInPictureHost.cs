#nullable enable
using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Util;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Android picture in picture. The system shrinks the activity itself into a floating window rather
/// than hosting a view of ours, so nothing is re-hosted and the pipeline never hears about it: the
/// page stays the picture and only has to put its controls away.
/// </summary>
/// <remarks>
/// The mode is also entered without being asked, when the user leaves the app while a video runs.
/// That is the platform's own doing: it needs the parameters to be in place beforehand, which is
/// what <see cref="Offer"/> keeps up to date.
/// </remarks>
internal sealed class AndroidPictureInPictureHost : IPictureInPictureHost
{
    /// <summary>Shapes the platform accepts. Anything outside is refused with an exception.</summary>
    private const double MinAspectRatio = 1 / 2.39;
    private const double MaxAspectRatio = 2.39;

    /// <summary>Denominator the shape is handed over with, the platform wanting it as a fraction.</summary>
    private const int AspectRatioDenominator = 1000;

    private readonly Activity activity;
    private readonly bool supported;

    private double aspectRatio;
    private bool active;

    /// <summary>
    /// Whether a video is running. It arms the platform's own entry into the mode and picks the
    /// transport button's face, both of which follow playback.
    /// </summary>
    private bool playing;

    public AndroidPictureInPictureHost(Activity activity)
    {
        this.activity = activity;
        supported = activity.PackageManager?.HasSystemFeature(PackageManager.FeaturePictureInPicture) == true;
    }

    public PictureInPictureKind Kind => PictureInPictureKind.HostManaged;

    public bool IsAvailable => supported;

    public bool IsActive => active;

    public event Action<bool>? ActiveChanged;

    public void Enter(PictureInPictureRequest request)
    {
        if (!supported || active)
        {
            return;
        }

        aspectRatio = request.AspectRatio;

        var entered = false;
        try
        {
            entered = activity.EnterPictureInPictureMode(BuildParams());
        }
        catch (Java.Lang.Exception exception)
        {
            AppLog.Warning("Android.PictureInPicture", "The mode was refused.", exception);
        }

        // Refused, most often because the user turned the feature off for the app. The page is
        // waiting to hear that the picture left; tell it that it stayed.
        if (!entered)
        {
            ActiveChanged?.Invoke(false);
        }
    }

    public void Exit()
    {
        if (!active)
        {
            return;
        }

        // Nothing asks the system to leave the mode: bringing the activity back to the front is
        // what expands the window again.
        try
        {
            var intent = new Intent(activity, activity.Class);
            intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            activity.StartActivity(intent);
        }
        catch (Java.Lang.Exception exception)
        {
            AppLog.Warning("Android.PictureInPicture", "The window could not be expanded.", exception);
        }
    }

    public void Offer(PictureInPictureRequest? request)
    {
        if (!supported)
        {
            return;
        }

        playing = request is not null;
        if (request is not null)
        {
            aspectRatio = request.AspectRatio;
        }

        Apply();
    }

    /// <summary>Reports the mode being entered or left, however it was asked for.</summary>
    internal void OnModeChanged(bool value)
    {
        if (active == value)
        {
            return;
        }

        active = value;
        ActiveChanged?.Invoke(value);
    }

    private void Apply()
    {
        try
        {
            activity.SetPictureInPictureParams(BuildParams());
        }
        catch (Java.Lang.Exception exception)
        {
            AppLog.Warning("Android.PictureInPicture", "The window could not be described.", exception);
        }
    }

    private PictureInPictureParams BuildParams()
    {
        var builder = new PictureInPictureParams.Builder();
        builder.SetAutoEnterEnabled(playing);
        builder.SetSeamlessResizeEnabled(true);

        if (BuildPlayPauseAction() is { } action)
        {
            builder.SetActions(new[] { action });
        }

        if (aspectRatio > 0)
        {
            var clamped = Math.Clamp(aspectRatio, MinAspectRatio, MaxAspectRatio);
            builder.SetAspectRatio(new Rational(
                (int)Math.Round(clamped * AspectRatioDenominator),
                AspectRatioDenominator));
        }

        // The binding says the builder may hand back nothing; the platform never does.
        return builder.Build()!;
    }

    /// <summary>
    /// The one control the window has room for. It sends the very intent the media notification's
    /// button does, which lands on whoever holds the OS transport controls while the video page is open.
    /// </summary>
    private RemoteAction? BuildPlayPauseAction()
    {
        var intent = new Intent(activity, typeof(MediaPlaybackService));
        intent.SetAction(MediaPlaybackService.ActionPlayPause);

        var pending = PendingIntent.GetService(
            activity,
            MediaPlaybackService.ActionPlayPause.GetHashCode(),
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (pending is null)
        {
            return null;
        }

        var icon = Icon.CreateWithResource(
            activity,
            playing
                ? global::Android.Resource.Drawable.IcMediaPause
                : global::Android.Resource.Drawable.IcMediaPlay);
        return icon is null ? null : new RemoteAction(icon, "Play/Pause", "Play/Pause", pending);
    }
}
