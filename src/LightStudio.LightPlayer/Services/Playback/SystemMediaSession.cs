using System;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// A borrowed hold on the OS transport controls, taken by a video session while the music queue is
/// stood down. Everything published through it lands on the lock screen / notification in place of
/// the music track, and the OS transport requests arrive here instead of at the music queue.
/// Disposing it hands the controls back, and the music transport republishes its own track.
/// </summary>
/// <remarks>
/// Suspending music alone is not enough: the OS session would keep naming the stopped track, so a
/// play from the notification or a media key would start music up again behind the video.
/// </remarks>
public sealed class SystemMediaSession : IDisposable
{
    private readonly ISystemMediaControls controls;
    private readonly Action<SystemMediaSession> release;
    private bool released;

    internal SystemMediaSession(ISystemMediaControls controls, Action<SystemMediaSession> release)
    {
        this.controls = controls;
        this.release = release;
    }

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    /// <summary>Raised when the OS asks to seek to an absolute position within the current item.</summary>
    public event Action<TimeSpan>? SeekRequested;

    public void SetNowPlaying(string title, string artist, string album)
    {
        if (!released)
        {
            controls.SetNowPlaying(title, artist, album);
        }
    }

    public void SetPlaybackState(bool isPlaying)
    {
        if (!released)
        {
            controls.SetPlaybackState(isPlaying, true);
        }
    }

    public void SetArtwork(byte[]? artworkContent)
    {
        if (!released)
        {
            controls.SetArtwork(artworkContent);
        }
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
        if (!released)
        {
            controls.SetPlaybackProgress(position, duration);
        }
    }

    /// <summary>Gives the controls back to the music transport. Publishing afterwards does nothing.</summary>
    public void Dispose()
    {
        if (released)
        {
            return;
        }

        released = true;
        release(this);
    }

    internal void RaisePlayPause() => PlayPauseRequested?.Invoke();

    internal void RaiseNext() => NextRequested?.Invoke();

    internal void RaisePrevious() => PreviousRequested?.Invoke();

    internal void RaiseSeek(TimeSpan position) => SeekRequested?.Invoke(position);
}
