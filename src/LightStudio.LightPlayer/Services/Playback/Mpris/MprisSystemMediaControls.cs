using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace LightStudio.LightPlayer.Services.Playback.Mpris;

/// <summary>
/// Linux implementation of <see cref="ISystemMediaControls"/> that exposes the
/// player on the session bus through MPRIS (<c>org.mpris.MediaPlayer2</c> and
/// <c>org.mpris.MediaPlayer2.Player</c>). This lets desktop shells, lock screens,
/// media keys, and tools like <c>playerctl</c> show "now playing" metadata and
/// drive play/pause/next/previous.
/// </summary>
/// <remarks>
/// One registered object implements both MPRIS interfaces (they share the object
/// path <c>/org/mpris/MediaPlayer2</c>), so the <c>org.freedesktop.DBus.Properties</c>
/// accessors are implemented explicitly per interface.
///
/// Connecting to the bus is asynchronous and best-effort: construction never
/// blocks and never throws. If no session bus is reachable (headless session,
/// container, SSH without a bus) the object stays a silent no-op, matching the
/// "degrade gracefully" contract for optional OS integrations.
///
/// <see cref="SetNowPlaying"/> / <see cref="SetPlaybackState"/> are invoked on the
/// UI thread; transport methods (<c>Next</c>, <c>PlayPause</c>, ...) are invoked on
/// the D-Bus read loop. Shared state is guarded by <see cref="gate"/>.
/// </remarks>
internal sealed class MprisSystemMediaControls : IMediaPlayer2Root, IMediaPlayer2Player, ISystemMediaControls, IDisposable
{
    private static readonly ObjectPath MprisPath = new("/org/mpris/MediaPlayer2");
    private static readonly ObjectPath NoTrackPath = new("/org/mpris/MediaPlayer2/TrackList/NoTrack");

    private const string StatusPlaying = "Playing";
    private const string StatusPaused = "Paused";
    private const string StatusStopped = "Stopped";

    private readonly string identity;
    private readonly string busName;
    private readonly object gate = new();

    // Framework-supplied PropertiesChanged sink for the Player interface. Invoking
    // it emits org.freedesktop.DBus.Properties.PropertiesChanged to subscribers.
    private Action<PropertyChanges>? playerPropertiesChanged;

    private Connection? connection;
    private volatile bool connected;
    private bool disposed;

    // Player state (guarded by gate; read from the D-Bus loop, written from UI).
    private string playbackStatus = StatusStopped;
    private bool hasItem;
    private string title = string.Empty;
    private string artist = string.Empty;
    private string album = string.Empty;
    private string artUrl = string.Empty;
    private long trackCounter;
    private ObjectPath trackId = NoTrackPath;

    public MprisSystemMediaControls(string identity, string busNameSuffix)
    {
        this.identity = identity;
        busName = $"org.mpris.MediaPlayer2.{busNameSuffix}";
        _ = Task.Run(InitializeAsync);
    }

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public ObjectPath ObjectPath => MprisPath;

    // ---- ISystemMediaControls (UI thread) ------------------------------

    public void SetNowPlaying(string title, string artist, string album)
    {
        title ??= string.Empty;
        artist ??= string.Empty;
        album ??= string.Empty;
        lock (gate)
        {
            // Ignore redundant updates for the same track so the track id (and the
            // artwork tied to it) don't churn while the UI re-projects the queue.
            if (hasItem && this.title == title && this.artist == artist && this.album == album)
            {
                return;
            }

            this.title = title;
            this.artist = artist;
            this.album = album;
            // A new track invalidates the previous artwork until it is resolved.
            artUrl = string.Empty;
            // A fresh, unique track id signals a new track to MPRIS clients.
            trackId = new ObjectPath($"/org/mpris/MediaPlayer2/Track/{++trackCounter}");
        }

        EmitPlayerChanged("Metadata");
    }

    public void SetPlaybackState(bool isPlaying, bool hasItem)
    {
        lock (gate)
        {
            this.hasItem = hasItem;
            playbackStatus = !hasItem ? StatusStopped : isPlaying ? StatusPlaying : StatusPaused;
            if (!hasItem)
            {
                trackId = NoTrackPath;
            }
        }

        EmitPlayerChanged(
            "PlaybackStatus", "Metadata", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause");
    }

    public void SetArtwork(string? artworkUri)
    {
        lock (gate)
        {
            artUrl = artworkUri ?? string.Empty;
        }

        EmitPlayerChanged("Metadata");
    }

    Task IMediaPlayer2Root.RaiseAsync() => Task.CompletedTask;

    Task IMediaPlayer2Root.QuitAsync() => Task.CompletedTask;

    Task<object> IMediaPlayer2Root.GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "CanQuit" => false,
        "CanRaise" => false,
        "HasTrackList" => false,
        "Identity" => identity,
        "DesktopEntry" => string.Empty,
        "SupportedUriSchemes" => Array.Empty<string>(),
        "SupportedMimeTypes" => Array.Empty<string>(),
        _ => throw new ArgumentException($"Unknown property '{prop}'.", nameof(prop)),
    });

    Task<MediaPlayer2RootProperties> IMediaPlayer2Root.GetAllAsync() => Task.FromResult(new MediaPlayer2RootProperties
    {
        CanQuit = false,
        CanRaise = false,
        HasTrackList = false,
        Identity = identity,
        DesktopEntry = string.Empty,
        SupportedUriSchemes = Array.Empty<string>(),
        SupportedMimeTypes = Array.Empty<string>(),
    });

    Task IMediaPlayer2Root.SetAsync(string prop, object val) => Task.CompletedTask;

    Task<IDisposable> IMediaPlayer2Root.WatchPropertiesAsync(Action<PropertyChanges> handler) =>
        Task.FromResult<IDisposable>(NoopDisposable.Instance);

    // ---- org.mpris.MediaPlayer2.Player (D-Bus loop) --------------------

    Task IMediaPlayer2Player.NextAsync()
    {
        NextRequested?.Invoke();
        return Task.CompletedTask;
    }

    Task IMediaPlayer2Player.PreviousAsync()
    {
        PreviousRequested?.Invoke();
        return Task.CompletedTask;
    }

    Task IMediaPlayer2Player.PlayPauseAsync()
    {
        PlayPauseRequested?.Invoke();
        return Task.CompletedTask;
    }

    Task IMediaPlayer2Player.PlayAsync()
    {
        // Our transport hook is a toggle; only fire it when not already playing so
        // an explicit Play from a client never accidentally pauses.
        if (!IsPlaying())
        {
            PlayPauseRequested?.Invoke();
        }

        return Task.CompletedTask;
    }

    Task IMediaPlayer2Player.PauseAsync()
    {
        if (IsPlaying())
        {
            PlayPauseRequested?.Invoke();
        }

        return Task.CompletedTask;
    }

    Task IMediaPlayer2Player.StopAsync() => Task.CompletedTask;

    Task IMediaPlayer2Player.SeekAsync(long offset) => Task.CompletedTask;

    Task IMediaPlayer2Player.SetPositionAsync(ObjectPath trackId, long position) => Task.CompletedTask;

    Task IMediaPlayer2Player.OpenUriAsync(string uri) => Task.CompletedTask;

    Task<object> IMediaPlayer2Player.GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "PlaybackStatus" => CurrentStatus(),
        "LoopStatus" => "None",
        "Rate" => 1.0,
        "Shuffle" => false,
        "Metadata" => BuildMetadata(),
        "Volume" => 1.0,
        "Position" => 0L,
        "MinimumRate" => 1.0,
        "MaximumRate" => 1.0,
        "CanGoNext" => HasItem(),
        "CanGoPrevious" => HasItem(),
        "CanPlay" => HasItem(),
        "CanPause" => HasItem(),
        "CanSeek" => false,
        "CanControl" => true,
        _ => throw new ArgumentException($"Unknown property '{prop}'.", nameof(prop)),
    });

    Task<MediaPlayer2PlayerProperties> IMediaPlayer2Player.GetAllAsync()
    {
        var has = HasItem();
        return Task.FromResult(new MediaPlayer2PlayerProperties
        {
            PlaybackStatus = CurrentStatus(),
            LoopStatus = "None",
            Rate = 1.0,
            Shuffle = false,
            Metadata = BuildMetadata(),
            Volume = 1.0,
            Position = 0,
            MinimumRate = 1.0,
            MaximumRate = 1.0,
            CanGoNext = has,
            CanGoPrevious = has,
            CanPlay = has,
            CanPause = has,
            CanSeek = false,
            CanControl = true,
        });
    }

    Task IMediaPlayer2Player.SetAsync(string prop, object val) => Task.CompletedTask;

    Task<IDisposable> IMediaPlayer2Player.WatchPropertiesAsync(Action<PropertyChanges> handler)
    {
        playerPropertiesChanged += handler;
        return Task.FromResult<IDisposable>(new Subscription(() => playerPropertiesChanged -= handler));
    }

    // ---- Connection lifecycle ------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            var address = Address.Session;
            if (string.IsNullOrEmpty(address))
            {
                // No session bus advertised: degrade to a silent no-op.
                return;
            }

            var conn = new Connection(address);
            await conn.ConnectAsync();
            await conn.RegisterObjectAsync(this);
            await conn.RegisterServiceAsync(busName);

            lock (gate)
            {
                if (disposed)
                {
                    conn.Dispose();
                    return;
                }

                connection = conn;
                connected = true;
            }

            // Push the current snapshot now that subscribers can receive it.
            EmitPlayerChanged(
                "PlaybackStatus", "Metadata", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause");
        }
        catch (Exception ex)
        {
            // Optional integration: log and stay a no-op rather than failing playback.
            Console.WriteLine($"[MPRIS] Initialization failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Connection? conn;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            connected = false;
            conn = connection;
            connection = null;
        }

        conn?.Dispose();
    }

    // ---- Helpers -------------------------------------------------------

    private void EmitPlayerChanged(params string[] properties)
    {
        var handler = playerPropertiesChanged;
        if (!connected || handler is null || properties.Length == 0)
        {
            return;
        }

        var changed = new KeyValuePair<string, object>[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            changed[i] = new KeyValuePair<string, object>(properties[i], PlayerPropertyValue(properties[i]));
        }

        handler(new PropertyChanges(changed));
    }

    private object PlayerPropertyValue(string prop) => prop switch
    {
        "PlaybackStatus" => CurrentStatus(),
        "Metadata" => BuildMetadata(),
        "CanGoNext" => HasItem(),
        "CanGoPrevious" => HasItem(),
        "CanPlay" => HasItem(),
        "CanPause" => HasItem(),
        _ => throw new ArgumentException($"Unknown property '{prop}'.", nameof(prop)),
    };

    private IDictionary<string, object> BuildMetadata()
    {
        string trackTitle;
        string trackArtist;
        string trackAlbum;
        string trackArt;
        ObjectPath id;
        bool has;
        lock (gate)
        {
            trackTitle = title;
            trackArtist = artist;
            trackAlbum = album;
            trackArt = artUrl;
            id = hasItem ? trackId : NoTrackPath;
            has = hasItem;
        }

        // mpris:trackid is mandatory and must be a valid object path.
        var metadata = new Dictionary<string, object> { ["mpris:trackid"] = id };
        if (!has)
        {
            return metadata;
        }

        if (!string.IsNullOrEmpty(trackTitle))
        {
            metadata["xesam:title"] = trackTitle;
        }

        if (!string.IsNullOrEmpty(trackArtist))
        {
            metadata["xesam:artist"] = new[] { trackArtist };
        }

        if (!string.IsNullOrEmpty(trackAlbum))
        {
            metadata["xesam:album"] = trackAlbum;
        }

        if (!string.IsNullOrEmpty(trackArt))
        {
            metadata["mpris:artUrl"] = trackArt;
        }

        return metadata;
    }

    private string CurrentStatus()
    {
        lock (gate)
        {
            return playbackStatus;
        }
    }

    private bool HasItem()
    {
        lock (gate)
        {
            return hasItem;
        }
    }

    private bool IsPlaying()
    {
        lock (gate)
        {
            return playbackStatus == StatusPlaying;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? dispose;

        public Subscription(Action dispose) => this.dispose = dispose;

        public void Dispose()
        {
            var action = dispose;
            dispose = null;
            action?.Invoke();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
