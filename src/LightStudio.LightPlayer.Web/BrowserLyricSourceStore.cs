using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LocalServices;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>Keeps the registered lyric source scripts across browser page loads.</summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserLyricSourceStore(string directory)
{
    private const string StoreId = "lyric-sources";
    private const string SnapshotName = "scripts.zip";

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool available;

    /// <summary>
    /// Lays the kept scripts back down in the runtime's filesystem, reporting whether this browser
    /// had a set of its own. False asks the caller for the bundled sources instead: it means either
    /// the first load here or a store this browser will not open.
    /// </summary>
    public async Task<bool> RestoreAsync()
    {
        try
        {
            available = await BlockStoreInterop.OpenAsync(StoreId);
            if (!available)
                return false;

            int size = await BlockStoreInterop.ReadBeginAsync(StoreId, SnapshotName);
            if (size <= 0)
                return false;

            var data = new byte[size];
            await BlockStoreInterop.ReadEndAsync(StoreId, SnapshotName, data);
            Directory.CreateDirectory(directory);
            using var stream = new MemoryStream(data, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // Only the flat set of scripts is kept, so a name is all an entry may be.
                if (entry.Name.Length == 0 || entry.FullName != entry.Name)
                    throw new InvalidDataException("The stored lyric source snapshot contains an invalid path.");

                entry.ExtractToFile(Path.Combine(directory, entry.Name), overwrite: true);
            }

            return true;
        }
        catch (Exception exception)
        {
            available = false;
            AppLog.Warning("Web.LyricSources", "The stored lyric sources could not be restored.", exception);
            return false;
        }
    }

    public void Attach(JsonLyricSourceService sources) =>
        sources.Changed += (_, _) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (!available)
            return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.js"))
                {
                    archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Fastest);
                }
            }

            await BrowserMainThread
                .InvokeAsync(() => BlockStoreInterop.WriteAsync(StoreId, SnapshotName, stream.ToArray()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.LyricSources", "The lyric sources could not be persisted.", exception);
        }
        finally
        {
            gate.Release();
        }
    }
}
