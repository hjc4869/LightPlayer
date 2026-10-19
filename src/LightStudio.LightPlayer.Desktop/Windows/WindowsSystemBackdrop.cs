using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Hands the window's backdrop to the compositor, as Mica Alt.
/// </summary>
/// <remarks>
/// Avalonia's <c>WindowTransparencyLevel.Mica</c> does not ask the compositor for a backdrop at all:
/// the Win32 backend composes its own approximation from a blurred-wallpaper brush and a light tint
/// brush. That approximation only covers the client area, so it can never match the title bar, which
/// the compositor draws itself, and its tint keeps the chrome brighter than the caption
/// above it. Asking for a plain transparent window instead and setting the backdrop here leaves one
/// material, drawn by the compositor across the caption and the client area alike. Mica Alt
/// (<c>DWMSBT_TABBEDWINDOW</c>) is the deeper, more wallpaper-tinted of the two Mica materials, and
/// the one the page layers above it are tuned against.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSystemBackdrop
{
    private const uint SystemBackdropTypeAttribute = 38;
    private const int TabbedWindowBackdrop = 4;

    /// <summary>
    /// Applies Mica Alt to a window handle, reporting whether the compositor accepted it. A refusal
    /// leaves the caller to fall back to an opaque window.
    /// </summary>
    public static bool Apply(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        // This backdrop covers the entire window while preserving the native caption geometry.
        var backdrop = TabbedWindowBackdrop;
        return DwmSetWindowAttribute(windowHandle, SystemBackdropTypeAttribute, ref backdrop, sizeof(int)) == 0;
    }

    [LibraryImport("dwmapi")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
}
