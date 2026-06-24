using System;
#if LINUX
using LightStudio.LightPlayer.Services.Playback.Mpris;
#endif

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
#if LINUX
    private const string Identity = "LightPlayer";
#endif

    public static ISystemMediaControls Create()
    {
        try
        {
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                // The instance suffix keeps the bus name unique per process so
                // multiple running copies don't collide on the well-known name.
                var suffix = $"LightStudioLightPlayer.instance{Environment.ProcessId}";
                return new MprisSystemMediaControls(Identity, suffix);
            }
#endif

#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                // Placeholder for Windows System Media Transport Controls (SMTC),
                // e.g. via Windows.Media.SystemMediaTransportControls.
                return new NoopSystemMediaControls();
            }
#endif

#if MACOS
            if (OperatingSystem.IsMacOS())
            {
                // Placeholder for macOS Now Playing (MPNowPlayingInfoCenter /
                // MPRemoteCommandCenter).
                return new NoopSystemMediaControls();
            }
#endif

            return new NoopSystemMediaControls();
        }
        catch
        {
            // Optional integration: never let media-control setup break playback.
            return new NoopSystemMediaControls();
        }
    }
}
