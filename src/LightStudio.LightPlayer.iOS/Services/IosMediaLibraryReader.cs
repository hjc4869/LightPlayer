using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using CoreGraphics;
using Foundation;
using LightStudio.LightPlayer.Services;
using LightStudio.LocalServices;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using MediaPlayer;
using ObjCRuntime;
using Photos;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

internal sealed record PhotoLibraryResource(PHAssetResource Resource, string FileName, long Size);

/// <summary>Reads songs and photo library assets for <see cref="IosMediaLibraryProvider"/> without network access.</summary>
internal sealed class IosMediaLibraryReader : ISystemMediaReader
{
    private const long MusicCacheLimit = 1L << 30;
    private static readonly Selector LocallyAvailableSelector = new("locallyAvailable");
    private static readonly Selector FileSizeSelector = new("fileSize");
    private static readonly (string Type, string Extension)[] ExportTypes =
    [
        ("com.apple.m4a-audio", ".m4a"),
        ("com.apple.coreaudio-format", ".caf"),
        ("com.apple.quicktime-movie", ".mov"),
    ];

    private readonly ConcurrentDictionary<ulong, Lazy<Task<string>>> exports = new();
    private readonly string musicCache = AppPaths.CacheSubdirectory("SystemMusic");

    public bool Owns(string path) =>
        path.StartsWith(IosMediaLibraryProvider.PhotoScheme, StringComparison.Ordinal) ||
        path.StartsWith(IosMediaLibraryProvider.MusicScheme, StringComparison.Ordinal);

    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (path.StartsWith(IosMediaLibraryProvider.MusicScheme, StringComparison.Ordinal))
            return File.OpenRead(await ExportTrackAsync(path, cancellationToken).ConfigureAwait(false));

        using var asset = FetchAsset(path);
        return asset.MediaType == PHAssetMediaType.Video
            ? await OpenVideoAsync(asset, path, cancellationToken).ConfigureAwait(false)
            : await OpenPhotoAsync(asset, path, cancellationToken).ConfigureAwait(false);
    }

    public Task<MediaFileProperties?> GetPropertiesAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<MediaFileProperties?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pool = new NSAutoreleasePool();
            if (path.StartsWith(IosMediaLibraryProvider.MusicScheme, StringComparison.Ordinal))
            {
                using var item = FindSong(path);
                if (item is null)
                    return null;
                var name = (item.Title is { Length: > 0 } title ? title : "item") + Path.GetExtension(new Uri(path).AbsolutePath);
                var added = item.DateAdded is { } date ? (DateTime)date : DateTime.UnixEpoch;
                return new MediaFileProperties(name, path, null, -1, added);
            }

            using var asset = FetchAsset(path);
            if (DescribeLocalResource(asset) is not { } resource)
                return null;
            var modified = asset.ModificationDate ?? asset.CreationDate;
            return new MediaFileProperties(resource.FileName, path, null, resource.Size > 0 ? resource.Size : -1,
                modified is null ? DateTime.UnixEpoch : (DateTime)modified);
        }, cancellationToken);

    public Task<byte[]?> GetArtworkAsync(string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith(IosMediaLibraryProvider.MusicScheme, StringComparison.Ordinal))
            return Task.FromResult<byte[]?>(null);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pool = new NSAutoreleasePool();
            using var item = FindSong(path);
            using var image = item?.Artwork?.ImageWithSize(new CGSize(1024, 1024));
            using var data = image?.AsJPEG(0.9f);
            return data?.ToArray();
        }, cancellationToken);
    }

    internal static async Task<byte[]?> GetPhotoThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        using var asset = FetchAsset(path);
        using var options = new PHImageRequestOptions
        {
            NetworkAccessAllowed = false,
            DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
            ResizeMode = PHImageRequestOptionsResizeMode.Fast,
            Version = PHImageRequestOptionsVersion.Current,
        };
        var completion = new TaskCompletionSource<UIImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = PHImageManager.DefaultManager;
        var request = manager.RequestImageForAsset(asset, new CGSize(512, 512), PHImageContentMode.AspectFit, options,
            (image, info) =>
            {
                if (image is null)
                    AppLog.Warning("iOS.MediaLibrary", $"Could not read the system thumbnail for {path}: {Describe(info)}");
                completion.TrySetResult(image);
            });
        using var registration = cancellationToken.Register(() => manager.CancelImageRequest(request));
        using var thumbnail = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var data = thumbnail?.AsJPEG(0.82f);
        return data?.ToArray();
    }

    /// <summary>Selects the asset's current-version resource; null when its data is not stored on this device.</summary>
    internal static PhotoLibraryResource? DescribeLocalResource(PHAsset asset)
    {
        var video = asset.MediaType == PHAssetMediaType.Video;
        PHAssetResource? original = null;
        PHAssetResource? current = null;
        foreach (var resource in PHAssetResource.GetAssetResources(asset))
        {
            switch (resource.ResourceType)
            {
                case PHAssetResourceType.Photo when !video:
                case PHAssetResourceType.Video when video:
                    original ??= resource;
                    break;
                case PHAssetResourceType.FullSizePhoto when !video:
                case PHAssetResourceType.FullSizeVideo when video:
                    current ??= resource;
                    break;
            }
        }

        var primary = current ?? original;
        if (primary is null || !IsLocallyAvailable(primary))
            return null;
        var name = original?.OriginalFilename ?? primary.OriginalFilename;
        if (current is not null && Path.GetExtension(current.OriginalFilename) is { Length: > 0 } extension)
            name = Path.ChangeExtension(name, extension);
        return new PhotoLibraryResource(primary, name, ReadFileSize(primary));
    }

    // Photo library resources report storage state through key-value properties; when absent the item is treated as local
    // and network access stays disabled on every read.
    private static bool IsLocallyAvailable(PHAssetResource resource) =>
        !resource.RespondsToSelector(LocallyAvailableSelector) ||
        (resource.ValueForKey(new NSString("locallyAvailable")) as NSNumber)?.BoolValue != false;

    private static long ReadFileSize(PHAssetResource resource) =>
        resource.RespondsToSelector(FileSizeSelector)
            ? (resource.ValueForKey(new NSString("fileSize")) as NSNumber)?.Int64Value ?? 0
            : 0;

    private static PHAsset FetchAsset(string path)
    {
        var start = IosMediaLibraryProvider.PhotoScheme.Length;
        var end = path.IndexOf('/', start);
        if (!path.StartsWith(IosMediaLibraryProvider.PhotoScheme, StringComparison.Ordinal) || end <= start)
            throw new FileNotFoundException("Invalid photo library identifier.", path);
        using var assets = PHAsset.FetchAssetsUsingLocalIdentifiers([Uri.UnescapeDataString(path[start..end])], null);
        return assets.FirstObject as PHAsset
            ?? throw new FileNotFoundException("The item is no longer in the photo library.", path);
    }

    private static MPMediaItem? FindSong(string path)
    {
        using var query = new MPMediaQuery();
        query.AddFilterPredicate(MPMediaPropertyPredicate.PredicateWithValue(
            NSNumber.FromUInt64(SongId(path)), MPMediaItem.PersistentIDProperty));
        var items = query.Items ?? [];
        foreach (var other in items.Skip(1))
            other.Dispose();
        return items.FirstOrDefault();
    }

    private static ulong SongId(string path)
    {
        var parameter = new Uri(path).Query.TrimStart('?').Split('&')
            .FirstOrDefault(part => part.StartsWith("id=", StringComparison.Ordinal));
        return parameter is not null && ulong.TryParse(parameter.AsSpan(3), out var id)
            ? id
            : throw new FileNotFoundException("Invalid media library identifier.", path);
    }

    private static async Task<Stream> OpenPhotoAsync(PHAsset asset, string path, CancellationToken cancellationToken)
    {
        using var options = new PHImageRequestOptions
        {
            NetworkAccessAllowed = false,
            DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
            Version = PHImageRequestOptionsVersion.Current,
        };
        var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = PHImageManager.DefaultManager;
        var request = manager.RequestImageDataAndOrientation(asset, options, (data, _, _, info) =>
        {
            if (data is not null)
                completion.TrySetResult(data.AsStream());
            else
                completion.TrySetException(new IOException($"Could not read {path} from the photo library: {Describe(info)}"));
        });
        using var registration = cancellationToken.Register(() => manager.CancelImageRequest(request));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> OpenVideoAsync(PHAsset asset, string path, CancellationToken cancellationToken)
    {
        // Edits that Photos has not rendered to a file, such as slow motion, arrive as compositions; the original is read instead.
        var url = await RequestVideoUrlAsync(asset, PHVideoRequestOptionsVersion.Current, path, cancellationToken).ConfigureAwait(false)
            ?? await RequestVideoUrlAsync(asset, PHVideoRequestOptionsVersion.Original, path, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"The photo library did not provide a file for {path}.");
        try
        {
            return File.OpenRead(url);
        }
        catch (UnauthorizedAccessException exception)
        {
            AppLog.Warning("iOS.MediaLibrary", $"Copying {path} because its library file is not readable.", exception);
            return await ExportVideoAsync(asset, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> RequestVideoUrlAsync(
        PHAsset asset, PHVideoRequestOptionsVersion version, string path, CancellationToken cancellationToken)
    {
        using var options = new PHVideoRequestOptions
        {
            NetworkAccessAllowed = false,
            DeliveryMode = PHVideoRequestOptionsDeliveryMode.HighQualityFormat,
            Version = version,
        };
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = PHImageManager.DefaultManager;
        var request = manager.RequestAVAsset(asset, options, (result, _, info) =>
        {
            using (result)
            {
                if (result is AVUrlAsset { Url: { IsFileUrl: true, Path: { } file } })
                    completion.TrySetResult(file);
                else if (result is null)
                    completion.TrySetException(new IOException($"Could not read {path} from the photo library: {Describe(info)}"));
                else
                    completion.TrySetResult(null);
            }
        });
        using var registration = cancellationToken.Register(() => manager.CancelImageRequest(request));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ExportVideoAsync(PHAsset asset, string path, CancellationToken cancellationToken)
    {
        var resource = DescribeLocalResource(asset)
            ?? throw new IOException($"{path} is not stored on this device.");
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(resource.FileName));
        using var options = new PHAssetResourceRequestOptions { NetworkAccessAllowed = false };
        try
        {
            await PHAssetResourceManager.DefaultManager.WriteDataAsync(resource.Resource, NSUrl.FromFilename(file), options)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        }
        catch
        {
            File.Delete(file);
            throw;
        }
    }

    private async Task<string> ExportTrackAsync(string path, CancellationToken cancellationToken)
    {
        var id = SongId(path);
        for (var attempt = 0; ; attempt++)
        {
            var export = exports.GetOrAdd(id, key => new Lazy<Task<string>>(() => Task.Run(() => ExportTrackCoreAsync(key, path))));
            string file;
            try
            {
                file = await export.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (export.Value.IsCompleted)
                    exports.TryRemove(new(id, export));
            }

            if (attempt > 0 || File.Exists(file))
                return file;
        }
    }

    // Songs are re-muxed once without transcoding, since the media library exposes them only to AVFoundation.
    private async Task<string> ExportTrackCoreAsync(ulong id, string path)
    {
        var name = id.ToString("x16");
        if (Directory.EnumerateFiles(musicCache, name + ".*").FirstOrDefault() is { } cached)
        {
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            return cached;
        }

        using var url = new NSUrl(path);
        using var asset = new AVUrlAsset(url);
        using var session = new AVAssetExportSession(asset, AVAssetExportSessionPreset.Passthrough);
        var supported = session.SupportedFileTypes ?? [];
        var (type, extension) = ExportTypes.FirstOrDefault(candidate => supported.Contains(candidate.Type));
        if (type is null)
            throw new NotSupportedException($"The media library cannot export {path} without conversion.");
        var partial = Path.Combine(musicCache, $"{Guid.NewGuid():N}.partial{extension}");
        var destination = Path.Combine(musicCache, name + extension);
        session.OutputUrl = NSUrl.FromFilename(partial);
        session.OutputFileType = type;
        try
        {
            await session.ExportTaskAsync().ConfigureAwait(false);
            if (session.Status != AVAssetExportSessionStatus.Completed)
                throw new IOException($"Could not export {path} from the media library: {session.Error?.LocalizedDescription}");
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }

        TrimMusicCache(destination);
        return destination;
    }

    private void TrimMusicCache(string keep)
    {
        try
        {
            var files = new DirectoryInfo(musicCache).GetFiles().OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            var total = 0L;
            foreach (var file in files)
            {
                total += file.Length;
                if (total > MusicCacheLimit && file.FullName != keep)
                    file.Delete();
            }
        }
        catch (IOException exception)
        {
            AppLog.Warning("iOS.MediaLibrary", "Could not trim the exported song cache.", exception);
        }
    }

    private static string Describe(NSDictionary? info)
    {
        if (info is null)
            return "no details";
        if ((info[PHImageKeys.ResultIsInCloud] as NSNumber)?.BoolValue == true)
            return "the item is stored in iCloud";
        return (info[PHImageKeys.Error] as NSError)?.LocalizedDescription ?? info.Description ?? "no details";
    }
}
