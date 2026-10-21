using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;
using Tmds.DBus.Protocol;

namespace LightStudio.LightPlayer.Desktop.Services.Mpris;

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
internal sealed class MprisSystemMediaControls : IPathMethodHandler, ISystemMediaControls, IDisposable
{
    private static readonly ObjectPath NoTrackPath = new("/org/mpris/MediaPlayer2/TrackList/NoTrack");

    private const string StatusPlaying = "Playing";
    private const string StatusPaused = "Paused";
    private const string StatusStopped = "Stopped";

    // A reported position that differs from the interpolated one by more than this many
    // microseconds is treated as a discontinuous jump (a seek) worth a Seeked signal.
    private const long SeekThresholdMicros = 1_000_000;

    private readonly string identity;
    private readonly string busName;
    private readonly string artworkDirectory;
    private readonly object gate = new();

    private DBusConnection? connection;
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

    // Progress state in microseconds (the MPRIS unit). lastProgressTicks is a Stopwatch
    // timestamp used to interpolate the expected position for seek detection; 0 means
    // "no baseline yet".
    private long positionMicros;
    private long lengthMicros;
    private long lastProgressTicks;

    public MprisSystemMediaControls(string identity, string busNameSuffix)
    {
        this.identity = identity;
        busName = $"org.mpris.MediaPlayer2.{busNameSuffix}";
        artworkDirectory = System.IO.Path.Combine(AppPaths.CacheDirectory, "MprisArtwork");
        ResetArtworkDirectory();
        _ = Task.Run(InitializeAsync);
    }

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public event Action<TimeSpan>? SeekRequested;

    // ---- IPathMethodHandler --------------------------------------------

    public string Path => MprisProtocol.ObjectPath;

    public bool HandlesChildPaths => false;

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
            // Reset the timeline so Position / mpris:length never report stale values from
            // the previous track before the first progress tick of the new one arrives.
            positionMicros = 0;
            lengthMicros = 0;
            lastProgressTicks = 0;
        }

        ResetArtworkDirectory();
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
                positionMicros = 0;
                lengthMicros = 0;
                lastProgressTicks = 0;
            }
        }

        EmitPlayerChanged(
            "PlaybackStatus", "Metadata", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek");
    }

    public void SetArtwork(byte[]? artworkContent)
    {
        var artworkUri = string.Empty;
        if (ResetArtworkDirectory() && artworkContent is { Length: > 0 })
        {
            try
            {
                var path = System.IO.Path.Combine(artworkDirectory, System.IO.Path.GetRandomFileName());
                File.WriteAllBytes(path, artworkContent);
                artworkUri = new Uri(path).AbsoluteUri;
            }
            catch
            {
                // Artwork is optional; an unwritable cache directory leaves it unset.
            }
        }

        lock (gate)
        {
            artUrl = artworkUri;
        }

        EmitPlayerChanged("Metadata");
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
        var newPos = Math.Max(0L, (long)(position.TotalMilliseconds * 1000.0));
        var newLen = Math.Max(0L, (long)(duration.TotalMilliseconds * 1000.0));
        var nowTicks = Stopwatch.GetTimestamp();

        bool lengthChanged;
        bool seeked;
        lock (gate)
        {
            lengthChanged = newLen != lengthMicros;

            // Detect a discontinuous jump (a seek) by comparing the reported position to the
            // value a client would have interpolated from the last update at the current rate.
            // Ordinary 200ms progress ticks stay far below the threshold; only real seeks (or a
            // track change resetting toward 0) exceed it. The first update has no baseline.
            if (lastProgressTicks == 0)
            {
                seeked = false;
            }
            else
            {
                var elapsed = playbackStatus == StatusPlaying
                    ? Stopwatch.GetElapsedTime(lastProgressTicks, nowTicks)
                    : TimeSpan.Zero;
                var expected = positionMicros + (long)(elapsed.TotalMilliseconds * 1000.0);
                seeked = Math.Abs(newPos - expected) > SeekThresholdMicros;
            }

            positionMicros = newPos;
            lengthMicros = newLen;
            lastProgressTicks = nowTicks;
        }

        // mpris:length lives inside Metadata, so a new duration is published there. Position is
        // deliberately never sent through PropertiesChanged (per the MPRIS spec); clients read it
        // on demand and rely on the Seeked signal to correct their interpolation after a jump.
        if (lengthChanged)
        {
            EmitPlayerChanged("Metadata");
        }

        if (seeked)
        {
            EmitSeeked(newPos);
        }
    }

    // ---- IPathMethodHandler dispatch (D-Bus read loop) -----------------

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MprisProtocol.RootInterfaceXml, MprisProtocol.PlayerInterfaceXml]);
            return default;
        }

        switch (context.Request.InterfaceAsString)
        {
            case "org.freedesktop.DBus.Properties":
                HandleProperties(context);
                return default;
            case MprisProtocol.RootInterface:
                HandleRoot(context);
                return default;
            case MprisProtocol.PlayerInterface:
                HandlePlayer(context);
                return default;
        }

        // Anything else: MethodContext.Dispose sends an "unknown method" error reply.
        return default;
    }

    // org.freedesktop.DBus.Properties: Get / GetAll / Set.
    private void HandleProperties(MethodContext context)
    {
        var request = context.Request;
        switch (request.MemberAsString)
        {
            case "Get" when request.SignatureAsString == "ss":
            {
                var reader = request.GetBodyReader();
                var iface = reader.ReadString();
                var prop = reader.ReadString();
                var writer = context.CreateReplyWriter("v");
                if (!TryWritePropertyVariant(ref writer, iface, prop))
                {
                    writer.Dispose();
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"No such property '{prop}'.");
                    return;
                }

                context.Reply(writer.CreateMessage());
                return;
            }

            case "GetAll" when request.SignatureAsString == "s":
            {
                var reader = request.GetBodyReader();
                var iface = reader.ReadString();
                var writer = context.CreateReplyWriter("a{sv}");
                WriteAllProperties(ref writer, iface);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "Set" when request.SignatureAsString == "ssv":
                // No writable properties are exposed; acknowledge without changing state.
                ReplyEmpty(context);
                return;
        }
    }

    // org.mpris.MediaPlayer2 root transport (Raise / Quit are no-ops here).
    private static void HandleRoot(MethodContext context)
    {
        switch (context.Request.MemberAsString)
        {
            case "Raise":
            case "Quit":
                ReplyEmpty(context);
                return;
        }
    }

    // org.mpris.MediaPlayer2.Player transport controls.
    private void HandlePlayer(MethodContext context)
    {
        switch (context.Request.MemberAsString)
        {
            case "Next":
                NextRequested?.Invoke();
                break;
            case "Previous":
                PreviousRequested?.Invoke();
                break;
            case "PlayPause":
                PlayPauseRequested?.Invoke();
                break;
            case "Play":
                // Our transport hook is a toggle; only fire it when not already playing
                // so an explicit Play from a client never accidentally pauses.
                if (!IsPlaying())
                {
                    PlayPauseRequested?.Invoke();
                }

                break;
            case "Pause":
                if (IsPlaying())
                {
                    PlayPauseRequested?.Invoke();
                }

                break;
            case "Seek":
                if (context.Request.SignatureAsString == "x")
                {
                    HandleSeek(context);
                }

                break;
            case "SetPosition":
                if (context.Request.SignatureAsString == "ox")
                {
                    HandleSetPosition(context);
                }

                break;
            case "Stop":
            case "OpenUri":
                // Accepted but not acted on (stopping / opening is not supported).
                break;
            default:
                // Unknown member: let MethodContext.Dispose send the error reply.
                return;
        }

        ReplyEmpty(context);
    }

    // Seek(x Offset): relative seek by Offset microseconds (may be negative). The absolute
    // target is derived from the last reported position and clamped to the track bounds.
    private void HandleSeek(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var offsetMicros = reader.ReadInt64();
        long target;
        lock (gate)
        {
            target = positionMicros + offsetMicros;
        }

        RaiseSeek(target);
    }

    // SetPosition(o TrackId, x Position): absolute seek to Position microseconds, honored only
    // when TrackId matches the current track (per the MPRIS spec); stale ids are ignored.
    private void HandleSetPosition(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var requestedTrack = reader.ReadObjectPath();
        var target = reader.ReadInt64();
        lock (gate)
        {
            if (!hasItem || requestedTrack.ToString() != trackId.ToString())
            {
                return;
            }
        }

        RaiseSeek(target);
    }

    // Clamps an absolute target (microseconds) into the current track and raises SeekRequested
    // in milliseconds for the shared controller to apply.
    private void RaiseSeek(long targetMicros)
    {
        long len;
        lock (gate)
        {
            len = lengthMicros;
        }

        if (targetMicros < 0)
        {
            targetMicros = 0;
        }
        else if (len > 0 && targetMicros > len)
        {
            targetMicros = len;
        }

        SeekRequested?.Invoke(TimeSpan.FromMilliseconds(targetMicros / 1000.0));
    }

    // ---- Property serialization ----------------------------------------

    private static readonly string[] RootPropertyNames =
    {
        "CanQuit", "CanRaise", "HasTrackList", "Identity",
        "DesktopEntry", "SupportedUriSchemes", "SupportedMimeTypes",
    };

    private static readonly string[] PlayerPropertyNames =
    {
        "PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume",
        "Position", "MinimumRate", "MaximumRate", "CanGoNext", "CanGoPrevious",
        "CanPlay", "CanPause", "CanSeek", "CanControl",
    };

    // Writes the variant value of a single property; returns false when unknown.
    private bool TryWritePropertyVariant(ref MessageWriter writer, string iface, string prop)
    {
        if (iface == MprisProtocol.RootInterface)
        {
            return TryWriteRootProperty(ref writer, prop);
        }

        if (iface == MprisProtocol.PlayerInterface)
        {
            return TryWritePlayerProperty(ref writer, prop);
        }

        return false;
    }

    // Writes the full a{sv} property dictionary for the requested interface.
    private void WriteAllProperties(ref MessageWriter writer, string iface)
    {
        var dictStart = writer.WriteDictionaryStart();
        if (iface == MprisProtocol.RootInterface)
        {
            foreach (var prop in RootPropertyNames)
            {
                WritePropertyEntry(ref writer, prop, root: true);
            }
        }
        else if (iface == MprisProtocol.PlayerInterface)
        {
            foreach (var prop in PlayerPropertyNames)
            {
                WritePropertyEntry(ref writer, prop, root: false);
            }
        }

        writer.WriteDictionaryEnd(dictStart);
    }

    private void WritePropertyEntry(ref MessageWriter writer, string prop, bool root)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(prop);
        if (root)
        {
            TryWriteRootProperty(ref writer, prop);
        }
        else
        {
            TryWritePlayerProperty(ref writer, prop);
        }
    }

    private bool TryWriteRootProperty(ref MessageWriter writer, string prop)
    {
        switch (prop)
        {
            case "CanQuit":
            case "CanRaise":
            case "HasTrackList":
                writer.WriteVariantBool(false);
                return true;
            case "Identity":
                writer.WriteVariantString(identity);
                return true;
            case "DesktopEntry":
                writer.WriteVariantString(string.Empty);
                return true;
            case "SupportedUriSchemes":
            case "SupportedMimeTypes":
                WriteEmptyStringArrayVariant(ref writer);
                return true;
            default:
                return false;
        }
    }

    private bool TryWritePlayerProperty(ref MessageWriter writer, string prop)
    {
        switch (prop)
        {
            case "PlaybackStatus":
                writer.WriteVariantString(CurrentStatus());
                return true;
            case "LoopStatus":
                writer.WriteVariantString("None");
                return true;
            case "Rate":
            case "MinimumRate":
            case "MaximumRate":
            case "Volume":
                writer.WriteVariantDouble(1.0);
                return true;
            case "Shuffle":
                writer.WriteVariantBool(false);
                return true;
            case "Metadata":
                WriteMetadataVariant(ref writer);
                return true;
            case "Position":
                writer.WriteVariantInt64(CurrentPositionMicros());
                return true;
            case "CanGoNext":
            case "CanGoPrevious":
            case "CanPlay":
            case "CanPause":
                writer.WriteVariantBool(HasItem());
                return true;
            case "CanSeek":
                writer.WriteVariantBool(HasItem());
                return true;
            case "CanControl":
                writer.WriteVariantBool(true);
                return true;
            default:
                return false;
        }
    }

    private static void WriteEmptyStringArrayVariant(ref MessageWriter writer)
    {
        writer.WriteSignature("as");
        var arrayStart = writer.WriteArrayStart(DBusType.String);
        writer.WriteArrayEnd(arrayStart);
    }

    // Writes a variant wrapping the current track's mpris/xesam metadata (a{sv}).
    private void WriteMetadataVariant(ref MessageWriter writer)
    {
        string trackTitle;
        string trackArtist;
        string trackAlbum;
        string trackArt;
        ObjectPath id;
        bool has;
        long len;
        lock (gate)
        {
            trackTitle = title;
            trackArtist = artist;
            trackAlbum = album;
            trackArt = artUrl;
            id = hasItem ? trackId : NoTrackPath;
            has = hasItem;
            len = lengthMicros;
        }

        writer.WriteSignature("a{sv}");
        var dictStart = writer.WriteDictionaryStart();

        // mpris:trackid is mandatory and must be a valid object path.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("mpris:trackid");
        writer.WriteVariantObjectPath(id.ToString());

        if (has)
        {
            // mpris:length (track duration) is an int64 count of microseconds.
            if (len > 0)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:length");
                writer.WriteVariantInt64(len);
            }

            if (!string.IsNullOrEmpty(trackTitle))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:title");
                writer.WriteVariantString(trackTitle);
            }

            if (!string.IsNullOrEmpty(trackArtist))
            {
                // xesam:artist is an array of strings (as) wrapped in a variant.
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:artist");
                writer.WriteSignature("as");
                var artistArray = writer.WriteArrayStart(DBusType.String);
                writer.WriteString(trackArtist);
                writer.WriteArrayEnd(artistArray);
            }

            if (!string.IsNullOrEmpty(trackAlbum))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:album");
                writer.WriteVariantString(trackAlbum);
            }

            if (!string.IsNullOrEmpty(trackArt))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:artUrl");
                writer.WriteVariantString(trackArt);
            }
        }

        writer.WriteDictionaryEnd(dictStart);
    }

    private static void ReplyEmpty(MethodContext context)
        => context.Reply(context.CreateReplyWriter(null).CreateMessage());

    private bool ResetArtworkDirectory()
    {
        try
        {
            if (Directory.Exists(artworkDirectory))
            {
                Directory.Delete(artworkDirectory, recursive: true);
            }

            Directory.CreateDirectory(artworkDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Connection lifecycle ------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            var address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address))
            {
                // No session bus advertised: degrade to a silent no-op.
                return;
            }

            var conn = new DBusConnection(address);
            await conn.ConnectAsync();

            // Serve the object before claiming the well-known name.
            conn.AddMethodHandler(this);
            await conn.RequestNameAsync(busName, RequestNameOptions.None);

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
            AppLog.Warning("SystemMediaControls.MPRIS", "MPRIS initialization failed.", ex);
        }
    }

    public void Dispose()
    {
        DBusConnection? conn;
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
        ResetArtworkDirectory();
    }

    // ---- Helpers -------------------------------------------------------

    // Emits org.freedesktop.DBus.Properties.PropertiesChanged for the Player
    // interface with the current values of the given properties.
    private void EmitPlayerChanged(params string[] properties)
    {
        var conn = connection;
        if (!connected || conn is null || properties.Length == 0)
        {
            return;
        }

        try
        {
            var writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                path: MprisProtocol.ObjectPath,
                @interface: "org.freedesktop.DBus.Properties",
                signature: "sa{sv}as",
                member: "PropertiesChanged");

            // interface_name
            writer.WriteString(MprisProtocol.PlayerInterface);

            // changed_properties: a{sv}
            var dictStart = writer.WriteDictionaryStart();
            foreach (var prop in properties)
            {
                WritePropertyEntry(ref writer, prop, root: false);
            }

            writer.WriteDictionaryEnd(dictStart);

            // invalidated_properties: as (empty because values are always included above)
            var invalidated = writer.WriteArrayStart(DBusType.String);
            writer.WriteArrayEnd(invalidated);

            conn.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
            // Best-effort: never let a signal-send failure disrupt playback.
        }
    }

    // Emits org.mpris.MediaPlayer2.Player.Seeked(x) so clients resync their interpolated
    // position after a discontinuous jump. The argument is the new position in microseconds.
    private void EmitSeeked(long positionUs)
    {
        var conn = connection;
        if (!connected || conn is null)
        {
            return;
        }

        try
        {
            var writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                path: MprisProtocol.ObjectPath,
                @interface: MprisProtocol.PlayerInterface,
                signature: "x",
                member: "Seeked");
            writer.WriteInt64(positionUs);
            conn.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
            // Best-effort: never let a signal-send failure disrupt playback.
        }
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

    private long CurrentPositionMicros()
    {
        lock (gate)
        {
            return positionMicros;
        }
    }
}
