using System;

namespace LightStudio.LightPlayer.Desktop.Services.SingleInstance;

// D-Bus contract for the freedesktop.org application interface
// (org.freedesktop.Application). A single-instance desktop app owns its app id as
// a well-known bus name and registers this interface at the object path derived
// from the app id (dots -> slashes). Launchers, desktop portals, and secondary
// launches reach the running instance through Activate()/Open().
//
// See the "D-Bus Activation" section of the freedesktop Desktop Entry spec:
// https://specifications.freedesktop.org/desktop-entry-spec/latest/dbus.html
//
// This uses the low-level, NativeAOT/trimming-compatible Tmds.DBus.Protocol API:
// the interface is served by a hand-written IPathMethodHandler (see
// DBusSingleInstanceService) that dispatches on the request member and reads the
// message body directly. The names and introspection XML live here.

/// <summary>Static D-Bus names and introspection XML for <c>org.freedesktop.Application</c>.</summary>
internal static class FreedesktopApplicationProtocol
{
    /// <summary><c>org.freedesktop.Application</c> interface name.</summary>
    public const string Interface = "org.freedesktop.Application";

    /// <summary>Introspection XML fragment for <c>org.freedesktop.Application</c>.</summary>
    public static ReadOnlyMemory<byte> InterfaceXml { get; } =
        """
        <interface name="org.freedesktop.Application">
          <method name="Activate">
            <arg direction="in" type="a{sv}"/>
          </method>
          <method name="Open">
            <arg direction="in" type="as"/>
            <arg direction="in" type="a{sv}"/>
          </method>
          <method name="ActivateAction">
            <arg direction="in" type="s"/>
            <arg direction="in" type="av"/>
            <arg direction="in" type="a{sv}"/>
          </method>
        </interface>

        """u8.ToArray();
}
