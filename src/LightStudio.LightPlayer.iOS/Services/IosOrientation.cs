using System;
using System.Linq;
using System.Runtime.Versioning;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>
/// Keeps the shell's own pages upright on a display too short for them (<see cref="ShellOrientation"/>),
/// turns the interface to the orientation that fits a picture, and holds it there until the picture
/// is done with or the device is turned the other way.
/// </summary>
/// <remarks>
/// The app delegate reports <see cref="SupportedOrientations"/>, so the device's own rotation does
/// not undo a held orientation, and a scene geometry update turns the interface even under the
/// system's rotation lock. Turning the device to the other orientation family ends the hold through
/// <see cref="PlatformServices.FullScreenChanged"/>, as a host leaving full screen on its own does; so
/// does the scene moving to a display of another size, such as a foldable's other display. Once a
/// turn has reached its orientations, the scene is asked for all of the app's orientations, so the
/// turn does not hold the scene on a display that refuses turns.
/// </remarks>
internal static class IosOrientation
{
    private static readonly UIInterfaceOrientationMask defaultOrientations = ReadSupportedOrientations();
    private static UIInterfaceOrientationMask? held;
    private static UIInterfaceOrientationMask before;
    private static NSObject? deviceObserver;
    private static UIWindowScene? observedScene;
    private static IDisposable? geometryObserver;
    private static CGSize display;
    private static bool turnsRefused;
    private static bool presentingMedia;

    // The orientations of an accepted turn until the interface has reached them.
    private static UIInterfaceOrientationMask turning;

    // Whether the device has been held the way the interface is held since the hold began; only then
    // does turning it the other way mean the hold should end.
    private static bool deviceFollowedHold;

    /// <summary>
    /// The orientations the app allows right now: the held ones while a picture holds the display,
    /// all of its own while a photo or video shows or the display fits the shell either way, and
    /// otherwise the upright ones.
    /// </summary>
    public static UIInterfaceOrientationMask SupportedOrientations =>
        presentingMedia ? held ?? defaultOrientations : UprightOrientations ?? defaultOrientations;

    /// <summary>
    /// Whether the scene lets the app turn it. Windowed scenes, as on iPadOS and the iPhone Duo's
    /// inner display, refuse programmatic orientation changes; <see cref="Observe"/> finds that out
    /// for every display the scene is shown on.
    /// </summary>
    public static bool CanTurn => OperatingSystem.IsIOSVersionAtLeast(16) && !turnsRefused && Scene is { SizeRestrictions: null };

    /// <summary>
    /// Follows the device and the scene's display from its activation on. Each display the scene is
    /// shown on gets the shell settled for its size and is asked for all of the app's orientations,
    /// which a windowed scene refuses like any other turn, so the refusal is known before a picture
    /// offers to turn.
    /// </summary>
    public static void Observe()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            return;
        }

        UIDevice.CurrentDevice.BeginGeneratingDeviceOrientationNotifications();
        NSNotificationCenter.DefaultCenter.AddObserver(UIScene.DidActivateNotification, notification =>
        {
            if (!OperatingSystem.IsIOSVersionAtLeast(16) || notification.Object is not UIWindowScene scene)
            {
                return;
            }

            if (!ReferenceEquals(observedScene, scene))
            {
                geometryObserver?.Dispose();
                observedScene = scene;
                geometryObserver = scene.AddObserver("effectiveGeometry", NSKeyValueObservingOptions.New, _ =>
                {
                    if (OperatingSystem.IsIOSVersionAtLeast(16))
                    {
                        OnGeometryChanged(scene);
                    }
                });
            }

            OnGeometryChanged(scene);
        });
    }

    /// <summary>
    /// Lets the interface turn with the device, and <see cref="Request"/> turn it, while a photo or
    /// video covers the shell; otherwise the shell's pages are kept upright where the display needs it.
    /// </summary>
    public static void SetMediaPresentation(bool value)
    {
        if (presentingMedia == value)
        {
            return;
        }

        presentingMedia = value;
        if (OperatingSystem.IsIOSVersionAtLeast(16))
        {
            // Once the app has turned the interface itself, UIKit no longer follows a device that has
            // not moved since, so a photo or video turns to the way the device is held.
            if (Scene is { } scene)
            {
                Settle(scene, value ? Family(UIDevice.CurrentDevice.Orientation) : 0);
            }
        }
        else
        {
            UIViewController.AttemptRotationToDeviceOrientation();
        }
    }

    /// <summary>
    /// Holds the orientation family that fits a picture of the given width/height ratio, or releases
    /// the hold when the ratio is 0.
    /// </summary>
    public static void Request(double aspectRatio)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16) || Scene is not { } scene)
        {
            return;
        }

        if (aspectRatio <= 0)
        {
            Release(scene);
            return;
        }

        var target = (aspectRatio > 1 ? UIInterfaceOrientationMask.Landscape : UIInterfaceOrientationMask.Portrait) & defaultOrientations;
        if (target == 0 || held == target)
        {
            return;
        }

        var current = scene.EffectiveGeometry.InterfaceOrientation;
        if (held is null)
        {
            before = Family(current);
        }

        held = target;
        UpdateSupportedOrientations(scene);
        if (!Matches(current, target))
        {
            Turn(scene, target);
        }

        if (deviceObserver is null)
        {
            deviceObserver = NSNotificationCenter.DefaultCenter.AddObserver(UIDevice.OrientationDidChangeNotification, OnDeviceOrientationChanged);
        }

        deviceFollowedHold = Family(UIDevice.CurrentDevice.Orientation) == target;
    }

    // The orientations that run the display's long edge vertically, while the display is too short
    // for the shell's pages to lie across it.
    private static UIInterfaceOrientationMask? UprightOrientations
    {
        get
        {
            var size = (Scene?.Screen ?? UIScreen.MainScreen).FixedCoordinateSpace.Bounds.Size;
            if (!ShellOrientation.HoldsUpright(size.Width, size.Height))
            {
                return null;
            }

            var upright = (size.Height >= size.Width ? UIInterfaceOrientationMask.Portrait : UIInterfaceOrientationMask.Landscape) & defaultOrientations;
            return upright != 0 ? upright : null;
        }
    }

    [SupportedOSPlatform("ios16.0")]
    private static void Release(UIWindowScene scene)
    {
        if (held is not { } wasHeld)
        {
            return;
        }

        var device = Family(UIDevice.CurrentDevice.Orientation);
        EndHold();

        // Under the system's rotation lock the interface would stay turned, so it is turned back to
        // the way the device is held, or to the way it was before, unless the device is held the
        // turned way.
        Settle(scene, Matches(scene.EffectiveGeometry.InterfaceOrientation, wasHeld) && device != wasHeld
            ? device != 0 ? device : before
            : 0);
    }

    private static void EndHold()
    {
        held = null;
        if (deviceObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(deviceObserver);
            deviceObserver = null;
        }
    }

    // A hold was made for the display the scene was on, so another display ends it, and is asked
    // anew whether the scene can be turned there.
    [SupportedOSPlatform("ios16.0")]
    private static void OnGeometryChanged(UIWindowScene scene)
    {
        var size = scene.Screen.FixedCoordinateSpace.Bounds.Size;
        if (size == display)
        {
            if (turning != 0 && Matches(scene.EffectiveGeometry.InterfaceOrientation, turning))
            {
                turning = 0;
                Loosen(scene);
            }

            return;
        }

        display = size;
        turnsRefused = false;
        turning = 0;
        AppLog.Information("iOS.Orientation", $"The scene is on a {size.Width}x{size.Height} display.");
        if (held is not null)
        {
            EndHold();
            PlatformServices.FullScreenChanged?.Invoke(false);
        }

        Settle(scene, probe: true);
    }

    // Turns the interface into the orientations allowed now when it is outside them, toward the
    // preferred family where that is allowed. A probe loosens the orientations otherwise.
    [SupportedOSPlatform("ios16.0")]
    private static void Settle(UIWindowScene scene, UIInterfaceOrientationMask preferred = 0, bool probe = false)
    {
        UpdateSupportedOrientations(scene);
        var allowed = SupportedOrientations;
        var current = scene.EffectiveGeometry.InterfaceOrientation;
        var target = (preferred & allowed) != 0 ? preferred & allowed : allowed;
        if (!Matches(current, target))
        {
            Turn(scene, target);
        }
        else if (probe)
        {
            Loosen(scene);
        }
    }

    // Asks for all of the app's orientations, which the supported ones narrow to those allowed now.
    // The scene keeps the orientations of the last request it accepted, and a windowed display
    // refuses new ones, so a narrower request made on another display would hold the scene there.
    [SupportedOSPlatform("ios16.0")]
    private static void Loosen(UIWindowScene scene) => Turn(scene, defaultOrientations, probe: true);

    [SupportedOSPlatform("ios16.0")]
    private static void OnDeviceOrientationChanged(NSNotification notification)
    {
        // Turning the device away from the held family, once it has followed it, is how the hold is left.
        var device = Family(UIDevice.CurrentDevice.Orientation);
        if (held is not { } mask || device == 0)
        {
            return;
        }

        if (device == mask)
        {
            deviceFollowedHold = true;
        }
        else if (deviceFollowedHold)
        {
            PlatformServices.FullScreenChanged?.Invoke(false);
        }
    }

    [SupportedOSPlatform("ios16.0")]
    private static void UpdateSupportedOrientations(UIWindowScene scene)
    {
        foreach (var window in scene.Windows)
        {
            window.RootViewController?.SetNeedsUpdateOfSupportedInterfaceOrientations();
        }
    }

    [SupportedOSPlatform("ios16.0")]
    private static void Turn(UIWindowScene scene, UIInterfaceOrientationMask orientations, bool probe = false)
    {
        if (orientations == 0)
        {
            return;
        }

        if (!probe)
        {
            turning = orientations;
        }

        scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(orientations), error => DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            turnsRefused = true;
            if (probe)
            {
                AppLog.Information("iOS.Orientation", $"The scene cannot be turned: {error.LocalizedDescription}");
                return;
            }

            if (turning == orientations)
            {
                turning = 0;
            }

            // A refused turn leaves nothing held; the picture's host finds out the way it does when a
            // host leaves full screen on its own.
            AppLog.Warning("iOS.Orientation", $"Could not turn the interface: {error.LocalizedDescription}");
            PlatformServices.FullScreenChanged?.Invoke(false);
        }));
    }

    private static UIWindowScene? Scene =>
        UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
            .FirstOrDefault(scene => scene.ActivationState == UISceneActivationState.ForegroundActive)
        ?? UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().FirstOrDefault();

    private static bool Matches(UIInterfaceOrientation orientation, UIInterfaceOrientationMask mask) =>
        orientation switch
        {
            UIInterfaceOrientation.Portrait => mask.HasFlag(UIInterfaceOrientationMask.Portrait),
            UIInterfaceOrientation.PortraitUpsideDown => mask.HasFlag(UIInterfaceOrientationMask.PortraitUpsideDown),
            UIInterfaceOrientation.LandscapeLeft => mask.HasFlag(UIInterfaceOrientationMask.LandscapeLeft),
            UIInterfaceOrientation.LandscapeRight => mask.HasFlag(UIInterfaceOrientationMask.LandscapeRight),
            _ => false,
        };

    // The portrait or landscape family the device is held in, or 0 while it lies flat or is unknown.
    private static UIInterfaceOrientationMask Family(UIDeviceOrientation orientation) =>
        orientation switch
        {
            UIDeviceOrientation.Portrait or UIDeviceOrientation.PortraitUpsideDown => UIInterfaceOrientationMask.Portrait,
            UIDeviceOrientation.LandscapeLeft or UIDeviceOrientation.LandscapeRight => UIInterfaceOrientationMask.Landscape,
            _ => 0,
        };

    private static UIInterfaceOrientationMask Family(UIInterfaceOrientation orientation) =>
        orientation switch
        {
            UIInterfaceOrientation.Portrait or UIInterfaceOrientation.PortraitUpsideDown => UIInterfaceOrientationMask.Portrait,
            UIInterfaceOrientation.LandscapeLeft or UIInterfaceOrientation.LandscapeRight => UIInterfaceOrientationMask.Landscape,
            _ => 0,
        };

    // The orientations Info.plist allows, which UIKit no longer consults once the delegate answers.
    private static UIInterfaceOrientationMask ReadSupportedOrientations()
    {
        var bundle = NSBundle.MainBundle;
        var list = (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
                ? bundle.ObjectForInfoDictionary("UISupportedInterfaceOrientations~ipad") as NSArray
                : null)
            ?? bundle.ObjectForInfoDictionary("UISupportedInterfaceOrientations") as NSArray;
        if (list is null)
        {
            return UIInterfaceOrientationMask.AllButUpsideDown;
        }

        UIInterfaceOrientationMask mask = 0;
        for (nuint index = 0; index < list.Count; index++)
        {
            mask |= list.GetItem<NSString>(index)?.ToString() switch
            {
                "UIInterfaceOrientationPortrait" => UIInterfaceOrientationMask.Portrait,
                "UIInterfaceOrientationPortraitUpsideDown" => UIInterfaceOrientationMask.PortraitUpsideDown,
                "UIInterfaceOrientationLandscapeLeft" => UIInterfaceOrientationMask.LandscapeLeft,
                "UIInterfaceOrientationLandscapeRight" => UIInterfaceOrientationMask.LandscapeRight,
                _ => 0,
            };
        }

        return mask == 0 ? UIInterfaceOrientationMask.AllButUpsideDown : mask;
    }
}
