using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace LightStudio.LightPlayer.Services.SingleInstance;

/// <summary>
/// Linux single-instance coordinator built on the session bus. The primary
/// instance owns the application's well-known name (its app id) and publishes
/// <see cref="IFreedesktopApplication"/> at the app object path; secondary
/// launches detect the owned name, forward their file-open / activation request
/// to the primary, and exit.
/// </summary>
/// <remarks>
/// This works inside a Flatpak sandbox: an app is always allowed to own its own
/// app id (and sub-names) on the session bus, so no extra bus policy is required
/// for the single-instance channel.
///
/// The name is claimed with <see cref="ServiceRegistrationOptions.None"/> — never
/// replacing an existing owner — so a running primary keeps ownership and the new
/// process reliably detects it as a secondary. Everything is best-effort: with no
/// reachable session bus the process simply runs as its own primary.
///
/// <see cref="IFreedesktopApplication"/> methods are invoked on the D-Bus read
/// loop; the <see cref="FilesOpened"/> / <see cref="Activated"/> events are raised
/// there, so subscribers must marshal to the UI thread.
/// </remarks>
internal sealed class DBusSingleInstanceService : IFreedesktopApplication, ISingleInstanceService
{
    private readonly string appId;
    private readonly ObjectPath applicationPath;
    private Connection? connection;
    private bool disposed;

    public DBusSingleInstanceService(string appId, string objectPath)
    {
        this.appId = appId;
        applicationPath = new ObjectPath(objectPath);
    }

    public event Action<IReadOnlyList<string>>? FilesOpened;

    public event Action? Activated;

    public ObjectPath ObjectPath => applicationPath;

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
        var address = Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            return true; // No session bus advertised: can't coordinate, act as primary.
        }

        var conn = new Connection(address);
        await conn.ConnectAsync();

        // Publish the application object first so it is ready to serve the moment
        // we own the name, then claim the name without replacing an existing owner.
        await conn.RegisterObjectAsync(this);
        try
        {
            await conn.RegisterServiceAsync(appId, ServiceRegistrationOptions.None);
        }
        catch (InvalidOperationException)
        {
            // The name is already owned: a primary instance is running. Forward the
            // activation to it and signal the caller to exit.
            await ForwardToPrimaryAsync(conn, activationPaths);
            conn.Dispose();
            return false;
        }

        // We are the primary owner; keep the connection alive for the app lifetime.
        connection = conn;
        return true;
    }

    private async Task ForwardToPrimaryAsync(Connection conn, IReadOnlyList<string> activationPaths)
    {
        var primary = conn.CreateProxy<IFreedesktopApplication>(appId, applicationPath);
        var platformData = new Dictionary<string, object>();

        if (activationPaths.Count > 0)
        {
            var uris = new string[activationPaths.Count];
            for (var i = 0; i < activationPaths.Count; i++)
            {
                uris[i] = FileActivation.ToFileUri(activationPaths[i]);
            }

            await primary.OpenAsync(uris, platformData);
        }
        else
        {
            await primary.ActivateAsync(platformData);
        }
    }

    // ---- org.freedesktop.Application (served on the primary, D-Bus read loop) ----

    Task IFreedesktopApplication.ActivateAsync(IDictionary<string, object> platformData)
    {
        Activated?.Invoke();
        return Task.CompletedTask;
    }

    Task IFreedesktopApplication.OpenAsync(string[] uris, IDictionary<string, object> platformData)
    {
        var paths = new List<string>(uris.Length);
        foreach (var uri in uris)
        {
            var path = FileActivation.ToLocalPath(uri);
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            FilesOpened?.Invoke(paths);
        }
        else
        {
            Activated?.Invoke();
        }

        return Task.CompletedTask;
    }

    Task IFreedesktopApplication.ActivateActionAsync(string actionName, object[] parameter, IDictionary<string, object> platformData)
        => Task.CompletedTask;

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
