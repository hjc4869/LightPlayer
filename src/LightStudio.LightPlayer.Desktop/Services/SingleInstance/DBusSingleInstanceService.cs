using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using Tmds.DBus.Protocol;

namespace LightStudio.LightPlayer.Desktop.Services.SingleInstance;

/// <summary>
/// Linux single-instance coordinator built on the session bus. The primary
/// instance owns the application's well-known name (its app id) and serves
/// <see cref="FreedesktopApplicationProtocol.Interface"/> at the app object path;
/// secondary launches detect the owned name, forward their target-open / activation
/// request to the primary, and exit.
/// </summary>
/// <remarks>
/// This works inside a Flatpak sandbox: an app is always allowed to own its own
/// app id (and sub-names) on the session bus, so no extra bus policy is required
/// for the single-instance channel.
///
/// The name is claimed with the <c>DoNotQueue</c> flag and without requesting
/// replacement, so a running primary keeps ownership and the new process reliably
/// detects it as a secondary (the RequestName reply is neither "primary owner" nor
/// "already owner"). Everything is best-effort: with no reachable session bus the
/// process simply runs as its own primary.
///
/// The <c>org.freedesktop.Application</c> methods are served on the D-Bus read loop
/// through the low-level, NativeAOT-compatible Tmds.DBus.Protocol API; the
/// <see cref="FilesOpened"/> / <see cref="Activated"/> events are raised there, so
/// subscribers must marshal to the UI thread.
/// </remarks>
internal sealed class DBusSingleInstanceService : IPathMethodHandler, ISingleInstanceService
{
    // org.freedesktop.DBus bus-daemon endpoint used to request the well-known name.
    private const string DBusService = "org.freedesktop.DBus";
    private const string DBusPath = "/org/freedesktop/DBus";
    private const string DBusInterface = "org.freedesktop.DBus";

    // org.freedesktop.DBus.RequestName flag / reply codes (D-Bus specification).
    private const uint NameFlagDoNotQueue = 0x4;
    private const uint RequestNameReplyPrimaryOwner = 1;
    private const uint RequestNameReplyAlreadyOwner = 4;

    private readonly string appId;
    private readonly string objectPath;
    private DBusConnection? connection;
    private bool disposed;

    public DBusSingleInstanceService(string appId, string objectPath)
    {
        this.appId = appId;
        this.objectPath = objectPath;
    }

    public event Action<IReadOnlyList<string>>? FilesOpened;

    public event Action? Activated;

    // ---- IPathMethodHandler --------------------------------------------

    public string Path => objectPath;

    public bool HandlesChildPaths => false;

    public bool TryActivate(IReadOnlyList<string> activationPaths)
    {
        try
        {
            return TryActivateAsync(activationPaths).GetAwaiter().GetResult();
        }
        catch
        {
            // No usable session bus (headless, container without a bus, denied by
            // policy): run as a standalone instance rather than failing to start.
            return true;
        }
    }

    private async Task<bool> TryActivateAsync(IReadOnlyList<string> activationPaths)
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            return true; // No session bus advertised: can't coordinate, act as primary.
        }

        var conn = new DBusConnection(address);
        await conn.ConnectAsync().ConfigureAwait(false);

        // Publish the application object first so it is ready to serve the moment
        // we own the name, then claim the name without queueing behind an owner.
        conn.AddMethodHandler(this);

        uint reply = await RequestNameAsync(conn, appId, NameFlagDoNotQueue).ConfigureAwait(false);
        if (reply is RequestNameReplyPrimaryOwner or RequestNameReplyAlreadyOwner)
        {
            // We are the primary owner; keep the connection alive for the app lifetime.
            connection = conn;
            return true;
        }

        // The name is already owned: a primary instance is running. Forward the
        // activation to it and signal the caller to exit.
        await ForwardToPrimaryAsync(conn, activationPaths).ConfigureAwait(false);
        conn.Dispose();
        return false;
    }

    // Calls org.freedesktop.DBus.RequestName directly so the reply code can be
    // inspected (the library's RequestNameAsync does not surface it). Returns the
    // raw DBUS_REQUEST_NAME_REPLY_* value.
    private static Task<uint> RequestNameAsync(DBusConnection conn, string name, uint flags)
    {
        return conn.CallMethodAsync(
            CreateMessage(),
            static (Message message, object? state) => message.GetBodyReader().ReadUInt32());

        MessageBuffer CreateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: DBusService,
                path: DBusPath,
                @interface: DBusInterface,
                signature: "su",
                member: "RequestName");
            writer.WriteString(name);
            writer.WriteUInt32(flags);
            return writer.CreateMessage();
        }
    }

    private Task ForwardToPrimaryAsync(DBusConnection conn, IReadOnlyList<string> activationPaths)
    {
        if (activationPaths.Count > 0)
        {
            var uris = new string[activationPaths.Count];
            for (var i = 0; i < activationPaths.Count; i++)
            {
                uris[i] = FileActivation.ToActivationUri(activationPaths[i]);
            }

            return conn.CallMethodAsync(CreateOpenMessage());

            MessageBuffer CreateOpenMessage()
            {
                var writer = conn.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: appId,
                    path: objectPath,
                    @interface: FreedesktopApplicationProtocol.Interface,
                    signature: "asa{sv}",
                    member: "Open");

                var uriArray = writer.WriteArrayStart(DBusType.String);
                foreach (var uri in uris)
                {
                    writer.WriteString(uri);
                }

                writer.WriteArrayEnd(uriArray);

                // Empty a{sv} platform data.
                var platformData = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(platformData);
                return writer.CreateMessage();
            }
        }

        return conn.CallMethodAsync(CreateActivateMessage());

        MessageBuffer CreateActivateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: appId,
                path: objectPath,
                @interface: FreedesktopApplicationProtocol.Interface,
                signature: "a{sv}",
                member: "Activate");

            // Empty a{sv} platform data.
            var platformData = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(platformData);
            return writer.CreateMessage();
        }
    }

    // ---- org.freedesktop.Application (served on the primary, D-Bus read loop) ----

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([FreedesktopApplicationProtocol.InterfaceXml]);
            return default;
        }

        var request = context.Request;
        if (request.InterfaceAsString == FreedesktopApplicationProtocol.Interface)
        {
            switch (request.MemberAsString)
            {
                case "Activate":
                    ReplyEmpty(context);
                    Activated?.Invoke();
                    return default;

                case "Open":
                    HandleOpen(context);
                    return default;

                case "ActivateAction":
                    // Not used by this app, but part of the interface contract.
                    ReplyEmpty(context);
                    return default;
            }
        }

        // Anything else: MethodContext.Dispose sends an "unknown method" error reply.
        return default;
    }

    private void HandleOpen(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var targets = new List<string>();

        // Read the "as" (uris) argument; the trailing a{sv} platform data is ignored.
        var uris = reader.ReadArrayStart(DBusType.String);
        while (reader.HasNext(uris))
        {
            var target = FileActivation.ToActivationTarget(reader.ReadString());
            if (!string.IsNullOrEmpty(target))
            {
                targets.Add(target);
            }
        }

        ReplyEmpty(context);

        if (targets.Count > 0)
        {
            FilesOpened?.Invoke(targets);
        }
        else
        {
            Activated?.Invoke();
        }
    }

    private static void ReplyEmpty(MethodContext context)
        => context.Reply(context.CreateReplyWriter(null).CreateMessage());

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connection?.Dispose();
        connection = null;
    }
}
