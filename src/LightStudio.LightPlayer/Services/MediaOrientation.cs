using System;
using Avalonia;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// How the full screen control of the photo and video views behaves on touch heads, where the
/// picture already covers the window: it turns the display to the orientation that fits the picture.
/// </summary>
public static class MediaOrientation
{
    /// <summary>Whether the full screen control turns the display rather than covering the window.</summary>
    public static bool TurnsDisplay =>
        PlatformServices.UsesTouchChrome && PlatformServices.PreferredOrientationRequested is not null;

    /// <summary>
    /// Whether the control is offered for a picture of the aspect ratio: the OS lets the app turn the
    /// display, and the picture would show noticeably larger with the viewport's sides swapped.
    /// </summary>
    public static bool OffersTurning(Size viewport, double aspectRatio)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || !double.IsFinite(aspectRatio) || aspectRatio <= 0 ||
            PlatformServices.CanTurnDisplay?.Invoke() == false)
        {
            return false;
        }

        return FitArea(viewport.Height, viewport.Width, aspectRatio) > FitArea(viewport.Width, viewport.Height, aspectRatio) * 1.05;
    }

    private static double FitArea(double width, double height, double aspectRatio)
    {
        double fitted = Math.Min(width, height * aspectRatio);
        return fitted * fitted / aspectRatio;
    }
}
