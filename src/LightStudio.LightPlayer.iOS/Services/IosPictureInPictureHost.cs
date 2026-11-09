using System;
using System.ComponentModel;
using System.Linq;
using AVFoundation;
using AVKit;
using Avalonia.Threading;
using CoreAnimation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.LightPlayer.iOS.Playback;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;
using UIKit;
using PlaybackEngine = LightMediaRenderer.Avalonia.Pipeline.MediaPlayer;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>
/// iOS picture in picture through AVKit's sample buffer content source. The system's floating
/// window shows an <see cref="AVSampleBufferDisplayLayer"/> fed by
/// <see cref="PictureInPictureVideoSink"/>, which takes over as the player's video sink while the
/// window is up, so the clock, the audio and the decoders carry on untouched.
/// </summary>
/// <remarks>
/// The layer sits in the key window behind the app's own view, where the system animates the
/// window from. Leaving the app while a video runs opens the window without being asked, which
/// <see cref="Offer"/> arms.
/// </remarks>
internal sealed class IosPictureInPictureHost : IPictureInPictureHost
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a picture is given to reach the screen before iOS 17.4, which cannot tell: the
    /// layer takes a sample a frame or two before it has an image the window can be sized from.
    /// </summary>
    private static readonly TimeSpan LegacySettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly bool supported = AVPictureInPictureController.IsPictureInPictureSupported;

    private UIView? container;
    private AVSampleBufferDisplayLayer? layer;
    private CMTimebase? timebase;
    private PictureInPictureVideoSink? sink;
    private AVPictureInPictureController? controller;
    private ControllerDelegate? controllerDelegate;
    private PlaybackDelegate? playbackDelegate;
    private IDisposable? possibleObserver;
    private NSObject? readyObserver;
    private NSObject? resignObserver;

    /// <summary>The page offering the picture, or showing it in the window.</summary>
    private VideoPlayerPageViewModel? model;

    /// <summary>The player whose frames go to the window, set only while they do.</summary>
    private PlaybackEngine? attached;

    private double aspectRatio;
    private bool offered;
    private bool starting;
    private bool startRequested;
    private bool active;
    private bool stopRequested;
    private bool restoring;
    private bool imageSettled;
    private int attempt;

    public PictureInPictureKind Kind => PictureInPictureKind.DetachedView;

    public bool IsAvailable => supported;

    public bool IsActive => active;

    public event Action<bool>? ActiveChanged;

    public void Enter(PictureInPictureRequest request)
    {
        if (!supported || active || starting)
            return;

        if (request.DataContext is not VideoPlayerPageViewModel page || !Prepare(page, request.AspectRatio))
        {
            ActiveChanged?.Invoke(false);
            return;
        }

        starting = true;
        int current = ++attempt;
        Attach();

        // The system sizes its window from the picture the layer shows, and refuses to open one with
        // nothing in it. A controller made just now also reports itself possible only a moment
        // later; whichever comes last starts the window, and one that never comes is given up on.
        StartWhenReady();
        DispatcherTimer.RunOnce(() =>
        {
            if (starting && !active && current == attempt)
            {
                AppLog.Warning("iOS.PictureInPicture", "The window did not become available.");
                Finish();
            }
        }, StartTimeout);
    }

    public void Exit()
    {
        if (active)
        {
            stopRequested = true;
            controller?.StopPictureInPicture();
        }
        else if (starting)
        {
            Finish();
        }
    }

    public void Offer(PictureInPictureRequest? request)
    {
        if (!supported)
            return;

        offered = request?.DataContext is VideoPlayerPageViewModel page && Prepare(page, request.AspectRatio);
        if (controller is not null)
            controller.CanStartPictureInPictureAutomaticallyFromInline = offered;

        if (active || starting)
            return;

        if (offered)
        {
            Prime();
        }
        else
        {
            StopSink(keepImage: false);
            SetModel(null);
        }
    }

    /// <summary>
    /// Shows the frame on screen in the layer behind the page, ready for the window the system opens
    /// by itself when the app is left.
    /// </summary>
    private void Prime()
    {
        if (sink is null || model?.Player.CaptureFrame() is not { } current)
            return;

        try
        {
            sink.Prime(current);
        }
        finally
        {
            current.Release();
        }
    }

    /// <summary>Makes the controller on first use and points it at <paramref name="page"/>.</summary>
    private bool Prepare(VideoPlayerPageViewModel page, double aspect)
    {
        if (FindWindow() is not { } window)
            return false;

        if (controller is null)
            Create();

        SetModel(page);
        aspectRatio = aspect;
        Place(window);
        return true;
    }

    private void Create()
    {
        // The system refuses the window to an app that is not set up to play in the background.
        if (AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback) is { } error)
            AppLog.Warning("iOS.PictureInPicture", $"Could not configure the audio session: {error.LocalizedDescription}");

        timebase = new CMTimebase(null, CMClock.HostTimeClock)
        {
            Time = CMTime.Zero,
            Rate = 0,
        };
        layer = new AVSampleBufferDisplayLayer { ControlTimebase = timebase };
        container = new UIView { UserInteractionEnabled = false, BackgroundColor = UIColor.Clear };
        container.Layer.AddSublayer(layer);
        sink = new PictureInPictureVideoSink(layer, timebase);

        playbackDelegate = new PlaybackDelegate(this);
        controllerDelegate = new ControllerDelegate(this);
        controller = new AVPictureInPictureController(
            new AVPictureInPictureControllerContentSource(layer, playbackDelegate))
        {
            Delegate = controllerDelegate,
        };
        possibleObserver = controller.AddObserver(
            "pictureInPicturePossible",
            NSKeyValueObservingOptions.New,
            _ => Dispatcher.UIThread.Post(StartWhenReady));
        if (OperatingSystem.IsIOSVersionAtLeast(17, 4))
        {
            readyObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                AVSampleBufferDisplayLayer.DisplayLayerReadyForDisplayDidChangeNotification,
                _ => Dispatcher.UIThread.Post(StartWhenReady),
                layer);
        }
        else
        {
            sink.ImageShown += () => DispatcherTimer.RunOnce(() =>
            {
                imageSettled = true;
                StartWhenReady();
            }, LegacySettleDelay);
        }

        // Leaving the app is what opens the window by itself, so the layer is brought up to date
        // with the page just before.
        resignObserver = UIApplication.Notifications.ObserveWillResignActive((_, _) =>
        {
            if (offered && !active && !starting)
                Prime();
        });
    }

    /// <summary>
    /// Puts the layer where the picture would fill the window, behind everything the app draws, so
    /// the system's animation starts from about where the page shows the video.
    /// </summary>
    private void Place(UIWindow window)
    {
        if (container is null || layer is null)
            return;

        if (!ReferenceEquals(container.Superview, window))
        {
            container.RemoveFromSuperview();
            window.InsertSubview(container, 0);
        }

        CGRect bounds = window.Bounds;
        CGRect frame = bounds;
        if (aspectRatio > 0 && bounds.Width > 0 && bounds.Height > 0)
        {
            double width = Math.Min(bounds.Width, bounds.Height * aspectRatio);
            double height = width / aspectRatio;
            frame = new CGRect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
        }

        container.Frame = frame;
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        layer.Frame = container.Bounds;
        CATransaction.Commit();
    }

    private void SetModel(VideoPlayerPageViewModel? page)
    {
        if (ReferenceEquals(model, page))
            return;

        if (model is not null)
            model.PropertyChanged -= OnModelPropertyChanged;
        model = page;
        if (model is not null)
            model.PropertyChanged += OnModelPropertyChanged;

        UpdatePlaybackState();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoPlayerPageViewModel.IsPlaying) or nameof(VideoPlayerPageViewModel.Speed) or
            nameof(VideoPlayerPageViewModel.DurationSeconds) or nameof(VideoPlayerPageViewModel.IsLiveStream) or
            nameof(VideoPlayerPageViewModel.CanSeek))
        {
            UpdatePlaybackState();
        }
    }

    /// <summary>Tells the window's controls about playing, pausing, speed and duration.</summary>
    private void UpdatePlaybackState()
    {
        if (timebase is not null)
            timebase.Rate = model is { IsPlaying: true } page ? page.Player.Speed : 0;

        if (controller is null)
            return;

        controller.RequiresLinearPlayback = model is { CanSeek: false };
        controller.InvalidatePlaybackState();
    }

    private void StartWhenReady()
    {
        if (starting && !startRequested && !active && IsLayerReady() &&
            controller is { PictureInPicturePossible: true })
        {
            startRequested = true;
            controller.StartPictureInPicture();
        }
    }

    private bool IsLayerReady() => OperatingSystem.IsIOSVersionAtLeast(17, 4)
        ? layer is { ReadyForDisplay: true }
        : imageSettled;

    private void StopSink(bool keepImage)
    {
        sink?.Stop(keepImage);
        if (!keepImage)
            imageSettled = false;
    }

    /// <summary>Moves the page's player over to the window, and shows the frame it is on.</summary>
    private void Attach()
    {
        if (model is null || sink is null || attached is not null)
            return;

        PlaybackEngine player = model.Player;
        attached = player;
        sink.Start();
        player.VideoSink = sink;

        // Device resizing follows the page's size, which means nothing to the window.
        player.RequestVideoOutput(null);
        Represent(player, sink);
        UpdatePlaybackState();
    }

    /// <summary>
    /// Gives the frames back. The page claims the sink on hearing that the window closed; when no
    /// page is left to, the player is left without one.
    /// </summary>
    private void Detach()
    {
        StopSink(keepImage: offered);
        if (attached is not { } player)
            return;

        attached = null;
        if (ReferenceEquals(player.VideoSink, sink))
            player.VideoSink = null;
        else if (player.VideoSink is { } next)
            Represent(player, next);
    }

    /// <summary>A paused player presents nothing new, so a sink that just took over starts from the frame on screen.</summary>
    private static void Represent(PlaybackEngine player, IVideoSink target)
    {
        if (player.CaptureFrame() is not { } current)
            return;

        try
        {
            target.Present(current);
        }
        finally
        {
            current.Release();
        }
    }

    private void OnWillStart()
    {
        starting = false;
        startRequested = false;
        if (active)
            return;

        active = true;
        stopRequested = false;
        restoring = false;

        // Entered by leaving the app rather than by the page's button: nothing is attached yet.
        Attach();
        ActiveChanged?.Invoke(true);
    }

    private void OnFailedToStart(NSError error)
    {
        AppLog.Warning("iOS.PictureInPicture", $"The window could not be opened: {error.LocalizedDescription} ({error.Domain} {error.Code}).");
        Finish();
    }

    private void OnDidStop()
    {
        // Closed with the window's own button: playback stops with it, as it does for the system's
        // players. Coming back to the app, or the page asking, carries on.
        PlaybackEngine? pause = !stopRequested && !restoring ? model?.Player : null;
        Finish();
        pause?.Pause();
    }

    private void Finish()
    {
        if (!starting && !active && attached is null)
            return;

        starting = false;
        startRequested = false;
        active = false;
        stopRequested = false;
        restoring = false;
        ActiveChanged?.Invoke(false);
        Detach();
        if (!offered)
            SetModel(null);
    }

    private CMTimeRange GetTimeRange()
    {
        if (model is not { IsLiveStream: false } page || page.SeekMaximumSeconds <= page.SeekMinimumSeconds)
            return new CMTimeRange { Start = CMTime.NegativeInfinity, Duration = CMTime.PositiveInfinity };

        return new CMTimeRange
        {
            Start = CMTime.FromSeconds(page.SeekMinimumSeconds, 90000),
            Duration = CMTime.FromSeconds(page.SeekMaximumSeconds - page.SeekMinimumSeconds, 90000),
        };
    }

    private void SetPlaying(bool playing)
    {
        if (model?.Player is not { } player)
            return;

        if (playing)
            player.Play();
        else
            player.Pause();
    }

    private static UIWindow? FindWindow()
    {
        UIWindowScene[] scenes = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
            .OrderByDescending(scene => scene.ActivationState == UISceneActivationState.ForegroundActive)
            .ToArray();
        return scenes.SelectMany(scene => scene.Windows).FirstOrDefault(window => window.IsKeyWindow)
            ?? scenes.SelectMany(scene => scene.Windows).FirstOrDefault();
    }

    private sealed class ControllerDelegate(IosPictureInPictureHost host) : AVPictureInPictureControllerDelegate
    {
        public override void WillStartPictureInPicture(AVPictureInPictureController pictureInPictureController) =>
            host.OnWillStart();

        public override void FailedToStartPictureInPicture(AVPictureInPictureController pictureInPictureController, NSError error) =>
            host.OnFailedToStart(error);

        public override void RestoreUserInterfaceForPictureInPicture(
            AVPictureInPictureController pictureInPictureController,
            Action<bool> completionHandler)
        {
            // The page is still there behind the window; it only has to take the frames back,
            // which it does once the window has closed.
            host.restoring = true;
            completionHandler(true);
        }

        public override void DidStopPictureInPicture(AVPictureInPictureController pictureInPictureController) =>
            host.OnDidStop();
    }

    private sealed class PlaybackDelegate(IosPictureInPictureHost host) : AVPictureInPictureSampleBufferPlaybackDelegate
    {
        public override void SetPlaying(AVPictureInPictureController pictureInPictureController, bool playing) =>
            host.SetPlaying(playing);

        public override CMTimeRange GetTimeRange(AVPictureInPictureController pictureInPictureController) =>
            host.GetTimeRange();

        public override bool IsPlaybackPaused(AVPictureInPictureController pictureInPictureController) =>
            host.model is not { IsPlaying: true };

        public override void DidTransitionToRenderSize(AVPictureInPictureController pictureInPictureController, CMVideoDimensions newRenderSize)
        {
        }

        public override void SkipByInterval(AVPictureInPictureController pictureInPictureController, CMTime skipInterval, Action completionHandler)
        {
            host.model?.SeekRelative(skipInterval.Seconds);
            completionHandler();
        }
    }
}
