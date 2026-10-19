#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android;

[Activity(
    Label = "@string/app_name",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ResizeableActivity = true,
    SupportsPictureInPicture = true,
    EnableOnBackInvokedCallback = true,
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize
                           | ConfigChanges.UiMode)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "audio/*")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "video/*")]
// Offered for every picture rather than the HDR ones alone: which of them decode depends on the
// FFmpeg this build ships, and a chooser entry that sometimes fails beats one that never appears.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "image/*")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "application/x-cue")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "application/x-matroska")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "application/vnd.rn-realmedia-vbr")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = RemoteApiPairingUri.Scheme,
    DataHost = RemoteApiPairingUri.Host)]
public class MainActivity : AvaloniaMainActivity
{
    private const int RuntimePermissionRequestCode = 0x4C50;

    private readonly SemaphoreSlim pairingIntentGate = new(1, 1);
    private TaskCompletionSource? permissionCompletion;
    private Services.AndroidPictureInPictureHost? pictureInPicture;
    private Services.AndroidBackNavigation? backNavigation;

    internal static MainActivity? Current { get; private set; }

    public MainActivity()
    {
        // Android raises Activated when the app is launched or resumed to open a file the user
        // picked through "Open with". Subscribe in the constructor (before OnCreate) so the initial
        // launch activation is not missed.
        ((IAvaloniaActivity)this).Activated += OnActivated;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);

        // Route ContextFlyout menus to android.widget.PopupMenu: Avalonia's MenuFlyout opens under
        // the finger with mouse-sized rows, while the platform widget anchors to the row and follows
        // the device theme and touch-target metrics.
        NativeContextMenu.Install(new Services.AndroidNativeContextMenu(this));

        PlatformServices.FullScreenRequested = SetFullScreen;
        PlatformServices.PreferredOrientationRequested = SetPreferredOrientation;
        PlatformServices.KeepScreenOnRequested = SetKeepScreenOn;
        pictureInPicture = new Services.AndroidPictureInPictureHost(this);
        PlatformServices.PictureInPicture = pictureInPicture;

        RequestRuntimePermissions();
        HandlePairingIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandlePairingIntent(intent);
    }

    /// <summary>
    /// Reports the system shrinking the app into a floating window, or restoring it. Entering the
    /// mode is as often the platform's own doing (the user leaving the app while a video plays) as
    /// the page's, and leaving it is always the user's, through the window's own frame.
    /// </summary>
    public override void OnPictureInPictureModeChanged(
        bool isInPictureInPictureMode,
        global::Android.Content.Res.Configuration? newConfig)
    {
        base.OnPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        pictureInPicture?.OnModeChanged(isInPictureInPictureMode);
    }

    /// <summary>
    /// Keeps the display lit while video plays. The window flag is preferred over a wake lock: it
    /// needs no permission and Android drops it by itself when the activity leaves the foreground.
    /// </summary>
    private void SetKeepScreenOn(bool value)
    {
        if (value)
        {
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            Window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        }
    }

    /// <summary>
    /// Hides the status and navigation bars for full-screen video, letting the picture use the
    /// whole display, and puts them back when it ends.
    /// </summary>
    private void SetFullScreen(bool value)
    {
        var window = Window;
        var controller = window?.InsetsController;
        var attributes = window?.Attributes;
        if (window is null || controller is null || attributes is null)
        {
            return;
        }

        if (value)
        {
            WindowCompat.SetDecorFitsSystemWindows(window, false);
            attributes.LayoutInDisplayCutoutMode = global::Android.Views.LayoutInDisplayCutoutMode.Always;
            window.Attributes = attributes;
            controller.SystemBarsBehavior = (int)global::Android.Views.WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            controller.Hide(global::Android.Views.WindowInsets.Type.SystemBars());
        }
        else
        {
            controller.Show(global::Android.Views.WindowInsets.Type.SystemBars());
            attributes.LayoutInDisplayCutoutMode = global::Android.Views.LayoutInDisplayCutoutMode.Default;
            window.Attributes = attributes;
            WindowCompat.SetDecorFitsSystemWindows(window, true);
        }
    }

    /// <summary>
    /// Rotates the screen to suit the picture: landscape for a video wider than it is tall,
    /// portrait otherwise. A ratio of 0 hands orientation back to the user's device setting.
    /// </summary>
    private void SetPreferredOrientation(double aspectRatio)
    {
        RequestedOrientation = aspectRatio switch
        {
            <= 0 => ScreenOrientation.Unspecified,
            > 1 => ScreenOrientation.SensorLandscape,
            _ => ScreenOrientation.SensorPortrait,
        };
    }

    protected override void OnStart()
    {
        base.OnStart();
        backNavigation = new Services.AndroidBackNavigation(this, DispatchContentTouchEvent);
        OnBackPressedDispatcher.AddCallback(this, backNavigation);
    }

    public override bool DispatchTouchEvent(MotionEvent? motionEvent) =>
        motionEvent is not null && backNavigation is not null
            ? backNavigation.DispatchTouchEvent(motionEvent)
            : base.DispatchTouchEvent(motionEvent);

    private bool DispatchContentTouchEvent(MotionEvent motionEvent) => base.DispatchTouchEvent(motionEvent);

    protected override void OnStop()
    {
        backNavigation?.Stop();
        backNavigation?.Dispose();
        backNavigation = null;
        base.OnStop();
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Android only permits starting the media foreground service while the app is in the
        // foreground, so the media controls defer any start requested earlier (a cold start restores
        // the previous queue from Application.OnCreate, before this activity exists) until here.
        Services.AndroidSystemMediaControls.OnAppForegroundChanged(true);
        (PlatformServices.NativeMediaLibrary as Services.AndroidMediaStoreProvider)?.Refresh();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        permissionCompletion?.TrySetCanceled();
        base.OnDestroy();
    }

    internal async Task RequestLibraryPermissionsAsync(string[] permissions, CancellationToken cancellationToken,
        bool forceRequest = false)
    {
        while (permissionCompletion is { } pending)
        {
            await pending.Task.WaitAsync(cancellationToken);
        }

        var required = forceRequest ? permissions : permissions.Where(permission => CheckSelfPermission(permission) != Permission.Granted).ToArray();
        if (required.Length == 0)
        {
            return;
        }

        permissionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = permissionCompletion.Task;
        RequestPermissions(required, RuntimePermissionRequestCode);
        await completion.WaitAsync(cancellationToken);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == RuntimePermissionRequestCode)
        {
            var completion = permissionCompletion;
            permissionCompletion = null;
            completion?.TrySetResult();
            (PlatformServices.NativeMediaLibrary as Services.AndroidMediaStoreProvider)?.Refresh();
        }
    }

    protected override void OnPause()
    {
        // Picture in picture pauses the activity while leaving it on screen, and a foreground
        // service may still be started from there, so the app does not count as backgrounded yet.
        Services.AndroidSystemMediaControls.OnAppForegroundChanged(IsInPictureInPictureMode);
        base.OnPause();
    }

    /// <summary>
    /// Requests the runtime permissions the app needs on first launch: reading the user's audio and
    /// the cover images stored beside it (scoped storage) and posting the playback notification.
    /// Folder access granted per-tree by the Storage Access Framework picker needs no runtime
    /// permission here; the media permissions cover shared locations scanned by filesystem path.
    /// </summary>
    private void RequestRuntimePermissions()
    {
        var required = new List<string>();

        void Require(string permission)
        {
            if (CheckSelfPermission(permission) != Permission.Granted)
            {
                required.Add(permission);
            }
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var settings = PlatformServices.LocalServicesFactory?.Invoke().Settings.NativeLibrary;
            if (settings is null || settings.UseSystemMusicLibrary || settings.ScanMusicFolders)
                Require(global::Android.Manifest.Permission.ReadMediaAudio);
            if ((settings is null || settings.UseSystemMediaLibrary || settings.ScanMediaFolders) &&
                CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaImages) != Permission.Granted &&
                CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaVideo) != Permission.Granted &&
                CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaVisualUserSelected) != Permission.Granted)
            {
                Require(global::Android.Manifest.Permission.ReadMediaVideo);
                Require(global::Android.Manifest.Permission.ReadMediaImages);
                Require(global::Android.Manifest.Permission.ReadMediaVisualUserSelected);
            }
        }
        else
        {
            Require(global::Android.Manifest.Permission.ReadExternalStorage);
        }

        Require(global::Android.Manifest.Permission.PostNotifications);

        if (required.Count > 0)
        {
            permissionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestPermissions(required.ToArray(), RuntimePermissionRequestCode);
        }
    }

    /// <summary>
    /// Handles an Android "Open with" activation: converts the incoming Storage Access Framework
    /// items to content URIs and forwards them to the shared application, which enqueues and plays
    /// them. Each item's Path is the content URI consumed by the Android media I/O layer.
    /// </summary>
    private static void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileActivated || Avalonia.Application.Current is not App app)
        {
            return;
        }

        var uris = new List<string>(fileActivated.Files.Count);
        foreach (var item in fileActivated.Files)
        {
            if (item.Path is { IsAbsoluteUri: true } uri)
            {
                uris.Add(uri.ToString());
            }
        }

        if (uris.Count > 0)
        {
            app.OpenFiles(uris);
        }
    }

    private async void HandlePairingIntent(Intent? intent)
    {
        if (intent?.Data is not { } data ||
            !string.Equals(data.Scheme, RemoteApiPairingUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var activation = data.ToString();
        intent.SetData(null);
        if (string.IsNullOrEmpty(activation))
        {
            return;
        }

        await pairingIntentGate.WaitAsync();
        try
        {
            var pairing = RemoteApiPairingUri.Parse(new Uri(activation, UriKind.Absolute));
            if (pairing.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                throw new InvalidOperationException("The pairing link has expired.");
            }

            if (global::Android.App.Application.Context is not Application application)
            {
                throw new InvalidOperationException("The Android application is not ready.");
            }

            await application.PairRemoteLibraryAsync(pairing);
            ShowPairingResult(string.Format(
                Strings.Resources.RemoteApiPairing_AndroidSuccess,
                pairing.ServerAddress));
        }
        catch (Exception exception)
        {
            AppLog.Warning("Android.Pairing", "The paired remote library could not be added.", exception);
            ShowPairingResult(Strings.Resources.RemoteApiPairing_AndroidFailed);
        }
        finally
        {
            pairingIntentGate.Release();
        }
    }

    private void ShowPairingResult(string message)
    {
        RunOnUiThread(() => Toast.MakeText(this, message, ToastLength.Long)?.Show());
    }
}
