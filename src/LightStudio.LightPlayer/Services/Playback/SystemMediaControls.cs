using System;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// Abstraction over OS "now playing" / system media transport controls (SMTC on
/// Windows, MPRIS on Linux, MPNowPlayingInfoCenter on macOS). The playback
/// controller pushes metadata and transport state here and listens for hardware
/// / OS transport requests. The desktop default is a no-op; per-OS
/// implementations can be added later without touching the controller.
/// </summary>
public interface ISystemMediaControls
{
    event Action? PlayPauseRequested;

    event Action? NextRequested;

    event Action? PreviousRequested;

    void SetNowPlaying(string title, string artist, string album);

    void SetPlaybackState(bool isPlaying, bool hasItem);

    /// <summary>Sets the current track's artwork URI (e.g. a <c>file://</c> URL), or null to clear.</summary>
    void SetArtwork(string? artworkUri);
}

/// <summary>
/// Default cross-platform implementation that does nothing. Keeps playback
/// working everywhere until real per-OS media controls are wired in.
/// </summary>
public sealed class NoopSystemMediaControls : ISystemMediaControls
{
    public event Action? PlayPauseRequested
    {
        add { }
        remove { }
    }

    public event Action? NextRequested
    {
        add { }
        remove { }
    }

    public event Action? PreviousRequested
    {
        add { }
        remove { }
    }

    public void SetNowPlaying(string title, string artist, string album)
    {
    }

    public void SetPlaybackState(bool isPlaying, bool hasItem)
    {
    }

    public void SetArtwork(string? artworkUri)
    {
    }
}
