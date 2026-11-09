using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// When touch heads keep the shell's own pages upright: on a display with an edge shorter than the
/// shell's minimum height, they stay in the orientation that runs the display's long edge vertically.
/// </summary>
public static class ShellOrientation
{
    /// <summary>The least height the shell's layout fits, which the desktop window keeps as its minimum.</summary>
    public const double MinimumHeight = 600;

    /// <summary>Whether a display of the given size in DIPs keeps the shell's pages upright.</summary>
    public static bool HoldsUpright(double width, double height) =>
        width > 0 && height > 0 && Math.Min(width, height) < MinimumHeight;
}
