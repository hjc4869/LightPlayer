#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using AndroidUri = Android.Net.Uri;

namespace LightStudio.LightPlayer.Android.Services;

internal sealed record MediaStoreSnapshot<T>(string Id, T[] Items);

internal sealed record MediaStoreTrack(DbMediaFile File, string Folder, string Root);

internal sealed class AndroidMediaStoreCatalog
{
    private static readonly string[] CommonProjection =
    [
        "_id", "_display_name", "_size", "date_added", "date_modified", "volume_name", "relative_path", "_data",
    ];

    private readonly Context context;
    private readonly CatalogCache<MediaStoreTrack> music;
    private readonly CatalogCache<IndexedMediaFile> media;

    public AndroidMediaStoreCatalog(Context context)
    {
        this.context = context.ApplicationContext ?? context;
        music = new CatalogCache<MediaStoreTrack>(ReadMusic);
        media = new CatalogCache<IndexedMediaFile>(ReadMedia);
    }

    public Task<MediaStoreSnapshot<MediaStoreTrack>> GetMusicAsync(CancellationToken cancellationToken = default) =>
        music.GetAsync(cancellationToken);

    public Task<MediaStoreSnapshot<IndexedMediaFile>> GetMediaAsync(CancellationToken cancellationToken = default) =>
        media.GetAsync(cancellationToken);

    public void Invalidate()
    {
        music.Invalidate();
        media.Invalidate();
    }

    public Task<byte[]?> GetThumbnailAsync(string path, CancellationToken cancellationToken) => Task.Run<byte[]?>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var uri = AndroidUri.Parse(path) ?? throw new ArgumentException("Invalid media URI.", nameof(path));
        using var size = new global::Android.Util.Size(512, 512);
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);
        try
        {
            using var bitmap = context.ContentResolver!.LoadThumbnail(uri, size, signal);
            if (bitmap is null)
                return null;
            using var buffer = new MemoryStream();
            return bitmap.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 82, buffer)
                ? buffer.ToArray() : null;
        }
        catch (global::Android.OS.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new System.OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is Java.IO.IOException or Java.Lang.SecurityException)
        {
            AppLog.Warning("Android.MediaStore", $"Could not read the system thumbnail for {path}.", exception);
            return null;
        }
    }, cancellationToken);

    public bool HasAudioAccess => HasPermission(global::Android.Manifest.Permission.ReadMediaAudio);

    public bool HasImageAccess => HasPermission(global::Android.Manifest.Permission.ReadMediaImages) || HasSelectedAccess;

    public bool HasVideoAccess => HasPermission(global::Android.Manifest.Permission.ReadMediaVideo) || HasSelectedAccess;

    private bool HasSelectedAccess => HasPermission(global::Android.Manifest.Permission.ReadMediaVisualUserSelected);

    private bool HasPermission(string permission) => context.CheckSelfPermission(permission) == Permission.Granted;

    private MediaStoreTrack[] ReadMusic(CancellationToken cancellationToken)
    {
        if (!HasAudioAccess)
        {
            return [];
        }

        var tracks = new List<MediaStoreTrack>();
        ReadRows(MediaStore.Audio.Media.ExternalContentUri!,
            [.. CommonProjection, "title", "artist", "album", "album_artist", "album_id", "duration", "year", "track", "composer", "genre"],
            "is_music != 0", row =>
            {
                var artist = Tag(row.Text("artist"));
                var albumArtist = Tag(row.Text("album_artist"));
                var trackNumber = row.Number("track");
                var file = new DbMediaFile
                {
                    Id = tracks.Count + 1,
                    Path = row.ContentPath("audio"),
                    Title = Tag(row.Text("title")) is { Length: > 0 } title ? title : row.Text("_display_name"),
                    Artist = artist,
                    Album = Tag(row.Text("album")),
                    AlbumArtist = albumArtist.Length > 0 ? albumArtist : artist,
                    AlbumGroupingKey = $"{row.Text("volume_name")}:{row.Number("album_id")}",
                    Genre = Tag(row.Text("genre")),
                    Composer = Tag(row.Text("composer")),
                    Date = PositiveNumber(row.Number("year")),
                    DiscNumber = PositiveNumber(trackNumber / 1000),
                    TrackNumber = PositiveNumber(trackNumber % 1000),
                    TotalDiscs = string.Empty,
                    TotalTracks = string.Empty,
                    Description = string.Empty,
                    Copyright = string.Empty,
                    Comments = string.Empty,
                    Grouping = string.Empty,
                    Performer = string.Empty,
                    Duration = Duration(row.Number("duration")),
                    DatabaseItemAddedDate = Timestamp(row.Number("date_added")),
                    FileLastModifiedDate = Timestamp(row.Number("date_modified")),
                };
                var (folder, root) = row.Location();
                tracks.Add(new MediaStoreTrack(file, folder, root));
            }, cancellationToken);
        return tracks.ToArray();
    }

    private IndexedMediaFile[] ReadMedia(CancellationToken cancellationToken)
    {
        var items = new List<IndexedMediaFile>();
        if (HasImageAccess)
        {
            ReadRows(MediaStore.Images.Media.ExternalContentUri!,
                [.. CommonProjection, "width", "height", "datetaken", "orientation"], null, row =>
                {
                    var (folder, root) = row.Location();
                    var rotated = row.Number("orientation") is 90 or 270;
                    var taken = CaptureTime(row.Number("datetaken"));
                    items.Add(new IndexedMediaFile(Image: new DbImageFile
                    {
                        Id = items.Count + 1,
                        Path = row.ContentPath("images"),
                        FileName = row.Text("_display_name"),
                        FolderPath = folder,
                        LibraryRoot = root,
                        FileSize = row.Number("_size"),
                        Width = (int)row.Number(rotated ? "height" : "width"),
                        Height = (int)row.Number(rotated ? "width" : "height"),
                        DateTaken = taken,
                        FileCreatedDate = taken ?? Timestamp(row.Number("date_added")),
                        FileLastModifiedDate = Timestamp(row.Number("date_modified")),
                        DatabaseItemAddedDate = Timestamp(row.Number("date_added")),
                    }));
                }, cancellationToken);
        }

        if (HasVideoAccess)
        {
            ReadRows(MediaStore.Video.Media.ExternalContentUri!,
                [.. CommonProjection, "width", "height", "datetaken", "duration", "title"], null, row =>
                {
                    var (folder, root) = row.Location();
                    var duration = Duration(row.Number("duration"));
                    var size = row.Number("_size");
                    items.Add(new IndexedMediaFile(Video: new DbVideoFile
                    {
                        Id = items.Count + 1,
                        Path = row.ContentPath("video"),
                        FileName = row.Text("_display_name"),
                        Title = row.Text("title"),
                        FolderPath = folder,
                        LibraryRoot = root,
                        FileSize = size,
                        Width = (int)row.Number("width"),
                        Height = (int)row.Number("height"),
                        Duration = duration,
                        BitRate = duration.TotalSeconds > 0 ? (long)(size * 8.0 / duration.TotalSeconds) : 0,
                        FileCreatedDate = CaptureTime(row.Number("datetaken")) ?? Timestamp(row.Number("date_added")),
                        FileLastModifiedDate = Timestamp(row.Number("date_modified")),
                        DatabaseItemAddedDate = Timestamp(row.Number("date_added")),
                    }));
                }, cancellationToken);
        }

        return items.ToArray();
    }

    private void ReadRows(AndroidUri collection, string[] projection, string? selection, Action<MediaRow> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);
        try
        {
            using var cursor = context.ContentResolver!.Query(collection, projection,
                "is_pending = 0 AND is_trashed = 0" + (selection is null ? string.Empty : $" AND ({selection})"),
                null, "_id ASC", signal)
                ?? throw new IOException($"The system media library could not query {collection}.");
            var row = new MediaRow(cursor, projection);
            while (cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                read(row);
            }
        }
        catch (global::Android.OS.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new System.OperationCanceledException(cancellationToken);
        }
    }

    private static string Tag(string value) => value == "<unknown>" ? string.Empty : value;

    private static string PositiveNumber(long value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static TimeSpan Duration(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, (long)TimeSpan.MaxValue.TotalMilliseconds));

    private static DateTimeOffset Timestamp(long seconds) => seconds is > 0 and <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
        : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? CaptureTime(long milliseconds) => milliseconds is > 0 and <= 253402300799999
        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime()
        : null;

    private sealed class MediaRow(ICursor cursor, string[] projection)
    {
        private readonly Dictionary<string, int> columns = projection.ToDictionary(
            name => name, cursor.GetColumnIndexOrThrow, StringComparer.Ordinal);

        public string Text(string column) => cursor.GetString(columns[column]) ?? string.Empty;

        public long Number(string column) => cursor.IsNull(columns[column]) ? 0 : cursor.GetLong(columns[column]);

        public string ContentPath(string kind)
        {
            var volume = Text("volume_name");
            return $"content://media/{(volume.Length > 0 ? volume : "external")}/{kind}/media/{Number("_id").ToString(CultureInfo.InvariantCulture)}";
        }

        public (string Folder, string Root) Location()
        {
            var relative = Text("relative_path").Trim('/');
            var folder = Path.GetDirectoryName(Text("_data"));
            if (string.IsNullOrEmpty(folder))
            {
                folder = $"/{Text("volume_name")}/{relative}".TrimEnd('/');
            }

            var topFolder = relative.Split('/')[0];
            var root = relative.Length > 0 && folder.EndsWith(relative, StringComparison.Ordinal)
                ? folder[..^relative.Length] + topFolder
                : folder;
            return (folder, root);
        }
    }

    private sealed class CatalogCache<T>(Func<CancellationToken, T[]> load)
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim loading = new(1, 1);
        private MediaStoreSnapshot<T>? snapshot;
        private long generation;

        public async Task<MediaStoreSnapshot<T>> GetAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (snapshot is not null)
                        return snapshot;
                }

                await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    long version;
                    lock (gate)
                    {
                        if (snapshot is not null)
                            return snapshot;
                        version = generation;
                    }

                    var items = await Task.Run(() => load(cancellationToken), cancellationToken).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (version == generation)
                            return snapshot = new MediaStoreSnapshot<T>(Guid.NewGuid().ToString("N"), items);
                    }
                }
                finally
                {
                    loading.Release();
                }
            }
        }

        public void Invalidate()
        {
            lock (gate)
            {
                generation++;
                snapshot = null;
            }
        }
    }
}
