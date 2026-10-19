using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>How much of a persisted library folder the page may currently read.</summary>
internal enum BrowserFolderAccess
{
    /// <summary>The saved folder reference is unavailable.</summary>
    Missing,

    /// <summary>Readable now.</summary>
    Granted,

    /// <summary>Restored from a previous session; needs a user gesture to be re-granted.</summary>
    Prompt,

    /// <summary>The user refused, and the browser will not ask again for this handle.</summary>
    Denied,
}

/// <summary>A child of a picked directory, as reported by the File System Access API.</summary>
internal readonly record struct BrowserDirectoryEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTime LastModifiedUtc);

/// <summary>An open file: the JavaScript-side slot id, and what the browser said about it.</summary>
internal readonly record struct BrowserFileHandle(int Id, long Length, DateTime LastModifiedUtc);

/// <summary>
/// A media identifier inside a folder the user picked in this browser: <c>fsa://{root}/{path}</c>.
/// </summary>
/// <remarks>
/// The library persists these strings as folder paths and media paths, so they have to survive a
/// round trip through settings, the SQLite index and the playback queue unchanged. The scheme also
/// makes the shared settings UI treat them as opaque identifiers rather than filesystem paths.
/// Segments are percent-encoded, so the separator never appears inside a name.
/// </remarks>
internal readonly record struct BrowserMediaPath(string RootId, string RelativePath)
{
    public const string Scheme = "fsa://";

    /// <summary>Whether an arbitrary library identifier belongs to this scheme.</summary>
    public static bool Owns(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    public static bool TryParse(string? path, out BrowserMediaPath result)
    {
        result = default;
        if (!Owns(path))
        {
            return false;
        }

        var body = path![Scheme.Length..];
        var separator = body.IndexOf('/');
        if (separator < 0)
        {
            result = new BrowserMediaPath(body, string.Empty);
            return body.Length > 0;
        }

        var rootId = body[..separator];
        if (rootId.Length == 0)
        {
            return false;
        }

        var segments = body[(separator + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.UnescapeDataString(segments[index]);
        }

        result = new BrowserMediaPath(rootId, string.Join('/', segments));
        return true;
    }

    /// <summary>The identifier of the folder this one names a child of, or null at the root.</summary>
    public BrowserMediaPath? Parent()
    {
        if (RelativePath.Length == 0)
        {
            return null;
        }

        var separator = RelativePath.LastIndexOf('/');
        return new BrowserMediaPath(RootId, separator < 0 ? string.Empty : RelativePath[..separator]);
    }

    public string Name
    {
        get
        {
            var separator = RelativePath.LastIndexOf('/');
            return separator < 0 ? RelativePath : RelativePath[(separator + 1)..];
        }
    }

    public BrowserMediaPath Child(string name) => new(
        RootId,
        RelativePath.Length == 0 ? name : $"{RelativePath}/{name}");

    public override string ToString()
    {
        if (RelativePath.Length == 0)
        {
            return Scheme + RootId;
        }

        var segments = RelativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return $"{Scheme}{RootId}/{string.Join('/', segments)}";
    }
}

/// <summary>
/// Managed face of <c>file-system-access.js</c>: the folders the user granted this page, and
/// enumeration and ranged reads inside them.
/// </summary>
/// <remarks>
/// JavaScript module bindings are per thread and this module is only imported on the main one, so
/// every call is marshalled onto the thread that imported it.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class BrowserFileSystem
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, RootFolder> roots = [];

    /// <summary>
    /// Whether the module is imported and reachable. Reading an adopted file needs no more than
    /// this; only the folder side of the module depends on <see cref="IsSupported"/>.
    /// </summary>
    private static bool moduleReady;

    /// <summary>Whether this browser can select library folders and store their identities.</summary>
    public static bool IsSupported { get; private set; }

    public static bool SupportsPersistentFolders { get; private set; }

    /// <summary>
    /// Imports the module, adopts the folders picked in earlier sessions and records how much of
    /// each one is readable without prompting. Must run on the main thread.
    /// </summary>
    public static async Task InitializeAsync()
    {
        await BrowserAssets.ImportAsync("FileSystemAccess", "file-system-access.js");
        moduleReady = BrowserMainThread.IsAvailable;
        IsSupported = moduleReady && FileSystemAccessInterop.IsSupported();
        SupportsPersistentFolders = moduleReady && FileSystemAccessInterop.SupportsPersistentFolders();
        if (!IsSupported)
        {
            return;
        }

        foreach (var line in (await FileSystemAccessInterop.RestoreAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 4)
            {
                lock (gate)
                {
                    roots[fields[0]] = new RootFolder(fields[1], ParseAccess(fields[2]), fields[3] == "true");
                }
            }
        }
    }

    /// <summary>The name the user's file manager gives a picked folder, or null when it is gone.</summary>
    public static string? GetDisplayName(string rootId)
    {
        lock (gate)
        {
            return roots.TryGetValue(rootId, out var root) ? root.Name : null;
        }
    }

    public static BrowserFolderAccess GetAccess(string rootId)
    {
        lock (gate)
        {
            return roots.TryGetValue(rootId, out var root) ? root.Access : BrowserFolderAccess.Missing;
        }
    }

    public static bool RequiresFolderSelection(string rootId)
    {
        lock (gate)
        {
            return roots.TryGetValue(rootId, out var root) && root.Temporary;
        }
    }

    /// <summary>
    /// Opens the browser's directory picker and keeps what the user chose. Must be reached without
    /// an intervening await from the click, or the browser refuses to open the dialog.
    /// </summary>
    public static async Task<(string RootId, string Name)?> PickFolderAsync(bool forVideo)
    {
        if (!IsSupported)
        {
            return null;
        }

        var picked = await BrowserMainThread.InvokeAsync(() => FileSystemAccessInterop.PickDirectoryAsync(
            forVideo ? "videos" : "music",
            forVideo ? "lightplayer-video" : "lightplayer-music"));
        var fields = picked.Split('\t');
        if (fields.Length != 3 || fields[0].Length == 0)
        {
            return null;
        }

        lock (gate)
        {
            roots[fields[0]] = new RootFolder(fields[1], BrowserFolderAccess.Granted, fields[2] == "true");
        }

        return (fields[0], fields[1]);
    }

    /// <summary>
    /// Asks the browser to re-grant read access to a restored folder. Only answered while the call
    /// is still attributable to a user gesture, so it must be reached straight from a click.
    /// </summary>
    public static async Task<bool> RequestAccessAsync(string rootId)
    {
        if (!IsSupported)
        {
            return false;
        }

        var access = ParseAccess(await BrowserMainThread.InvokeAsync(
            () => FileSystemAccessInterop.RequestAccessAsync(rootId)));
        lock (gate)
        {
            if (roots.TryGetValue(rootId, out var root))
            {
                roots[rootId] = root with { Access = access };
            }
        }

        return access == BrowserFolderAccess.Granted;
    }

    /// <summary>
    /// Re-grants native handles together. Temporary folders require separate confirmation gestures.
    /// </summary>
    public static async Task RequestAccessAsync(IReadOnlyList<string> rootIds)
    {
        if (!IsSupported || rootIds.Count == 0)
        {
            return;
        }

        var reported = await BrowserMainThread.InvokeAsync(
            () => FileSystemAccessInterop.RequestAccessManyAsync(string.Join('\t', rootIds)));
        lock (gate)
        {
            foreach (var line in reported.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');
                if (fields.Length == 2 && roots.TryGetValue(fields[0], out var root))
                {
                    roots[fields[0]] = root with { Access = ParseAccess(fields[1]) };
                }
            }
        }
    }

    public static async Task ForgetAsync(string rootId)
    {
        lock (gate)
        {
            roots.Remove(rootId);
        }

        if (IsSupported)
        {
            await BrowserMainThread.InvokeAsync(() => FileSystemAccessInterop.ForgetAsync(rootId));
        }
    }

    /// <summary>
    /// Registers a whole file from a file-handler launch, share, or file picker as a root of its own
    /// and returns the identifier that names it. Nothing is
    /// persisted: the grant came with the file and dies with the page, so these never reach the
    /// library.
    /// </summary>
    public static string AdoptFile(string rootId, string name)
    {
        lock (gate)
        {
            roots[rootId] = new RootFolder(name, BrowserFolderAccess.Granted);
        }

        return new BrowserMediaPath(rootId, name).ToString();
    }

    /// <summary>
    /// Opens the browser's file picker and adopts what the user chose. Needs no more than the
    /// module: files are handed over whole, so this works even where a folder cannot be kept
    /// (<see cref="IsSupported"/>). Must be reached without an intervening await from the click,
    /// or the browser refuses to open the dialog.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        string description,
        string audioExtensions,
        string videoExtensions,
        string imageExtensions)
    {
        if (!moduleReady)
        {
            return [];
        }

        var picked = await BrowserMainThread.InvokeAsync(() =>
            FileSystemAccessInterop.PickFilesAsync(description, audioExtensions, videoExtensions, imageExtensions));
        var paths = new List<string>();
        foreach (var line in picked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 2 && fields[0].Length > 0)
            {
                paths.Add(AdoptFile(fields[0], fields[1]));
            }
        }

        return paths;
    }

    public static async Task<bool> DirectoryExistsAsync(BrowserMediaPath path)
    {
        if (!moduleReady || GetAccess(path.RootId) != BrowserFolderAccess.Granted)
        {
            return false;
        }

        return await BrowserMainThread.InvokeAsync(() =>
            FileSystemAccessInterop.DirectoryExistsAsync(path.RootId, path.RelativePath));
    }

    public static async Task<IReadOnlyList<BrowserDirectoryEntry>> ListAsync(BrowserMediaPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!moduleReady || GetAccess(path.RootId) != BrowserFolderAccess.Granted)
        {
            throw new UnauthorizedAccessException($"The browser cannot enumerate '{path}'.");
        }

        int id = await BrowserMainThread.InvokeAsync(() =>
            FileSystemAccessInterop.BeginListAsync(path.RootId, path.RelativePath));
        var entries = new List<BrowserDirectoryEntry>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string json = await BrowserMainThread.InvokeAsync(() => FileSystemAccessInterop.ReadListAsync(id));
                cancellationToken.ThrowIfCancellationRequested();
                if (json.Length > 65536)
                    throw new InvalidDataException("Directory page exceeds the transfer budget.");
                var page = JsonSerializer.Deserialize(json, BrowserDirectoryJsonContext.Default.BrowserDirectoryPage)
                    ?? throw new InvalidDataException("The browser returned no directory page.");
                if (page.Entries.Length > 128 || entries.Count + page.Entries.Length > 100000)
                    throw new InvalidDataException("Directory enumeration exceeds the entry budget.");
                foreach (var entry in page.Entries)
                    entries.Add(new BrowserDirectoryEntry(entry.Name, entry.IsDirectory, entry.Length, FromEpoch(entry.LastModified)));
                if (page.Done) break;
                await Task.Delay(1, cancellationToken);
            }
        }
        finally { await BrowserMainThread.InvokeAsync(() => FileSystemAccessInterop.EndListAsync(id)); }

        return entries;
    }

    public static async Task<BrowserFileHandle?> OpenAsync(BrowserMediaPath path)
    {
        if (!moduleReady || GetAccess(path.RootId) != BrowserFolderAccess.Granted)
        {
            return null;
        }

        var opened = await BrowserMainThread.InvokeAsync(() =>
            FileSystemAccessInterop.OpenFileAsync(path.RootId, path.RelativePath));
        var fields = opened.Split('\t');
        if (fields.Length != 3
            || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modified))
        {
            return null;
        }

        return new BrowserFileHandle(id, length, FromEpoch(modified));
    }

    /// <summary>Reads at most <paramref name="count"/> bytes into <paramref name="buffer"/>.</summary>
    public static async Task<int> ReadAsync(int fileId, long offset, byte[] buffer, int count)
    {
        var staged = await BrowserMainThread.InvokeAsync(
            () => FileSystemAccessInterop.ReadBeginAsync(fileId, offset, count));
        if (staged <= 0)
        {
            return staged < 0 ? throw new IOException("The browser could not read the local file.") : 0;
        }

        await BrowserMainThread.InvokeAsync(() =>
            FileSystemAccessInterop.ReadEndAsync(fileId, new ArraySegment<byte>(buffer, 0, staged)));
        return staged;
    }

    public static void Close(int fileId)
    {
        _ = CloseObservedAsync(fileId);
    }

    public static Task CloseAsync(int fileId) => moduleReady
        ? BrowserMainThread.InvokeAsync(() => FileSystemAccessInterop.CloseFileAsync(fileId))
        : Task.CompletedTask;

    private static async Task CloseObservedAsync(int fileId)
    {
        try { await CloseAsync(fileId).ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Files", "Could not close the browser file slot.", exception);
        }
    }

    private static DateTime FromEpoch(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

    private static BrowserFolderAccess ParseAccess(string state) => state switch
    {
        "granted" => BrowserFolderAccess.Granted,
        "prompt" => BrowserFolderAccess.Prompt,
        "denied" => BrowserFolderAccess.Denied,
        _ => BrowserFolderAccess.Missing,
    };

    private readonly record struct RootFolder(string Name, BrowserFolderAccess Access, bool Temporary = false);
}

[SupportedOSPlatform("browser")]
internal static partial class FileSystemAccessInterop
{
    [JSImport("isSupported", "FileSystemAccess")]
    internal static partial bool IsSupported();

    [JSImport("supportsPersistentFolders", "FileSystemAccess")]
    internal static partial bool SupportsPersistentFolders();

    /// <summary>Returns "rootId\tname\tstate\ttemporary" per saved folder.</summary>
    [JSImport("restore", "FileSystemAccess")]
    internal static partial Task<string> RestoreAsync();

    /// <summary>Returns "rootId\tname\ttemporary", or an empty string when nothing was picked or kept.</summary>
    [JSImport("pickDirectory", "FileSystemAccess")]
    internal static partial Task<string> PickDirectoryAsync(string startIn, string pickerId);

    /// <summary>
    /// Takes comma-separated extension lists and returns "rootId\tname" per picked file.
    /// </summary>
    [JSImport("pickFiles", "FileSystemAccess")]
    internal static partial Task<string> PickFilesAsync(
        string description,
        string audioExtensions,
        string videoExtensions,
        string imageExtensions);

    [JSImport("requestAccess", "FileSystemAccess")]
    internal static partial Task<string> RequestAccessAsync(string rootId);

    /// <summary>Takes tab-separated root ids and returns "rootId\tstate" per line.</summary>
    [JSImport("requestAccessMany", "FileSystemAccess")]
    internal static partial Task<string> RequestAccessManyAsync(string rootIds);

    [JSImport("forget", "FileSystemAccess")]
    internal static partial Task ForgetAsync(string rootId);

    [JSImport("directoryExists", "FileSystemAccess")]
    internal static partial Task<bool> DirectoryExistsAsync(string rootId, string path);

    [JSImport("beginList", "FileSystemAccess")]
    internal static partial Task<int> BeginListAsync(string rootId, string path);

    [JSImport("readList", "FileSystemAccess")]
    internal static partial Task<string> ReadListAsync(int id);

    [JSImport("endList", "FileSystemAccess")]
    internal static partial Task EndListAsync(int id);

    /// <summary>Returns "fileId\tsize\tlastModified", or an empty string when unreadable.</summary>
    [JSImport("openFile", "FileSystemAccess")]
    internal static partial Task<string> OpenFileAsync(string rootId, string path);

    /// <summary>Stages a slice in a JavaScript slot and returns its length, or -1 on failure.</summary>
    [JSImport("readBegin", "FileSystemAccess")]
    internal static partial Task<int> ReadBeginAsync(
        int fileId,
        [JSMarshalAs<JSType.Number>] long offset,
        int length);

    /// <summary>Copies the slot prepared by <see cref="ReadBeginAsync"/> into managed memory.</summary>
    [JSImport("readEnd", "FileSystemAccess")]
    internal static partial Task ReadEndAsync(
        int fileId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> buffer);

    [JSImport("closeFile", "FileSystemAccess")]
    internal static partial Task CloseFileAsync(int fileId);
}

internal sealed record BrowserDirectoryItem(string Name, bool IsDirectory, long Length, long LastModified);
internal sealed record BrowserDirectoryPage(BrowserDirectoryItem[] Entries, bool Done);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(BrowserDirectoryPage))]
internal sealed partial class BrowserDirectoryJsonContext : JsonSerializerContext;
