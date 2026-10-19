using System;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// Publishes the current track to <c>navigator.mediaSession</c>, so media keys, the browser's own
/// media hub and the OS notification / lock screen drive playback in the browser head.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserSystemMediaControls : ISystemMediaControls
{
    private static readonly byte[] NoArtwork = [];

    public static void Register()
    {
        if (!MediaSessionInterop.Register())
        {
            AppLog.Information("Web.MediaSession", "This browser has no media session; OS media controls stay off.");
            return;
        }

        var controls = new BrowserSystemMediaControls();
        SystemMediaControlsFactory.Register(() => controls);
    }

    private BrowserSystemMediaControls() => _ = ListenAsync();

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public event Action<TimeSpan>? SeekRequested;

    public void SetNowPlaying(string title, string artist, string album) =>
        MediaSessionInterop.SetNowPlaying(title, artist, album);

    public void SetPlaybackState(bool isPlaying, bool hasItem) =>
        MediaSessionInterop.SetPlaybackState(isPlaying, hasItem);

    public void SetArtwork(byte[]? artworkContent) =>
        MediaSessionInterop.SetArtwork(artworkContent ?? NoArtwork);

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration) =>
        MediaSessionInterop.SetPlaybackProgress(position.TotalMilliseconds, duration.TotalMilliseconds);

    /// <summary>
    /// Pulls transport requests one at a time. Awaited as a promise rather than pushed through a
    /// marshalled callback, which multithreaded WebAssembly refuses to invoke from a JS event
    /// handler. The awaits are not configured away from the JS synchronization context, so the loop
    /// stays on the thread that owns the module.
    /// </summary>
    private async Task ListenAsync()
    {
        while (true)
        {
            string action;
            try
            {
                action = await MediaSessionInterop.WaitForActionAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warning("Web.MediaSession", "Transport listener stopped.", ex);
                return;
            }

            switch (action)
            {
                case "playpause":
                    PlayPauseRequested?.Invoke();
                    break;
                case "next":
                    NextRequested?.Invoke();
                    break;
                case "previous":
                    PreviousRequested?.Invoke();
                    break;
                default:
                    if (action.StartsWith("seek:", StringComparison.Ordinal)
                        && double.TryParse(action[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        SeekRequested?.Invoke(TimeSpan.FromSeconds(seconds));
                    }
                    break;
            }
        }
    }
}

[SupportedOSPlatform("browser")]
internal static partial class MediaSessionInterop
{
    [JSImport("register", "MediaSession")]
    internal static partial bool Register();

    [JSImport("waitForAction", "MediaSession")]
    internal static partial Task<string> WaitForActionAsync();

    [JSImport("setNowPlaying", "MediaSession")]
    internal static partial void SetNowPlaying(string title, string artist, string album);

    [JSImport("setPlaybackState", "MediaSession")]
    internal static partial void SetPlaybackState(bool isPlaying, bool hasItem);

    [JSImport("setArtwork", "MediaSession")]
    internal static partial void SetArtwork(byte[] artworkContent);

    [JSImport("setPlaybackProgress", "MediaSession")]
    internal static partial void SetPlaybackProgress(double positionMs, double durationMs);
}
