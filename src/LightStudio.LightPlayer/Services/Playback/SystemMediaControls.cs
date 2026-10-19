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

    /// <summary>
    /// Raised when the OS requests seeking to an absolute position within the current track
    /// (e.g. the user drags the lock-screen / notification progress bar).
    /// </summary>
    event Action<TimeSpan>? SeekRequested;

    void SetNowPlaying(string title, string artist, string album);

    void SetPlaybackState(bool isPlaying, bool hasItem);

    /// <summary>Sets the current track's encoded artwork content, or null to clear.</summary>
    void SetArtwork(byte[]? artworkContent);

    /// <summary>
    /// Reports the current playback timeline so the OS can render (and let the user drive) a
    /// progress / seek bar. <paramref name="position"/> is the elapsed time within the current
    /// track and <paramref name="duration"/> is its total length; a zero duration indicates no
    /// seekable timeline. Called frequently while playing, so implementations should be cheap.
    /// </summary>
    void SetPlaybackProgress(TimeSpan position, TimeSpan duration);
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

    public event Action<TimeSpan>? SeekRequested
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

    public void SetArtwork(byte[]? artworkContent)
    {
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
    }
}
