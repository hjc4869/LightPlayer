using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// Holds off the screensaver and the display's idle blanking while video plays, through the
/// <c>org.freedesktop.ScreenSaver</c> session-bus interface served by GNOME, KDE and Xfce.
/// </summary>
/// <remarks>
/// The inhibition is owned by the bus connection, so the one opened here is kept for the process
/// lifetime: were it dropped, the desktop would release the inhibition with it. That is also the
/// failure mode wanted if the app dies while playing.
///
/// Everything is best-effort. A session with no bus, a desktop that serves no such interface, or a
/// sandbox policy that refuses the call all leave the display on its own idle behavior.
///
/// Under Flatpak this needs <c>--talk-name=org.freedesktop.ScreenSaver</c> in the manifest's
/// finish-args; the name is not one an app is allowed to talk to by default.
/// </remarks>
internal sealed class ScreenSaverInhibitor
{
    private const string Service = "org.freedesktop.ScreenSaver";
    private const string ObjectPath = "/org/freedesktop/ScreenSaver";
    private const string Interface = "org.freedesktop.ScreenSaver";

    private readonly string applicationName;
    private readonly string reason;
    private readonly object gate = new();

    private DBusConnection? connection;
    private uint? cookie;
    private Task pending = Task.CompletedTask;

    public ScreenSaverInhibitor(string applicationName, string reason)
    {
        this.applicationName = applicationName;
        this.reason = reason;
    }

    /// <summary>
    /// Takes or drops the inhibition. Called from the UI thread, but the bus round-trip outlives
    /// that call, so requests are queued behind one another: a release must not overtake the
    /// request it cancels and leave the screensaver held off for good.
    /// </summary>
    public void SetKeepScreenOn(bool value)
    {
        lock (gate)
        {
            pending = pending
                .ContinueWith(
                    _ => ApplyAsync(value),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task ApplyAsync(bool value)
    {
        try
        {
            if (value)
            {
                await InhibitAsync();
            }
            else
            {
                await UnInhibitAsync();
            }
        }
        catch
        {
            // See the best-effort note above.
        }
    }

    private async Task InhibitAsync()
    {
        if (cookie is not null)
        {
            return;
        }

        var conn = await GetConnectionAsync();
        if (conn is null)
        {
            return;
        }

        cookie = await conn.CallMethodAsync(
            CreateMessage(),
            static (Message message, object? state) => message.GetBodyReader().ReadUInt32());

        MessageBuffer CreateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Service,
                path: ObjectPath,
                @interface: Interface,
                signature: "ss",
                member: "Inhibit");
            writer.WriteString(applicationName);
            writer.WriteString(reason);
            return writer.CreateMessage();
        }
    }

    private async Task UnInhibitAsync()
    {
        if (cookie is not { } held || connection is not { } conn)
        {
            return;
        }

        // Forgotten before the call, so a refused release does not leave a cookie that later
        // Inhibit requests would keep skipping.
        cookie = null;
        await conn.CallMethodAsync(CreateMessage());

        MessageBuffer CreateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Service,
                path: ObjectPath,
                @interface: Interface,
                signature: "u",
                member: "UnInhibit");
            writer.WriteUInt32(held);
            return writer.CreateMessage();
        }
    }

    private async Task<DBusConnection?> GetConnectionAsync()
    {
        if (connection is not null)
        {
            return connection;
        }

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        var conn = new DBusConnection(address);
        await conn.ConnectAsync();
        connection = conn;
        return conn;
    }
}
