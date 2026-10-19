using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Frames menu windows the way Windows 11 frames its own menus: small rounded corners and the compact
/// shadow of transient surfaces, without the system border, which the menu draws itself.
/// </summary>
/// <remarks>
/// The small rounding stays inside the menu's own larger corners, so only its shadow shows; the regular
/// rounding comes with the much larger shadow of a top-level window. Avalonia composes the blur behind the
/// window as a rectangle, which the platform options clip to the menu's corners when the window's first
/// frame is composed.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsMenuFrame
{
    private const uint WindowCornerPreferenceAttribute = 33;
    private const uint BorderColorAttribute = 34;
    private const int SmallRoundedCorners = 3;
    private const int NoBorderColor = unchecked((int)0xFFFFFFFE);

    public static void Apply(Win32PlatformOptions options, IntPtr windowHandle, double cornerRadius)
    {
        options.WinUICompositionBackdropCornerRadius = (float)cornerRadius;

        var corners = SmallRoundedCorners;
        DwmSetWindowAttribute(windowHandle, WindowCornerPreferenceAttribute, ref corners, sizeof(int));
        var border = NoBorderColor;
        DwmSetWindowAttribute(windowHandle, BorderColorAttribute, ref border, sizeof(int));
    }

    [LibraryImport("dwmapi")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
}
