using System;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// Selects the per-OS <see cref="ISystemMediaControls"/> implementation. Linux
/// gets MPRIS over D-Bus; Windows and macOS fall back to a no-op until their
/// native integrations are wired in (see the placeholders below). Any failure
/// degrades to <see cref="NoopSystemMediaControls"/> so playback never depends
/// on the optional OS integration being present.
/// </summary>
internal static class SystemMediaControlsFactory
{
    private static Func<ISystemMediaControls> SmcFactory = () => new NoopSystemMediaControls();

    public static void Register(Func<ISystemMediaControls> smcFactory)
    {
        SmcFactory = smcFactory;
    }

    public static ISystemMediaControls Create()
    {
        try
        {
            return SmcFactory();
        }
        catch (Exception ex)
        {
            // Optional integration: never let media-control setup break playback.
            AppLog.Warning("SystemMediaControls", "Could not create system media controls.", ex);
            return new NoopSystemMediaControls();
        }
    }
}
