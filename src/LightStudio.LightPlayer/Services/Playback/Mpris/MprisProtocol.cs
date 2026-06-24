using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace LightStudio.LightPlayer.Services.Playback.Mpris;

// D-Bus contract for the MPRIS (Media Player Remote Interfacing Specification)
// over D-Bus. A compliant player exposes the two interfaces below on the object
// path "/org/mpris/MediaPlayer2" under a well-known bus name of the form
// "org.mpris.MediaPlayer2.<app>[.instance<id>]". Desktop shells, "playerctl",
// and media-key daemons discover the player through these interfaces.
//
// Tmds.DBus models a D-Bus interface as a .NET interface decorated with
// [DBusInterface] and inheriting IDBusObject. The org.freedesktop.DBus.Properties
// surface is modelled by the Get/GetAll/Set/WatchProperties members. A single
// registered object implements both interfaces (they share one object path), so
// the property accessors are implemented explicitly per interface.

/// <summary>Root MPRIS interface (<c>org.mpris.MediaPlayer2</c>).</summary>
[DBusInterface("org.mpris.MediaPlayer2")]
public interface IMediaPlayer2Root : IDBusObject
{
    Task RaiseAsync();

    Task QuitAsync();

    Task<object> GetAsync(string prop);

    Task<MediaPlayer2RootProperties> GetAllAsync();

    Task SetAsync(string prop, object val);

    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>Transport MPRIS interface (<c>org.mpris.MediaPlayer2.Player</c>).</summary>
[DBusInterface("org.mpris.MediaPlayer2.Player")]
public interface IMediaPlayer2Player : IDBusObject
{
    Task NextAsync();

    Task PreviousAsync();

    Task PauseAsync();

    Task PlayPauseAsync();

    Task StopAsync();

    Task PlayAsync();

    Task SeekAsync(long offset);

    Task SetPositionAsync(ObjectPath trackId, long position);

    Task OpenUriAsync(string uri);

    Task<object> GetAsync(string prop);

    Task<MediaPlayer2PlayerProperties> GetAllAsync();

    Task SetAsync(string prop, object val);

    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>Property bag for <c>org.mpris.MediaPlayer2</c> (a{sv}).</summary>
[Dictionary]
public class MediaPlayer2RootProperties
{
    public bool CanQuit;

    public bool CanRaise;

    public bool HasTrackList;

    public string Identity = string.Empty;

    public string DesktopEntry = string.Empty;

    public string[] SupportedUriSchemes = Array.Empty<string>();

    public string[] SupportedMimeTypes = Array.Empty<string>();
}

/// <summary>Property bag for <c>org.mpris.MediaPlayer2.Player</c> (a{sv}).</summary>
[Dictionary]
public class MediaPlayer2PlayerProperties
{
    public string PlaybackStatus = "Stopped";

    public string LoopStatus = "None";

    public double Rate = 1.0;

    public bool Shuffle;

    public IDictionary<string, object> Metadata = new Dictionary<string, object>();

    public double Volume = 1.0;

    public long Position;

    public double MinimumRate = 1.0;

    public double MaximumRate = 1.0;

    public bool CanGoNext;

    public bool CanGoPrevious;

    public bool CanPlay;

    public bool CanPause;

    public bool CanSeek;

    public bool CanControl = true;
}
