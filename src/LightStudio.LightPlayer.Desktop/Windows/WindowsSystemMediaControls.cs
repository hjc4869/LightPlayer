using System;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services.Playback;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Windows implementation of <see cref="ISystemMediaControls"/> backed by the
/// System Media Transport Controls (SMTC). A private <see cref="MediaPlayer"/>
/// supplies the Win32 process with an SMTC session; playback itself remains in
/// LightPlayer's FFmpeg/audio-output pipeline.
/// </summary>
/// <remarks>
/// The media player's automatic command manager is disabled so Windows never
/// attempts to control the unused player. Metadata, state, timeline, artwork,
/// and transport requests are synchronized explicitly with the shared playback
/// controller instead.
/// </remarks>
internal sealed class WindowsSystemMediaControls : ISystemMediaControls, IDisposable
{
    private readonly MediaPlayer mediaPlayer;
    private readonly SystemMediaTransportControls controls;
    private readonly object gate = new();

    private bool hasItem;
    private bool isPlaying;
    private TimeSpan duration;
    private int artworkVersion;
    private InMemoryRandomAccessStream? artworkStream;
    private volatile bool disposed;

    public WindowsSystemMediaControls()
    {
        mediaPlayer = new MediaPlayer();
        mediaPlayer.CommandManager.IsEnabled = false;

        controls = mediaPlayer.SystemMediaTransportControls;
        controls.IsEnabled = false;
        controls.IsPlayEnabled = false;
        controls.IsPauseEnabled = false;
        controls.IsNextEnabled = false;
        controls.IsPreviousEnabled = false;
        controls.IsStopEnabled = false;
        controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
        controls.ButtonPressed += OnButtonPressed;
        controls.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;

        var updater = controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.Update();
    }

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public event Action<TimeSpan>? SeekRequested;

    public void SetNowPlaying(string title, string artist, string album)
    {
        if (disposed)
        {
            return;
        }

        // A new metadata snapshot invalidates artwork still being loaded for the
        // previous track. ClearAll also ensures a stopped/empty queue cannot leave
        // stale metadata visible in Windows' media flyout.
        Interlocked.Increment(ref artworkVersion);
        var updater = controls.DisplayUpdater;
        updater.ClearAll();
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title ?? string.Empty;
        updater.MusicProperties.Artist = artist ?? string.Empty;
        updater.MusicProperties.AlbumTitle = album ?? string.Empty;
        updater.Update();
        Interlocked.Exchange(ref artworkStream, null)?.Dispose();
    }

    public void SetPlaybackState(bool isPlaying, bool hasItem)
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            this.hasItem = hasItem;
            this.isPlaying = isPlaying && hasItem;
            if (!hasItem)
            {
                duration = TimeSpan.Zero;
            }
        }

        controls.IsPlayEnabled = hasItem;
        controls.IsPauseEnabled = hasItem;
        controls.IsNextEnabled = hasItem;
        controls.IsPreviousEnabled = hasItem;
        controls.PlaybackStatus = !hasItem
            ? MediaPlaybackStatus.Stopped
            : isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        controls.IsEnabled = hasItem;

        if (!hasItem)
        {
            UpdateTimeline(TimeSpan.Zero, TimeSpan.Zero);
        }
    }

    public void SetArtwork(byte[]? artworkContent)
    {
        if (disposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref artworkVersion);
        if (artworkContent is not { Length: > 0 })
        {
            var updater = controls.DisplayUpdater;
            updater.Thumbnail = null;
            updater.Update();
            Interlocked.Exchange(ref artworkStream, null)?.Dispose();
            return;
        }

        _ = SetArtworkAsync(artworkContent, version);
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
        if (disposed)
        {
            return;
        }

        duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        position = position > TimeSpan.Zero ? position : TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        lock (gate)
        {
            this.duration = duration;
        }

        UpdateTimeline(position, duration);
    }

    private async Task SetArtworkAsync(byte[] artworkContent, int version)
    {
        InMemoryRandomAccessStream? stream = null;
        try
        {
            stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(artworkContent);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            if (disposed || version != Volatile.Read(ref artworkVersion))
            {
                return;
            }

            var updater = controls.DisplayUpdater;
            updater.Thumbnail = thumbnail;
            updater.Update();

            var previousStream = Interlocked.Exchange(ref artworkStream, stream);
            stream = null;
            previousStream?.Dispose();
        }
        catch
        {
            // Artwork is optional; invalid image content and WinRT failures must never affect playback.
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void UpdateTimeline(TimeSpan position, TimeSpan duration)
    {
        var timeline = new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = duration,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = duration,
            Position = position,
        };
        controls.UpdateTimelineProperties(timeline);
    }

    private void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        bool currentHasItem;
        bool currentIsPlaying;
        lock (gate)
        {
            currentHasItem = hasItem;
            currentIsPlaying = isPlaying;
        }

        if (!currentHasItem || disposed)
        {
            return;
        }

        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play when !currentIsPlaying:
            case SystemMediaTransportControlsButton.Pause when currentIsPlaying:
                PlayPauseRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke();
                break;
        }
    }

    private void OnPlaybackPositionChangeRequested(
        SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        TimeSpan currentDuration;
        bool currentHasItem;
        lock (gate)
        {
            currentDuration = duration;
            currentHasItem = hasItem;
        }

        if (!currentHasItem || disposed)
        {
            return;
        }

        var requested = args.RequestedPlaybackPosition;
        if (requested < TimeSpan.Zero)
        {
            requested = TimeSpan.Zero;
        }
        else if (currentDuration > TimeSpan.Zero && requested > currentDuration)
        {
            requested = currentDuration;
        }

        SeekRequested?.Invoke(requested);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Interlocked.Increment(ref artworkVersion);
        Interlocked.Exchange(ref artworkStream, null)?.Dispose();

        try
        {
            controls.ButtonPressed -= OnButtonPressed;
            controls.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
            controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            controls.IsEnabled = false;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
        }
        catch
        {
            // Disposal is best-effort during application shutdown.
        }

        mediaPlayer.Dispose();
    }
}