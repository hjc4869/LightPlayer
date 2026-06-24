using System;
#if LINUX
using LightStudio.LightPlayer.Services.SingleInstance;
#endif

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Selects the per-OS <see cref="ISingleInstanceService"/> implementation. Linux
/// uses a D-Bus well-known name (which works inside Flatpak, whose sandbox always
/// lets an app own its own app id on the session bus); Windows and macOS fall
/// back to a no-op until native single-instance support is added. Any failure
/// degrades to the no-op so the app always starts.
/// </summary>
internal static class SingleInstanceServiceFactory
{
    public static ISingleInstanceService Create()
    {
        try
        {
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                return new DBusSingleInstanceService(AppInfo.AppId, AppInfo.ObjectPath);
            }
#endif
            return new NoopSingleInstanceService();
        }
        catch
        {
            return new NoopSingleInstanceService();
        }
    }
}
