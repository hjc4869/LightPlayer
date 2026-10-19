using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>Keeps imported video shader packages across browser page loads.</summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserVideoShaderPackageStore(string directory)
{
    private const string StoreId = "video-shader-packages";
    private const string SnapshotName = "packages.zip";

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool available;

    public async Task RestoreAsync()
    {
        try
        {
            available = await BlockStoreInterop.OpenAsync(StoreId);
            if (!available)
                return;

            int size = await BlockStoreInterop.ReadBeginAsync(StoreId, SnapshotName);
            if (size <= 0)
                return;

            var data = new byte[size];
            await BlockStoreInterop.ReadEndAsync(StoreId, SnapshotName, data);
            Directory.CreateDirectory(directory);
            using var stream = new MemoryStream(data, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                string relative = Path.GetRelativePath(directory, path);
                if (relative == ".." ||
                    relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                {
                    throw new InvalidDataException("The stored shader package snapshot contains an invalid path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            available = false;
            AppLog.Warning("Web.ShaderPackage", "The stored shader packages could not be restored.", exception);
        }
    }

    public void Attach(VideoShaderPackageCatalog catalog) =>
        catalog.Changed += (_, _) => _ = SaveAsync();

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
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                    archive.CreateEntryFromFile(path, name, CompressionLevel.Fastest);
                }
            }

            await BrowserMainThread
                .InvokeAsync(() => BlockStoreInterop.WriteAsync(StoreId, SnapshotName, stream.ToArray()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.ShaderPackage", "The shader packages could not be persisted.", exception);
        }
        finally
        {
            gate.Release();
        }
    }
}