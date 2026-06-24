namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Centralized application identity used for OS integrations (the single-instance
/// D-Bus well-known name, the Flatpak app id, and the desktop entry). Keep this in
/// sync with the Flatpak manifest, desktop file, and metainfo under
/// <c>packaging/flatpak</c>.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Reverse-DNS application id. Doubles as the Flatpak app id and the D-Bus
    /// well-known name the primary instance owns on the session bus.
    /// </summary>
    public const string AppId = "im.hjc.LightPlayer";

    /// <summary>
    /// D-Bus object path derived from <see cref="AppId"/> (dots replaced by
    /// slashes, leading slash added), as required by <c>org.freedesktop.Application</c>.
    /// </summary>
    public const string ObjectPath = "/com/lightstudio/LightPlayer";
}
