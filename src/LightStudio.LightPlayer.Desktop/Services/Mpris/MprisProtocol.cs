using System;

namespace LightStudio.LightPlayer.Desktop.Services.Mpris;

// D-Bus contract for MPRIS (Media Player Remote Interfacing Specification). A
// compliant player exposes the two interfaces below on the object path
// "/org/mpris/MediaPlayer2" under a well-known bus name of the form
// "org.mpris.MediaPlayer2.<app>[.instance<id>]". Desktop shells, "playerctl",
// and media-key daemons discover the player through these interfaces.
//
// This uses the low-level, NativeAOT/trimming-compatible Tmds.DBus.Protocol API
// (the high-level Tmds.DBus relies on reflection / IL emit and fails under AOT):
// instead of reflection-generated proxies, the player is served by a hand-written
// IPathMethodHandler (see MprisSystemMediaControls) that dispatches on the request
// interface/member and reads/writes the D-Bus message body directly. The names
// and introspection XML fragments for the served interfaces live here.

/// <summary>Static D-Bus names and introspection XML for the MPRIS interfaces.</summary>
internal static class MprisProtocol
{
    /// <summary>Object path shared by both MPRIS interfaces.</summary>
    public const string ObjectPath = "/org/mpris/MediaPlayer2";

    /// <summary>Root MPRIS interface name.</summary>
    public const string RootInterface = "org.mpris.MediaPlayer2";

    /// <summary>Transport MPRIS interface name.</summary>
    public const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    /// <summary>Introspection XML fragment for <c>org.mpris.MediaPlayer2</c>.</summary>
    public static ReadOnlyMemory<byte> RootInterfaceXml { get; } =
        """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>

        """u8.ToArray();

    /// <summary>Introspection XML fragment for <c>org.mpris.MediaPlayer2.Player</c>.</summary>
    public static ReadOnlyMemory<byte> PlayerInterfaceXml { get; } =
        """
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek">
            <arg direction="in" type="x"/>
          </method>
          <method name="SetPosition">
            <arg direction="in" type="o"/>
            <arg direction="in" type="x"/>
          </method>
          <method name="OpenUri">
            <arg direction="in" type="s"/>
          </method>
          <signal name="Seeked">
            <arg name="Position" type="x"/>
          </signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Shuffle" type="b" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="read"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>

        """u8.ToArray();
}
