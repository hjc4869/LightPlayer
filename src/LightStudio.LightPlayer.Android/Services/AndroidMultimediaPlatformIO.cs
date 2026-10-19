#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using Android.Provider;
using Java.Interop;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Constant;
using LightStudio.MediaLibraryCore.IO;
using Microsoft.Win32.SafeHandles;
using AndroidUri = Android.Net.Uri;
using MediaInfo = LightStudio.MediaLibraryCore.IO.MediaInfo;
using Stream = System.IO.Stream;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Android implementation of <see cref="IMultiMediaPlatformIO"/> backed by the Storage Access
/// Framework. Library folders are content-tree URIs the user granted through the system folder
/// picker; enumeration walks the tree with <see cref="DocumentsContract"/>, media are opened via
/// the <see cref="ContentResolver"/>, and tags/artwork are read with <see cref="MediaMetadataRetriever"/>
/// (so no native FFmpeg build is required just to index the library).
/// </summary>
/// <remarks>
/// Picked trees use hierarchical <c>saf://</c> paths with escaped display-name segments. Native
/// content URIs are resolved at the IO boundary and remain readable as persisted identifiers.
/// <para>
/// Plain filesystem paths are supported as well and served with the regular <see cref="File"/> and
/// <see cref="Directory"/> APIs: shared locations such as <c>/sdcard/Music</c> are readable under
/// the granular media permissions the app declares, and are seeded as a default library folder.
/// </para>
/// </remarks>
internal sealed class AndroidMultimediaPlatformIO : IMultiMediaPlatformIO
{
    private static readonly string[] FolderArtworkNames =
    [
        "cover.jpg", "cover.jpeg", "folder.jpg", "folder.jpeg", "front.jpg", "front.jpeg",
        "cover.png", "folder.png", "front.png",
    ];

    private static readonly string[] ChildProjection =
    {
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnLastModified,
        DocumentsContract.Document.ColumnSize,
    };

    private readonly Context context;
    private readonly ConcurrentDictionary<string, string> documentPaths = new(StringComparer.Ordinal);
    private readonly object configurationLock = new();
    private readonly Task databasePreparation;
    private Dictionary<string, JsonElement> configuration = new(StringComparer.OrdinalIgnoreCase);

    public AndroidMultimediaPlatformIO(Context context)
    {
        this.context = context.ApplicationContext ?? context;
        LocalDataPath = Path.Combine(PlatformServices.AppDataPath, "LightStudio.LightPlayer");
        Directory.CreateDirectory(LocalDataPath);
        ConfigPath = Path.Combine(LocalDataPath, DatabaseConstants.DbConfigName);
        databasePreparation = Task.Run(() =>
        {
            LocalServices.IndexDatabaseReset.ApplyPending(LocalDataPath);
            LoadConfiguration();
        });
    }

    // Content-provider reads are I/O-bound but not highly parallel-friendly on mobile.
    public int OptimalIoThreadCount => Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

    public string LocalDataPath { get; }

    public string ConfigPath { get; }

    public void InitializePlatform() => IMultiMediaPlatformIO.Instance = this;

    public Task PrepareLibraryDatabaseAsync() => databasePreparation;

    public Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run<Stream>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsFileSystemPath(path))
            {
                return File.OpenRead(path);
            }

            var uri = ToDocumentUri(path)
                ?? throw new FileNotFoundException($"Invalid content URI: {path}");

            // A file descriptor yields a seekable FileStream, which FFmpeg needs for format probing
            // and seeking (unlike the forward-only stream from OpenInputStream).
            using var descriptor = context.ContentResolver!.OpenFileDescriptor(uri, "r")
                ?? throw new FileNotFoundException($"Unable to open content URI: {path}");
            var handle = new SafeFileHandle(descriptor.DetachFd(), ownsHandle: true);
            return new FileStream(handle, FileAccess.Read);
        }, cancellationToken);
    }

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsFileSystemPath(path))
            {
                return Directory.Exists(path);
            }

            try
            {
                var uri = ToDocumentUri(path);
                if (uri is null)
                {
                    return false;
                }

                string[] projection = [DocumentsContract.Document.ColumnMimeType];
                using var cursor = context.ContentResolver!.Query(uri, projection, null, null, null);
                return cursor?.MoveToFirst() == true
                    && cursor.GetString(0) == DocumentsContract.Document.MimeTypeDir;
            }
            catch (Exception exception)
            {
                AppLog.Warning("Android.Storage", $"Could not check folder '{path}'.", exception);
                return false;
            }
        }, cancellationToken);
    }

    public Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<MediaFileProperties?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsFileSystemPath(path))
            {
                var file = new FileInfo(path);
                return file.Exists
                    ? new MediaFileProperties(file.Name, file.FullName, file.DirectoryName, file.Length, file.LastWriteTimeUtc)
                    : null;
            }

            try
            {
                var uri = ToDocumentUri(path);
                if (uri is null)
                {
                    return null;
                }

                var isMediaStore = uri.Authority == "media";
                string[] projection = isMediaStore ? ["_display_name", "_size", "date_modified", "_data"] :
                [
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnSize,
                    DocumentsContract.Document.ColumnLastModified,
                ];
                using var cursor = context.ContentResolver!.Query(uri, projection, null, null, null);
                if (cursor?.MoveToFirst() != true)
                {
                    return null;
                }

                var name = cursor.GetString(0) ?? PortableFileName(path);
                var length = cursor.IsNull(1) ? -1L : cursor.GetLong(1);
                var lastModifiedMs = cursor.IsNull(2) ? 0L : cursor.GetLong(2) * (isMediaStore ? 1000 : 1);
                var lastModified = lastModifiedMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMs).UtcDateTime
                    : DateTime.UnixEpoch;
                var documentPath = NormalizePath(path);

                return new MediaFileProperties(
                    name,
                    documentPath,
                    isMediaStore ? Path.GetDirectoryName(cursor.GetString(3)) : GetParentPath(documentPath),
                    length,
                    lastModified);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Android.Storage", $"Could not read properties for '{path}'.", exception);
                return null;
            }
        }, cancellationToken);
    }

    public Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourceFilePath) || string.IsNullOrWhiteSpace(referencedFileName))
            {
                return null;
            }

            var fileSystemPath = IsFileSystemPath(sourceFilePath) ? sourceFilePath : GetMediaStoreFilePath(sourceFilePath);
            if (fileSystemPath is not null)
            {
                return ResolveFileSystemReference(fileSystemPath, referencedFileName);
            }

            var parentPath = GetParentPath(sourceFilePath);
            if (parentPath is null)
            {
                return null;
            }

            var targetName = PortableFileName(referencedFileName);
            string? match = null;
            EnumerateChildren(
                parentPath,
                onFile: (name, uri, _, _) =>
                {
                    if (match is null && string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = uri;
                    }
                });
            return match;
        }, cancellationToken);
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path)
    {
        return Task.Run(() =>
        {
            var files = new List<IFile>();
            if (IsFileSystemPath(path))
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    var info = new FileInfo(file);
                    files.Add(new MediaFile(info.Name, info.FullName, info.LastWriteTimeUtc, info.Length));
                }
            }
            else
            {
                EnumerateChildren(path, onFile: (name, uri, lastModified, sizeInBytes) =>
                    files.Add(new MediaFile(name, uri, lastModified, sizeInBytes)));
            }

            return files.ToArray();
        });
    }

    public Task<string[]> ListFoldersFullNameInPathAsync(string path)
    {
        return Task.Run(() =>
        {
            if (IsFileSystemPath(path))
            {
                return Directory.GetDirectories(path);
            }

            var folders = new List<string>();
            EnumerateChildren(path, onFolder: uri => folders.Add(uri));
            return folders.ToArray();
        });
    }

    public Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath)
    {
        return Task.Run(() =>
        {
            using var retriever = new MediaMetadataRetriever();
            SetDataSource(retriever, mediaFilePath);

            string? Read(MetadataKey key) => retriever.ExtractMetadata(key);
            var durationMs = long.TryParse(Read(MetadataKey.Duration), out var ms) ? ms : 0L;

            return new MediaInfo
            {
                Title = Read(MetadataKey.Title) ?? string.Empty,
                Artist = Read(MetadataKey.Artist) ?? string.Empty,
                Album = Read(MetadataKey.Album) ?? string.Empty,
                AlbumArtist = Read(MetadataKey.Albumartist) ?? string.Empty,
                Composer = Read(MetadataKey.Composer) ?? string.Empty,
                Date = Read(MetadataKey.Year) ?? string.Empty,
                Genre = Read(MetadataKey.Genre) ?? string.Empty,
                DiscNumber = Read(MetadataKey.DiscNumber) ?? string.Empty,
                TrackNumber = Read(MetadataKey.CdTrackNumber) ?? string.Empty,
                TotalTracks = Read(MetadataKey.NumTracks) ?? string.Empty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                AllProperties = new Dictionary<string, string>(),
            };
        });
    }

    public Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        ReadAlbumCoverAsync(
            mediaFilePath,
            static retriever => retriever.GetEmbeddedPicture() is { Length: > 0 } picture ? picture : null,
            ReadToEndAsync,
            cancellationToken);

    public Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        ReadAlbumCoverAsync(
            mediaFilePath,
            ReadEmbeddedPicture,
            static async (stream, token) =>
            {
                // Providers can hand out pipes, whose length is unknown until they are read.
                if (stream.CanSeek)
                {
                    return await ArtworkBuffer.ReadAsync(stream, stream.Length, token).ConfigureAwait(false);
                }

                return await ReadToEndAsync(stream, token).ConfigureAwait(false) is { } content
                    ? ArtworkBuffer.Copy(content)
                    : null;
            },
            cancellationToken);

    private async Task<T?> ReadAlbumCoverAsync<T>(
        string mediaFilePath,
        Func<MediaMetadataRetriever, T?> readEmbedded,
        Func<Stream, CancellationToken, Task<T?>> readFile,
        CancellationToken cancellationToken)
        where T : class
    {
        var embedded = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var retriever = new MediaMetadataRetriever();
            try
            {
                SetDataSource(retriever, mediaFilePath);
                return readEmbedded(retriever);
            }
            catch (Exception)
            {
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (embedded is not null)
        {
            return embedded;
        }

        foreach (var name in FolderArtworkNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artworkPath = await ResolveFileRelativeToAsync(mediaFilePath, name, cancellationToken)
                .ConfigureAwait(false);
            if (artworkPath is null)
            {
                continue;
            }

            await using var stream = await OpenMediaFileAsync(artworkPath, cancellationToken).ConfigureAwait(false);
            if (await readFile(stream, cancellationToken).ConfigureAwait(false) is { } artwork)
            {
                return artwork;
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    // Copies the picture out of the Java array without the managed array the binding would return.
    private static unsafe ArtworkBuffer? ReadEmbeddedPicture(MediaMetadataRetriever retriever)
    {
        JniObjectReference array = retriever.JniPeerMembers.InstanceMethods.InvokeVirtualObjectMethod(
            "getEmbeddedPicture.()[B", retriever, null);
        try
        {
            int length = array.IsValid ? JniEnvironment.Arrays.GetArrayLength(array) : 0;
            if (length <= 0)
            {
                return null;
            }

            var buffer = ArtworkBuffer.Allocate(length);
            try
            {
                fixed (byte* pointer = buffer.Span)
                {
                    JniEnvironment.Arrays.GetByteArrayRegion(array, 0, length, (sbyte*)pointer);
                }

                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        finally
        {
            JniObjectReference.Dispose(ref array);
        }
    }

    public async Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        // MediaMetadataRetriever hands back hardware-decoded frames and cached system thumbnails,
        // which is far cheaper on a phone than opening the file with FFmpeg.
        var native = await Task.Run(() => ProbeWithRetriever(mediaFilePath, withPreview), cancellationToken)
            .ConfigureAwait(false);
        if (native is not null)
        {
            return native;
        }

        // Formats the platform decoders do not know still index through the software path.
        await using var stream = await OpenMediaFileAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => VideoPreviewFactory.Probe(stream, withPreview, leaveOpen: true), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioStreamProbeInfo>>([]);
    }

    public Task<ImageProbeInfo?> ProbeImageAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        PhotoFolderService.ProbeMetadataAsync(this, mediaFilePath, cancellationToken);

    public async Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenMediaFileAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return MediaProbeFactory.Inspect(stream, leaveOpen: true);
    }

    private VideoProbeInfo? ProbeWithRetriever(string mediaFilePath, bool withPreview)
    {
        using var retriever = new MediaMetadataRetriever();
        try
        {
            SetDataSource(retriever, mediaFilePath);

            if (retriever.ExtractMetadata(MetadataKey.HasVideo) != "yes")
            {
                return null;
            }
            if (withPreview && (!OperatingSystem.IsAndroidVersionAtLeast(30) ||
                retriever.ExtractMetadata(MetadataKey.ColorTransfer) != "3" ||
                retriever.ExtractMetadata(MetadataKey.ColorStandard) == "6" ||
                retriever.ExtractMetadata(MetadataKey.Mimetype) == "video/dolby-vision"))
                return null;

            var width = int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoWidth), out var w) ? w : 0;
            var height = int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoHeight), out var h) ? h : 0;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var durationMs = long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? ms : 0L;
            var duration = TimeSpan.FromMilliseconds(durationMs);
            var position = LightStudio.FfmpegShim.Media.VideoProbe.ChoosePreviewPosition(duration);

            byte[]? preview = null;
            var previewWidth = 0;
            var previewHeight = 0;
            if (withPreview)
            {
                preview = ExtractFrame(retriever, position, out previewWidth, out previewHeight);
            }

            return new VideoProbeInfo
            {
                Duration = duration,
                Width = width,
                Height = height,
                FrameRate = double.TryParse(
                    retriever.ExtractMetadata(MetadataKey.CaptureFramerate),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var fps) ? fps : 0,
                VideoCodec = retriever.ExtractMetadata(MetadataKey.Mimetype),
                Title = retriever.ExtractMetadata(MetadataKey.Title),
                Preview = preview,
                PreviewWidth = previewWidth,
                PreviewHeight = previewHeight,
                PreviewPosition = position,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? ExtractFrame(
        MediaMetadataRetriever retriever,
        TimeSpan position,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var frame = retriever.GetScaledFrameAtTime(
                (long)position.TotalMilliseconds * 1000,
                Option.ClosestSync,
                VideoPreviewFactory.PreviewMaxDimension,
                VideoPreviewFactory.PreviewMaxDimension);
            if (frame is null)
            {
                return null;
            }

            width = frame.Width;
            height = frame.Height;
            using var buffer = new MemoryStream();
            if (!frame.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 82, buffer))
            {
                return null;
            }

            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public T GetConfigurationValue<T>(string key)
    {
        lock (configurationLock)
        {
            if (!configuration.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return default!;
            }

            return value.Deserialize((JsonTypeInfo<T>)ConfigurationJsonContext.Default.GetTypeInfo(typeof(T))!)!;
        }
    }

    public void SetConfigurationValue<T>(string key, T value)
    {
        lock (configurationLock)
        {
            configuration[key] = JsonSerializer.SerializeToElement(value, (JsonTypeInfo<T>)ConfigurationJsonContext.Default.GetTypeInfo(typeof(T))!);
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Whether the identifier is a legacy filesystem path (e.g. <c>/sdcard/Music</c>) rather than a
    /// Storage Access Framework URI, which always carries a scheme.
    /// </summary>
    private static bool IsFileSystemPath(string path) => path.StartsWith('/');

    public Task<string> NormalizePathAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizePath(path);
            cancellationToken.ThrowIfCancellationRequested();
            return normalized;
        }, cancellationToken);

    internal string NormalizePath(string path)
    {
        if (AndroidStoragePath.TryParse(path, out var storagePath))
        {
            return storagePath.Path;
        }

        if (!AndroidStoragePath.TryParseDocumentUri(path, out var tree, out var documentId))
        {
            if (path.StartsWith("saf:", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Invalid storage path: {path}");
            }

            return path;
        }

        if (documentId == tree.TreeId)
        {
            return tree.RootPath;
        }

        if (tree.ExternalPath(documentId) is { } externalPath)
        {
            return externalPath;
        }

        var documentUri = tree.DocumentUri(documentId);
        if (documentPaths.TryGetValue(documentUri, out var rememberedPath))
        {
            return rememberedPath;
        }

        using var nativeUri = AndroidUri.Parse(documentUri)!;
        using var documentPath = DocumentsContract.FindDocumentPath(context.ContentResolver!, nativeUri);
        var ids = documentPath?.GetPath();
        if (ids is not { Count: > 0 } || ids[0] != tree.TreeId || ids[^1] != documentId ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new IOException($"The document provider did not return a path within the selected tree: {path}");
        }

        var result = tree.RootPath;
        foreach (var ancestorId in ids.Skip(1))
        {
            var ancestorUri = tree.DocumentUri(ancestorId);
            using var nativeAncestor = AndroidUri.Parse(ancestorUri)!;
            using var cursor = context.ContentResolver!.Query(
                nativeAncestor, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
            if (cursor?.MoveToFirst() != true || cursor.GetString(0) is not { Length: > 0 } name)
            {
                throw new FileNotFoundException($"Could not read a document on the path to '{path}'.");
            }

            result = AndroidStoragePath.Append(result, name);
            RememberDocumentPath(result, ancestorUri);
        }

        return result;
    }

    internal AndroidUri? ToDocumentUri(string path)
    {
        if (!AndroidStoragePath.TryParse(path, out var storagePath))
        {
            if (AndroidStoragePath.TryParseDocumentUri(path, out var tree, out var documentId))
            {
                return AndroidUri.Parse(tree.DocumentUri(documentId));
            }

            var native = AndroidUri.Parse(path);
            return native?.Scheme == ContentResolver.SchemeContent ? native : null;
        }

        if (storagePath.ExternalDocumentId() is { } externalId)
        {
            return AndroidUri.Parse(storagePath.DocumentUri(externalId));
        }

        if (documentPaths.TryGetValue(storagePath.Path, out var rememberedUri))
        {
            return AndroidUri.Parse(rememberedUri);
        }

        var currentId = storagePath.TreeId;
        var currentPath = storagePath.RootPath;
        using var treeUri = AndroidUri.Parse(storagePath.TreeUri)!;
        foreach (var name in storagePath.Names)
        {
            using var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, currentId)
                ?? throw new IOException($"Could not address the children of '{currentPath}'.");
            using var cursor = context.ContentResolver!.Query(childrenUri, ChildProjection, null, null, null)
                ?? throw new IOException($"Could not list storage folder '{currentPath}'.");
            string? childId = null;
            while (cursor.MoveToNext())
            {
                if (!string.Equals(cursor.GetString(2), name, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = cursor.GetString(0);
                if (childId is not null && childId != candidate)
                {
                    throw new IOException($"The document provider returned more than one item named '{name}'.");
                }

                childId = candidate;
            }

            currentId = childId ?? throw new FileNotFoundException($"Storage item not found: {path}");
            currentPath = AndroidStoragePath.Append(currentPath, name);
            RememberDocumentPath(currentPath, storagePath.DocumentUri(currentId));
        }

        return AndroidUri.Parse(storagePath.DocumentUri(currentId));
    }

    internal string? GetParentPath(string path)
    {
        if (GetMediaStoreFilePath(path) is { } filePath)
        {
            return Path.GetDirectoryName(filePath);
        }

        var normalized = NormalizePath(path);
        return AndroidStoragePath.TryParse(normalized, out var storagePath) && storagePath.Names.Count > 0
            ? normalized[..normalized.LastIndexOf('/')]
            : null;
    }

    internal string? GetMediaStoreFilePath(string path)
    {
        if (!path.StartsWith("content://media/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var uri = AndroidUri.Parse(path)!;
        using var cursor = context.ContentResolver!.Query(uri, ["_data"], null, null, null);
        return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
    }

    private void RememberDocumentPath(string path, string documentUri)
    {
        if (documentPaths.Count >= 8192)
        {
            documentPaths.Clear();
        }

        documentPaths[path] = documentUri;
        documentPaths[documentUri] = path;
    }

    private static string? ResolveFileSystemReference(string sourceFilePath, string referencedFileName)
    {
        try
        {
            var directory = Path.GetDirectoryName(sourceFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var platformReference = referencedFileName.Replace('\\', '/');
            var candidate = Path.IsPathRooted(platformReference)
                ? platformReference
                : Path.Combine(directory, platformReference);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var targetName = PortableFileName(referencedFileName);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(file), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string PortableFileName(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private void SetDataSource(MediaMetadataRetriever retriever, string mediaFilePath)
    {
        if (IsFileSystemPath(mediaFilePath))
        {
            retriever.SetDataSource(mediaFilePath);
            return;
        }

        var uri = ToDocumentUri(mediaFilePath)
            ?? throw new FileNotFoundException($"Invalid content URI: {mediaFilePath}");
        retriever.SetDataSource(context, uri);
    }

    private void EnumerateChildren(
        string path,
        Action<string>? onFolder = null,
        Action<string, string, DateTime, long>? onFile = null)
    {
        var normalizedPath = NormalizePath(path);
        var uri = ToDocumentUri(normalizedPath);
        if (uri is null)
        {
            throw new DirectoryNotFoundException($"Invalid storage folder: {path}");
        }

        var documentId = DocumentsContract.IsDocumentUri(context, uri)
            ? DocumentsContract.GetDocumentId(uri)
            : DocumentsContract.GetTreeDocumentId(uri);
        using var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(uri, documentId)
            ?? throw new IOException($"Could not address the children of '{path}'.");
        using var cursor = context.ContentResolver!.Query(childrenUri, ChildProjection, null, null, null)
            ?? throw new IOException($"Could not list storage folder '{path}'.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        while (cursor.MoveToNext())
        {
            var childId = cursor.GetString(0);
            if (string.IsNullOrEmpty(childId))
            {
                continue;
            }

            var mimeType = cursor.GetString(1);
            var displayName = cursor.GetString(2)
                ?? throw new IOException($"The document provider did not name '{childId}'.");
            if (!names.Add(displayName))
            {
                throw new IOException($"The document provider returned more than one item named '{displayName}'.");
            }
            var lastModifiedMs = cursor.IsNull(3) ? 0L : cursor.GetLong(3);
            var sizeInBytes = cursor.IsNull(4) ? 0L : cursor.GetLong(4);
            var childUri = DocumentsContract.BuildDocumentUriUsingTree(uri, childId)?.ToString();
            if (childUri is null)
            {
                continue;
            }

            var childPath = AndroidStoragePath.Append(normalizedPath, displayName);
            RememberDocumentPath(childPath, childUri);
            if (mimeType == DocumentsContract.Document.MimeTypeDir)
            {
                onFolder?.Invoke(childPath);
            }
            else
            {
                var lastModified = lastModifiedMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMs).UtcDateTime
                    : DateTime.UnixEpoch;
                onFile?.Invoke(displayName, childPath, lastModified, sizeInBytes);
            }
        }
    }

    private void LoadConfiguration()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        var loaded = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigurationJsonContext.Default.DictionaryStringJsonElement);
        configuration = loaded is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveConfiguration()
    {
        Directory.CreateDirectory(LocalDataPath);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(configuration, ConfigurationJsonContext.Default.DictionaryStringJsonElement));
    }

    private sealed class MediaFile(string name, string path, DateTime lastModifiedUtc, long sizeInBytes) : IFile
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public DateTime LastModifiedUtc { get; } = lastModifiedUtc;

        public long SizeInBytes { get; } = sizeInBytes;
    }
}

internal sealed class AndroidStoragePath(string authority, string treeId, IReadOnlyList<string> names)
{
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    public string Authority { get; } = authority;
    public string TreeId { get; } = treeId;
    public IReadOnlyList<string> Names { get; } = names;
    public string RootPath => $"saf://{Authority}/{Uri.EscapeDataString(TreeId)}";
    public string TreeUri => $"content://{Authority}/tree/{Uri.EscapeDataString(TreeId)}";
    public string Path => Names.Aggregate(RootPath, Append);

    public static bool TryParse(string path, [NotNullWhen(true)] out AndroidStoragePath? result)
    {
        result = null;
        if (!TryParseUri(path, "saf", out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length < 2 || string.IsNullOrEmpty(segments[1]))
        {
            return false;
        }

        var names = segments.Skip(2).Select(Uri.UnescapeDataString).ToArray();
        if (names.Any(name => string.IsNullOrEmpty(name) || name is "." or ".."))
        {
            return false;
        }

        result = new AndroidStoragePath(uri.Authority, Uri.UnescapeDataString(segments[1]), names);
        return true;
    }

    public static bool TryParseDocumentUri(
        string path,
        [NotNullWhen(true)] out AndroidStoragePath? tree,
        [NotNullWhen(true)] out string? documentId)
    {
        tree = null;
        documentId = null;
        if (!TryParseUri(path, "content", out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length is not (3 or 5) || segments[1] != "tree" ||
            string.IsNullOrEmpty(segments[2]) ||
            (segments.Length == 5 && (segments[3] != "document" || string.IsNullOrEmpty(segments[4]))))
        {
            return false;
        }

        var treeId = Uri.UnescapeDataString(segments[2]);
        tree = new AndroidStoragePath(uri.Authority, treeId, []);
        documentId = segments.Length == 5 ? Uri.UnescapeDataString(segments[4]) : treeId;
        return true;
    }

    public string DocumentUri(string documentId) =>
        $"{TreeUri}/document/{Uri.EscapeDataString(documentId)}";

    public string? ExternalDocumentId()
    {
        if (!string.Equals(Authority, ExternalStorageAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Names.Any(name => name.Contains('/')))
        {
            throw new IOException("An external-storage filename cannot contain a path separator.");
        }

        return Names.Count == 0 ? TreeId :
            TreeId + (TreeId.EndsWith(':') ? string.Empty : "/") + string.Join('/', Names);
    }

    public string? ExternalPath(string documentId)
    {
        if (!string.Equals(Authority, ExternalStorageAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (documentId == TreeId)
        {
            return RootPath;
        }

        var prefix = TreeId + (TreeId.EndsWith(':') ? string.Empty : "/");
        if (!documentId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The document is outside the selected storage tree.");
        }

        return documentId[prefix.Length..].Split('/').Aggregate(RootPath, Append);
    }

    public static string Append(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            throw new IOException("The document provider returned an invalid filename.");
        }

        return $"{parent}/{Uri.EscapeDataString(name)}";
    }

    private static bool TryParseUri(string path, string scheme, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(path, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }, out uri) &&
        uri.IsAbsoluteUri && uri.Scheme == scheme && !string.IsNullOrEmpty(uri.Authority) &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
        !path.Contains('?') && !path.Contains('#');
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext
{
}
