using System;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Gives a window the title bar of a unified toolbar. An empty <c>NSToolbar</c> makes the title bar
/// as tall as the toolbar the shell draws there and centers the window controls on it, while clicks
/// still reach the content beneath.
/// </summary>
internal static class ToolbarTitleBar
{
    private const nint NSWindowToolbarStyleUnified = 3;

    public static void Apply(IntPtr window, bool enabled)
    {
        if (window == IntPtr.Zero || (ObjC.Send(window, ObjC.Sel("toolbar")) != IntPtr.Zero) == enabled)
        {
            return;
        }

        if (!enabled)
        {
            ObjC.Send(window, ObjC.Sel("setToolbar:"), IntPtr.Zero);
            return;
        }

        var toolbar = ObjC.Send(ObjC.Send(ObjC.GetClass("NSToolbar"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(window, ObjC.Sel("setToolbarStyle:"), NSWindowToolbarStyleUnified);
        ObjC.Send(window, ObjC.Sel("setToolbar:"), toolbar);
        ObjC.Send(toolbar, ObjC.Sel("release"));
    }
}
