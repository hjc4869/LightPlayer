namespace LightStudio.LightPlayer.Services;

/// <summary>
/// How the Linux desktop head picks its windowing backend. Only meaningful inside a Wayland
/// session; elsewhere the platform detection runs unchanged.
/// </summary>
public enum WaylandMode
{
    /// <summary>Run under X11 (XWayland) even when the session is Wayland.</summary>
    Off,

    /// <summary>Use the Wayland backend when the session reports one.</summary>
    Auto,
}
