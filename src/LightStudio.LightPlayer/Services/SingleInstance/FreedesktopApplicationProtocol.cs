using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace LightStudio.LightPlayer.Services.SingleInstance;

// D-Bus contract for the freedesktop.org application interface
// (org.freedesktop.Application). A single-instance desktop app owns its app id as
// a well-known bus name and registers this interface at the object path derived
// from the app id (dots -> slashes). Launchers, desktop portals, and secondary
// launches reach the running instance through Activate()/Open().
//
// See the "D-Bus Activation" section of the freedesktop Desktop Entry spec:
// https://specifications.freedesktop.org/desktop-entry-spec/latest/dbus.html
//
// Tmds.DBus models a D-Bus interface as a .NET interface decorated with
// [DBusInterface] and inheriting IDBusObject; the trailing "Async" of each method
// is stripped to form the D-Bus member name (ActivateAsync -> Activate).

/// <summary><c>org.freedesktop.Application</c> — activation and file-open entry points.</summary>
[DBusInterface("org.freedesktop.Application")]
public interface IFreedesktopApplication : IDBusObject
{
    /// <summary>Activate the application (bring it forward); <c>a{sv}</c> platform data.</summary>
    Task ActivateAsync(IDictionary<string, object> platformData);

    /// <summary>Open the given <c>file://</c> URIs; <c>a{sv}</c> platform data.</summary>
    Task OpenAsync(string[] uris, IDictionary<string, object> platformData);

    /// <summary>Invoke a named application action; unused here but part of the interface.</summary>
    Task ActivateActionAsync(string actionName, object[] parameter, IDictionary<string, object> platformData);
}
