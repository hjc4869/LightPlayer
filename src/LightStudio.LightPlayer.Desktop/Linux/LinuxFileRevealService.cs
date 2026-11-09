using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Tmds.DBus.Protocol;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// Linux implementation of <see cref="IFileRevealService"/>. The file manager opens the folder with
/// the track already selected through the <c>org.freedesktop.FileManager1</c> session-bus interface,
/// which Dolphin, Nautilus, Nemo, Caja, Thunar and most other file managers serve.
/// </summary>
/// <remarks>
/// Inside Flatpak the request goes through the OpenURI portal's <c>OpenDirectory</c> instead: it
/// needs no extra sandbox permission and resolves document-portal paths to the file's real location
/// before handing it to the host's file manager.
///
/// The bus connection is kept for the process lifetime, since the portal may drop a request whose
/// caller has already left the bus. When the bus request fails, the folder is opened without a
/// selection.
/// </remarks>
internal sealed class LinuxFileRevealService(bool useDesktopPortal) : IFileRevealService
{
    private static readonly DesktopFileRevealService FolderFallback = new();

    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private DBusConnection? connection;

    public async Task RevealAsync(string filePath)
    {
        // A file that has since been moved or deleted still gets its folder opened.
        if (File.Exists(filePath))
        {
            try
            {
                if (await GetConnectionAsync() is { } conn)
                {
                    await (useDesktopPortal
                        ? OpenDirectoryAsync(conn, filePath)
                        : ShowItemsAsync(conn, filePath));
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "Desktop.FileReveal",
                    $"Could not select {filePath} in the file manager; opening its folder instead.",
                    ex);
            }
        }

        await FolderFallback.RevealAsync(filePath);
    }

    private static Task ShowItemsAsync(DBusConnection conn, string filePath)
    {
        return conn.CallMethodAsync(CreateMessage());

        MessageBuffer CreateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.FileManager1",
                path: "/org/freedesktop/FileManager1",
                @interface: "org.freedesktop.FileManager1",
                signature: "ass",
                member: "ShowItems");
            writer.WriteArray(new[] { FileActivation.ToFileUri(filePath) });

            // No startup notification id: the file manager applies its own focus policy.
            writer.WriteString(string.Empty);
            return writer.CreateMessage();
        }
    }

    private static async Task OpenDirectoryAsync(DBusConnection conn, string filePath)
    {
        using var file = File.OpenHandle(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await conn.CallMethodAsync(CreateMessage());

        MessageBuffer CreateMessage()
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.portal.Desktop",
                path: "/org/freedesktop/portal/desktop",
                @interface: "org.freedesktop.portal.OpenURI",
                signature: "sha{sv}",
                member: "OpenDirectory");
            writer.WriteString(string.Empty);
            writer.WriteHandle(file);
            writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
            return writer.CreateMessage();
        }
    }

    private async Task<DBusConnection?> GetConnectionAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            if (connection is null && DBusAddress.Session is { Length: > 0 } address)
            {
                var conn = new DBusConnection(address);
                try
                {
                    await conn.ConnectAsync();
                }
                catch
                {
                    conn.Dispose();
                    throw;
                }

                connection = conn;
            }

            return connection;
        }
        finally
        {
            connectionGate.Release();
        }
    }
}
