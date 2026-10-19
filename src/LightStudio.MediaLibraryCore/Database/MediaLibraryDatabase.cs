using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.Strings;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>
/// Awaitable library queries over a replaceable SQLite connection owner.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a scoped service: each DI scope owns a single connection that is
/// opened asynchronously and disposed with the scope. Native connections execute
/// on background workers; browser connections use the independent SQLite worker.
/// </para>
/// <para>
/// <see cref="TimeSpan"/> and <see cref="DateTimeOffset"/> values are serialized
/// with stable textual formats to keep existing library databases compatible.
/// </para>
/// </remarks>
public sealed class MediaLibraryDatabase : IAsyncDisposable
{
    private const string BrowseCollation = "LIGHTPLAYER_NOCASE";

    /// <summary>SQLite storage format for <see cref="DateTimeOffset"/> values.</summary>
    private const string DateTimeOffsetFormat = @"yyyy\-MM\-dd HH\:mm\:ss.FFFFFFFzzz";

    private string MediaFileColumns =>
        "Id, Duration, TotalDiscs, DiscNumber, TotalTracks, Description, Copyright, " +
        "Comments, Grouping, Genre, TrackNumber, AlbumArtist, Performer, Composer, " +
        "Date, Album, Artist, Title, Path, DatabaseItemAddedDate, FileLastModifiedDate, StartTime, " +
        AlbumGroupExpression + " AS AlbumGroupingKey";

    private const string VideoFileColumns =
        "Id, Path, FolderPath, LibraryRoot, FileName, Title, Duration, Width, Height, FrameRate, " +
        "VideoCodec, FileSize, BitRate, VideoStreamIndex, MediaKind, DiscTitle, AudioStreams, " +
        "ExternalAudioPaths, ExternalSubtitlePaths, DatabaseItemAddedDate, FileLastModifiedDate";

    private const string ImageFileColumns =
        "Id, Path, FolderPath, LibraryRoot, FileName, FileSize, Width, Height, DateTaken, " +
        "FileCreatedDate, FileLastModifiedDate, DatabaseItemAddedDate, Latitude, Longitude, " +
        "Country, State, City, LocationLanguage";

    private readonly IAsyncSqliteConnection _connection;
    private IAsyncSqliteTransaction _transaction;

    public AlbumGroupingMode AlbumGrouping { get; set; }

    /// <summary>
    /// Creates a data-access instance for the supplied ADO.NET connection string
    /// (e.g. <c>Data source=/path/library-v1.sqlite</c>).
    /// </summary>
    public MediaLibraryDatabase(string connectionString, AlbumGroupingMode albumGroupingMode = AlbumGroupingMode.SameFolder)
    {
        _connection = SqliteDatabase.CreateConnection(connectionString);
        AlbumGrouping = albumGroupingMode;
    }

    private IAsyncSqliteConnection Connection => _connection;

    internal static void ConfigureConnection(SqliteConnection connection)
    {
        connection.CreateCollation(
            BrowseCollation,
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        connection.CreateFunction<string, int>(
            "lightplayer_leading_int",
            ParseLeadingInt,
            isDeterministic: true);
        connection.CreateFunction<string, string>(
            "lightplayer_parent_path",
            Path.GetDirectoryName,
            isDeterministic: true);
        connection.CreateFunction<string, string, string>(
            "lightplayer_track_title",
            TrackTitle,
            isDeterministic: true);
        connection.CreateFunction<string, string, string>(
            "lightplayer_video_title",
            VideoTitle,
            isDeterministic: true);
        connection.CreateFunction<long, long>(
            "lightplayer_shuffle_key",
            ShuffleKey,
            isDeterministic: true);
        connection.CreateFunction<string, long>(
            "lightplayer_utc_ticks",
            ParseUtcTicks,
            isDeterministic: true);
        connection.CreateFunction<string, long>(
            "lightplayer_duration_ticks",
            static value => ParseTimeSpan(value).Ticks,
            isDeterministic: true);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
    }

    #region Schema

    /// <summary>
    /// Creates the library tables and indexes if they do not already exist while
    /// preserving the schema used by existing library databases.
    /// </summary>
    public async Task EnsureSchemaCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(@"
CREATE TABLE IF NOT EXISTS ""MediaFiles"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_MediaFiles"" PRIMARY KEY AUTOINCREMENT,
    ""Duration"" TEXT NOT NULL,
    ""TotalDiscs"" TEXT NULL,
    ""DiscNumber"" TEXT NULL,
    ""TotalTracks"" TEXT NULL,
    ""Description"" TEXT NULL,
    ""Copyright"" TEXT NULL,
    ""Comments"" TEXT NULL,
    ""Grouping"" TEXT NULL,
    ""Genre"" TEXT NULL,
    ""TrackNumber"" TEXT NULL,
    ""AlbumArtist"" TEXT NULL,
    ""Performer"" TEXT NULL,
    ""Composer"" TEXT NULL,
    ""Date"" TEXT NULL,
    ""Album"" TEXT NULL,
    ""Artist"" TEXT NULL,
    ""Title"" TEXT NULL,
    ""Path"" TEXT NULL,
    ""DatabaseItemAddedDate"" TEXT NOT NULL,
    ""FileLastModifiedDate"" TEXT NOT NULL,
    ""StartTime"" INTEGER NULL
);
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_Path"" ON ""MediaFiles"" (""Path"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_Folder"" ON ""MediaFiles"" (lightplayer_parent_path(""Path""));
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_Album"" ON ""MediaFiles"" (""Album"", ""AlbumArtist"", ""DiscNumber"", ""TrackNumber"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_Artist"" ON ""MediaFiles"" (""Artist"", ""Album"", ""DiscNumber"", ""TrackNumber"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_AlbumArtist"" ON ""MediaFiles"" (""AlbumArtist"", ""Album"", ""DiscNumber"", ""TrackNumber"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseTitle"" ON ""MediaFiles"" (lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseAlbum"" ON ""MediaFiles"" (COALESCE(""Album"", '') COLLATE LIGHTPLAYER_NOCASE, lightplayer_leading_int(""DiscNumber""), lightplayer_leading_int(""TrackNumber""), lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseArtist"" ON ""MediaFiles"" (COALESCE(""Artist"", '') COLLATE LIGHTPLAYER_NOCASE, lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseGenre"" ON ""MediaFiles"" (COALESCE(""Genre"", '') COLLATE LIGHTPLAYER_NOCASE, lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseYear"" ON ""MediaFiles"" (lightplayer_leading_int(""Date""), lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_BrowseDateAdded"" ON ""MediaFiles"" (lightplayer_utc_ticks(""DatabaseItemAddedDate""), lightplayer_track_title(""Title"", ""Path"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_MediaFiles_Shuffle"" ON ""MediaFiles"" (lightplayer_shuffle_key(""Id""), ""Id"");
CREATE TABLE IF NOT EXISTS ""VideoFiles"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_VideoFiles"" PRIMARY KEY AUTOINCREMENT,
    ""Path"" TEXT NOT NULL,
    ""FolderPath"" TEXT NULL,
    ""LibraryRoot"" TEXT NULL,
    ""FileName"" TEXT NULL,
    ""Title"" TEXT NULL,
    ""Duration"" TEXT NOT NULL,
    ""Width"" INTEGER NOT NULL,
    ""Height"" INTEGER NOT NULL,
    ""FrameRate"" REAL NOT NULL,
    ""VideoCodec"" TEXT NULL,
    ""FileSize"" INTEGER NOT NULL,
    ""BitRate"" INTEGER NOT NULL,
    ""VideoStreamIndex"" INTEGER NOT NULL,
    ""MediaKind"" INTEGER NOT NULL DEFAULT 0,
    ""DiscTitle"" INTEGER NOT NULL DEFAULT 0,
    ""AudioStreams"" TEXT NULL,
    ""ExternalAudioPaths"" TEXT NULL,
    ""ExternalSubtitlePaths"" TEXT NULL,
    ""DatabaseItemAddedDate"" TEXT NOT NULL,
    ""FileLastModifiedDate"" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_VideoFiles_Path"" ON ""VideoFiles"" (""Path"");
CREATE INDEX IF NOT EXISTS ""IX_VideoFiles_FolderPath"" ON ""VideoFiles"" (""FolderPath"", ""FileName"");
CREATE INDEX IF NOT EXISTS ""IX_VideoFiles_BrowseName"" ON ""VideoFiles"" (lightplayer_video_title(""Title"", ""FileName"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_VideoFiles_FolderBrowseName"" ON ""VideoFiles"" (""FolderPath"", lightplayer_video_title(""Title"", ""FileName"") COLLATE LIGHTPLAYER_NOCASE, ""Id"");
CREATE TABLE IF NOT EXISTS ""ImageFiles"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ImageFiles"" PRIMARY KEY AUTOINCREMENT,
    ""Path"" TEXT NOT NULL,
    ""FolderPath"" TEXT NULL,
    ""LibraryRoot"" TEXT NULL,
    ""FileName"" TEXT NOT NULL,
    ""FileSize"" INTEGER NOT NULL,
    ""Width"" INTEGER NOT NULL,
    ""Height"" INTEGER NOT NULL,
    ""DateTaken"" TEXT NULL,
    ""FileCreatedDate"" TEXT NULL,
    ""FileLastModifiedDate"" TEXT NOT NULL,
    ""DatabaseItemAddedDate"" TEXT NOT NULL,
    ""Latitude"" REAL NULL,
    ""Longitude"" REAL NULL,
    ""Country"" TEXT NULL,
    ""State"" TEXT NULL,
    ""City"" TEXT NULL,
    ""LocationLanguage"" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ImageFiles_Path"" ON ""ImageFiles"" (""Path"");
CREATE INDEX IF NOT EXISTS ""IX_ImageFiles_FolderPath"" ON ""ImageFiles"" (""FolderPath"", ""FileName"");
CREATE INDEX IF NOT EXISTS ""IX_ImageFiles_BrowseName"" ON ""ImageFiles"" (""FileName"" COLLATE LIGHTPLAYER_NOCASE, ""FileName"" COLLATE BINARY, ""Id"");");
        await cmd.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
        await EnsureVideoDiscColumnsAsync(cancellationToken: cancellationToken);
    }

    private async Task EnsureVideoDiscColumnsAsync(CancellationToken cancellationToken = default)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = CreateCommand("PRAGMA table_info(\"VideoFiles\");"))
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("MediaKind"))
        {
            await using var addMediaKind = CreateCommand(
                            "ALTER TABLE VideoFiles ADD COLUMN MediaKind INTEGER NOT NULL DEFAULT 0;");
            await addMediaKind.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
        }

        if (!columns.Contains("DiscTitle"))
        {
            await using var addDiscTitle = CreateCommand(
                            "ALTER TABLE VideoFiles ADD COLUMN DiscTitle INTEGER NOT NULL DEFAULT 0;");
            await addDiscTitle.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
        }
    }

    #endregion

    #region Media file queries

    /// <summary>Returns the number of tracks in the library or directly inside a folder.</summary>
    public async Task<int> CountMediaFilesAsync(string folderPath = null, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(folderPath is null
                    ? "SELECT COUNT(*) FROM MediaFiles;"
                    : "SELECT COUNT(*) FROM MediaFiles WHERE lightplayer_parent_path(Path) = $folder;");
        if (folderPath is not null)
        {
            cmd.Parameters.AddWithValue("$folder", folderPath);
        }
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<List<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "SELECT DISTINCT lightplayer_parent_path(Path) AS FolderPath FROM MediaFiles " +
                    "WHERE Path IS NOT NULL ORDER BY FolderPath;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        var folders = new List<string>();
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.IsDBNull(0))
            {
                folders.Add(reader.GetString(0));
            }
        }
        return folders;
    }

    /// <summary>Loads every media file. The reader is fully consumed before returning.</summary>
    public async Task<List<DbMediaFile>> GetAllMediaFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {MediaFileColumns} FROM MediaFiles;");
        return await ReadMediaFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Loads one keyset page in the same deterministic order used by the songs view. The cursor is
    /// opaque to callers and includes the complete ordering key of the last returned row.
    /// </summary>
    public async Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var totalCount = string.IsNullOrEmpty(request.Cursor) ? await CountMediaFilesAsync(request.FolderPath, cancellationToken: cancellationToken) : (int?)null;
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null : DecodeMediaFileCursor(request);
        var order = MediaFilePageOrder(request, cursor);
        var sql = new StringBuilder($"SELECT {MediaFileColumns} FROM MediaFiles");

        if (request.FolderPath is not null)
        {
            sql.Append(" WHERE lightplayer_parent_path(Path) = $folder");
        }

        if (cursor is not null)
        {
            sql.Append(request.FolderPath is null ? " WHERE (" : " AND (");
            for (var keyIndex = 0; keyIndex < order.Count; keyIndex++)
            {
                if (keyIndex > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append('(');
                for (var equalIndex = 0; equalIndex < keyIndex; equalIndex++)
                {
                    sql.Append(order[equalIndex].Expression)
                        .Append(" = $cursor")
                        .Append(equalIndex)
                        .Append(" AND ");
                }

                sql.Append(order[keyIndex].Expression)
                    .Append(order[keyIndex].Ascending ? " > $cursor" : " < $cursor")
                    .Append(keyIndex)
                    .Append(')');
            }
            sql.Append(')');
        }

        sql.Append(" ORDER BY ");
        for (var index = 0; index < order.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            sql.Append(order[index].Expression)
                .Append(order[index].Ascending ? " ASC" : " DESC");
        }
        sql.Append(" LIMIT $limit;");
        await
                using var cmd = CreateCommand(sql.ToString());
        if (request.FolderPath is not null)
        {
            cmd.Parameters.AddWithValue("$folder", request.FolderPath);
        }
        if (cursor is not null)
        {
            for (var index = 0; index < order.Count; index++)
            {
                cmd.Parameters.AddWithValue($"$cursor{index}", order[index].CursorValue);
            }
        }
        cmd.Parameters.AddWithValue("$limit", request.PageSize + 1);

        var files = new List<DbMediaFile>(request.PageSize + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(ReadMediaFile(reader));
        }

        string nextCursor = null;
        if (files.Count > request.PageSize)
        {
            files.RemoveAt(request.PageSize);
            nextCursor = EncodeMediaFileCursor(request, files[^1]);
        }

        return new PagedResult<DbMediaFile>(files.ToArray(), nextCursor, totalCount);
    }

    /// <summary>
    /// Loads a stable shuffled page. The indexed shuffle key is a permutation of integer ids; the
    /// request seed rotates that ordering at a different pivot for each Home page instance.
    /// </summary>
    public async Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null : DecodeShuffleCursor(request);
        var pivot = ShufflePivot(request.Seed);
        var files = new List<DbMediaFile>(request.PageSize + 1);
        var phase = cursor?.Phase ?? 0;
        var afterKey = cursor?.Key ?? 0;
        var afterId = cursor?.Id ?? 0;

        while (files.Count <= request.PageSize && phase < 2)
        {
            var expression = "lightplayer_shuffle_key(Id)";
            var sql = new StringBuilder($"SELECT {MediaFileColumns} FROM MediaFiles WHERE ")
                .Append(expression)
                .Append(phase == 0 ? " >= $pivot" : " < $pivot");
            if (cursor is not null && cursor.Phase == phase)
            {
                sql.Append(" AND (")
                    .Append(expression)
                    .Append(" > $key OR (")
                    .Append(expression)
                    .Append(" = $key AND Id > $id))");
            }

            sql.Append(" ORDER BY ")
                .Append(expression)
                .Append(", Id LIMIT $limit;");
            await
                        using var cmd = CreateCommand(sql.ToString());
            cmd.Parameters.AddWithValue("$pivot", pivot);
            cmd.Parameters.AddWithValue("$key", afterKey);
            cmd.Parameters.AddWithValue("$id", afterId);
            cmd.Parameters.AddWithValue("$limit", request.PageSize + 1 - files.Count);

            var countBefore = files.Count;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(ReadMediaFile(reader));
            }

            if (files.Count > request.PageSize || files.Count > countBefore)
            {
                var last = files[^1];
                afterKey = ShuffleKey(last.Id);
                afterId = last.Id;
            }

            if (files.Count <= request.PageSize)
            {
                phase++;
                cursor = null;
                afterKey = 0;
                afterId = 0;
            }
        }

        string nextCursor = null;
        if (files.Count > request.PageSize)
        {
            files.RemoveAt(request.PageSize);
            var last = files[^1];
            var lastKey = ShuffleKey(last.Id);
            nextCursor = EncodeCursor(
                new ShuffledMediaFilePageCursor(
                    request.Seed,
                    lastKey >= pivot ? 0 : 1,
                    lastKey,
                    last.Id),
                MediaFilePagingJsonContext.Default.ShuffledMediaFilePageCursor);
        }

        var totalCount = string.IsNullOrEmpty(request.Cursor) ? await CountMediaFilesAsync(cancellationToken: cancellationToken) : (int?)null;
        return new PagedResult<DbMediaFile>(files.ToArray(), nextCursor, totalCount);
    }

    /// <summary>
    /// Loads the first <paramref name="limit"/> media files ordered by artist,
    /// album, disc, then track number (SQL <c>ORDER BY</c> + <c>LIMIT</c>).
    /// </summary>
    public async Task<List<DbMediaFile>> GetMediaFilesPreviewAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {MediaFileColumns} FROM MediaFiles " +
                    "ORDER BY Artist, Album, DiscNumber, TrackNumber LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", limit);
        return await ReadMediaFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>Finds a media file by primary key, or <see langword="null"/> if absent.</summary>
    public async Task<DbMediaFile> GetMediaFileByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {MediaFileColumns} FROM MediaFiles WHERE Id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        return await reader.ReadAsync(cancellationToken: cancellationToken) ? ReadMediaFile(reader) : null;
    }

    /// <summary>Finds the first media file with the given path, or <see langword="null"/>.</summary>
    public async Task<DbMediaFile> GetMediaFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {MediaFileColumns} FROM MediaFiles WHERE Path = $path LIMIT 1;");
        cmd.Parameters.AddWithValue("$path", string.IsNullOrEmpty(path) ? DBNull.Value : path);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        return await reader.ReadAsync(cancellationToken: cancellationToken) ? ReadMediaFile(reader) : null;
    }

    /// <summary>
    /// Returns the tracks of an album ordered textually by disc then track number.
    /// A null artist matches a null album artist.
    /// </summary>
    public async Task<List<DbMediaFile>> GetMediaFilesByAlbumAsync(string albumName, string artistName, string albumGroupingKey = null, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {MediaFileColumns} FROM MediaFiles " +
                    "WHERE Album IS $album AND AlbumArtist IS $artist " +
                    (albumGroupingKey is null ? string.Empty : $" AND ({AlbumGroupExpression}) IS $albumGroup ") +
                    "ORDER BY DiscNumber, TrackNumber;");
        cmd.Parameters.AddWithValue("$album", string.IsNullOrEmpty(albumName) ? DBNull.Value : albumName);
        cmd.Parameters.AddWithValue("$artist", string.IsNullOrEmpty(artistName) ? DBNull.Value : artistName);
        if (albumGroupingKey is not null)
        {
            cmd.Parameters.AddWithValue("$albumGroup", albumGroupingKey);
        }
        return await ReadMediaFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns every track credited to the artist (as track artist or album
    /// artist), ordered by album, disc, then track number.
    /// </summary>
    public async Task<List<DbMediaFile>> GetMediaFilesByArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {MediaFileColumns} FROM MediaFiles " +
                    "WHERE Artist IS $artist OR AlbumArtist IS $artist " +
                    "ORDER BY Album, DiscNumber, TrackNumber;");
        cmd.Parameters.AddWithValue("$artist", string.IsNullOrEmpty(artistName) ? DBNull.Value : artistName);
        return await ReadMediaFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Searches media files whose title, artist, album artist, or album contains
    /// <paramref name="likePattern"/> (a SQL <c>LIKE</c> pattern, e.g. <c>%foo%</c>).
    /// </summary>
    public async Task<List<DbMediaFile>> SearchMediaFilesAsync(string likePattern, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {MediaFileColumns} FROM MediaFiles " +
                    "WHERE Title LIKE $kw OR Artist LIKE $kw OR AlbumArtist LIKE $kw OR Album LIKE $kw;");
        cmd.Parameters.AddWithValue("$kw", likePattern);
        return await ReadMediaFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    #endregion

    #region Album / artist projections

    /// <summary>
    /// Lists albums using the configured grouping rule. An artist filter includes
    /// albums credited to that artist through either album or track tags.
    /// </summary>
    public async Task<List<MediaLibraryAlbum>> GetAlbumsAsync(string albumArtist = null, CancellationToken cancellationToken = default)
        => await QueryAlbumsAsync(albumArtist, albumTitle: null, searchLike: null, cancellationToken: cancellationToken);

    /// <summary>Loads one deterministic page of grouped albums.</summary>
    public async Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null : DecodeAlbumCursor(request);
        var order = AlbumPageOrder(request, cursor);
        var sql = new StringBuilder("WITH AlbumGroups AS (")
            .Append(AlbumGroupingSql)
            .Append(AlbumGroupBySql).Append(") ")
            .Append("SELECT Title, ArtistForQuery, FileCount, Artist, Date, Genre, ")
            .Append("FirstFileInAlbum, DatabaseItemAddedDate, AlbumGroupingKey FROM AlbumGroups");
        AppendKeysetPage(sql, order, cursor is not null);
        await
                using var cmd = CreateCommand(sql.ToString());
        cmd.Parameters.AddWithValue("$various", Resources.VariousArtist);
        BindCursorParameters(cmd, order, cursor is not null);
        cmd.Parameters.AddWithValue("$limit", request.PageSize + 1);
        var albums = new List<MediaLibraryAlbum>(request.PageSize + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            albums.Add(ReadAlbum(reader));
        }

        string nextCursor = null;
        if (albums.Count > request.PageSize)
        {
            albums.RemoveAt(request.PageSize);
            var last = albums[^1];
            nextCursor = EncodeCursor(
                new AlbumPageCursor(
                    request.Sort,
                    request.Ascending,
                    last.Title ?? string.Empty,
                    last.Artist ?? string.Empty,
                    last.ArtistForQuery ?? string.Empty,
                    ParseLeadingInt(last.Date),
                    last.AlbumGroupingKey ?? string.Empty,
                    AlbumGrouping),
                MediaFilePagingJsonContext.Default.AlbumPageCursor);
        }

        int? totalCount = null;
        if (string.IsNullOrEmpty(request.Cursor))
        {
            await using var count = CreateCommand(
                            "SELECT COUNT(*) FROM (SELECT 1 FROM MediaFiles WHERE Album IS NOT NULL " +
                            AlbumGroupBySql + ");");
            totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
        }
        return new PagedResult<MediaLibraryAlbum>(albums.ToArray(), nextCursor, totalCount);
    }

    /// <summary>
    /// Returns the album matching the album artist and title, or
    /// <see langword="null"/> when none is found.
    /// </summary>
    public async Task<MediaLibraryAlbum> GetAlbumAsync(string albumArtist, string albumTitle, string albumGroupingKey = null, CancellationToken cancellationToken = default)
    {
        var albums = await QueryAlbumsAsync(albumArtist, albumTitle, searchLike: null, albumGroupingKey, exactAlbum: true, cancellationToken: cancellationToken);
        return albums.Count > 0 ? albums[0] : null;
    }

    /// <summary>
    /// Returns the albums whose title, display artist, or genre matches the SQL
    /// <c>LIKE</c> pattern (e.g. <c>%foo%</c>). Filtering is performed in SQL over
    /// the grouped projection.
    /// </summary>
    public async Task<List<MediaLibraryAlbum>> SearchAlbumsAsync(string likePattern, CancellationToken cancellationToken = default)
        => await QueryAlbumsAsync(albumArtist: null, albumTitle: null, searchLike: likePattern, cancellationToken: cancellationToken);

    /// <summary>
    /// Returns the album key (album title and album artist) stored for the track at
    /// <paramref name="path"/>, or <see langword="null"/> when the path is unknown.
    /// </summary>
    public async Task<(string Album, string AlbumArtist)?> GetAlbumKeyByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("SELECT Album, AlbumArtist FROM MediaFiles WHERE Path = $path LIMIT 1;");
        cmd.Parameters.AddWithValue("$path", string.IsNullOrEmpty(path) ? DBNull.Value : path);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        if (!await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            return null;
        }

        return (GetNullableString(reader, 0), GetNullableString(reader, 1));
    }

    private async Task<List<MediaLibraryAlbum>> QueryAlbumsAsync(
        string albumArtist,
        string albumTitle,
        string searchLike,
        string albumGroupingKey = null,
        bool exactAlbum = false, CancellationToken cancellationToken = default)
    {
        // The display artist is the album artist, the sole track artist, or the "various artists"
        // placeholder. A null track artist counts as a distinct value.
        var grouping = new StringBuilder(AlbumGroupingSql);

        if (exactAlbum)
        {
            grouping.Append(" AND AlbumArtist IS $artist AND Album IS $title");
        }

        if (albumGroupingKey is not null)
        {
            grouping.Append($" AND ({AlbumGroupExpression}) IS $albumGroup");
        }

        grouping.Append(AlbumGroupBySql);
        if (!exactAlbum && !string.IsNullOrEmpty(albumArtist))
        {
            grouping.Append(" HAVING AlbumArtist IS $artist OR " +
                "SUM(CASE WHEN Artist IS $artist THEN 1 ELSE 0 END) > 0");
        }

        string sql = !string.IsNullOrEmpty(searchLike)
                        ? "SELECT Title, ArtistForQuery, FileCount, Artist, Date, Genre, FirstFileInAlbum, DatabaseItemAddedDate, AlbumGroupingKey FROM (" +
              grouping + ") WHERE Title LIKE $kw OR Artist LIKE $kw OR Genre LIKE $kw;"
            : grouping + ";";
        await
                using var cmd = CreateCommand(sql);
        cmd.Parameters.AddWithValue("$various", Resources.VariousArtist);
        if (exactAlbum || !string.IsNullOrEmpty(albumArtist))
        {
            AddNullableString(cmd, "$artist", albumArtist);
        }
        if (exactAlbum)
        {
            AddNullableString(cmd, "$title", albumTitle);
        }
        if (albumGroupingKey is not null)
        {
            cmd.Parameters.AddWithValue("$albumGroup", albumGroupingKey);
        }
        if (!string.IsNullOrEmpty(searchLike))
        {
            cmd.Parameters.AddWithValue("$kw", searchLike);
        }

        var albums = new List<MediaLibraryAlbum>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            albums.Add(ReadAlbum(reader));
        }

        return albums;
    }

    /// <summary>Lists artists grouped by the track artist tag.</summary>
    public async Task<List<MediaLibraryArtist>> GetArtistsAsync(CancellationToken cancellationToken = default) => await QueryArtistsAsync(name: null, searchLike: null, cancellationToken: cancellationToken);

    /// <summary>Loads one artist-name page together with bounded collage artwork candidates.</summary>
    public async Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null : DecodeArtistCursor(request);
        var order = new List<MediaFileOrderKey>
        {
            new("Name COLLATE LIGHTPLAYER_NOCASE", request.Ascending, cursor?.Name ?? string.Empty),
            new("Name COLLATE BINARY", true, cursor?.Name ?? string.Empty),
        };
        var sql = new StringBuilder(
            "WITH ArtistGroups AS (" + ArtistGroupingSql + ") " +
            "SELECT Name, FileCount, AlbumCount, LooseTrackCount, DatabaseItemAddedDate FROM ArtistGroups");
        AppendKeysetPage(sql, order, cursor is not null);

        var artists = new List<MediaLibraryArtist>(request.PageSize + 1);
        await using (var cmd = CreateCommand(sql.ToString()))
        {
            BindCursorParameters(cmd, order, cursor is not null);
            cmd.Parameters.AddWithValue("$limit", request.PageSize + 1);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                artists.Add(ReadArtist(reader));
            }
        }

        string nextCursor = null;
        if (artists.Count > request.PageSize)
        {
            artists.RemoveAt(request.PageSize);
            nextCursor = EncodeCursor(
                new ArtistPageCursor(request.Ascending, artists[^1].Name),
                MediaFilePagingJsonContext.Default.ArtistPageCursor);
        }

        var values = new MediaLibraryArtistBrowseItem[artists.Count];
        for (var index = 0; index < artists.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[index] = new MediaLibraryArtistBrowseItem
            {
                Artist = artists[index],
                ArtworkPaths = await GetArtistArtworkPathsAsync(artists[index].Name, cancellationToken: cancellationToken),
            };
        }

        int? totalCount = null;
        if (string.IsNullOrEmpty(request.Cursor))
        {
            await using var count = CreateCommand(
                            "SELECT COUNT(*) FROM (SELECT 1 FROM MediaFiles WHERE Artist IS NOT NULL GROUP BY Artist);");
            totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
        }
        return new PagedResult<MediaLibraryArtistBrowseItem>(values, nextCursor, totalCount);
    }

    /// <summary>
    /// Returns the artist with the given name, or <see langword="null"/> when no
    /// track is credited to it.
    /// </summary>
    public async Task<MediaLibraryArtist> GetArtistAsync(string name, CancellationToken cancellationToken = default)
    {
        var artists = await QueryArtistsAsync(name, searchLike: null, cancellationToken: cancellationToken);
        return artists.Count > 0 ? artists[0] : null;
    }

    /// <summary>
    /// Returns the artists whose name matches the SQL <c>LIKE</c> pattern (e.g.
    /// <c>%foo%</c>).
    /// </summary>
    public async Task<List<MediaLibraryArtist>> SearchArtistsAsync(string likePattern, CancellationToken cancellationToken = default)
        => await QueryArtistsAsync(name: null, searchLike: likePattern, cancellationToken: cancellationToken);

    private async Task<List<MediaLibraryArtist>> QueryArtistsAsync(string name, string searchLike, CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder()
            .Append("SELECT Name, FileCount, AlbumCount, LooseTrackCount, DatabaseItemAddedDate FROM (")
            .Append(ArtistGroupingSql).Append(") WHERE 1 = 1");
        if (!string.IsNullOrEmpty(name))
        {
            sql.Append(" AND Name IS $name");
        }
        if (!string.IsNullOrEmpty(searchLike))
        {
            sql.Append(" AND Name LIKE $kw");
        }
        sql.Append(';');
        await
                using var cmd = CreateCommand(sql.ToString());
        if (!string.IsNullOrEmpty(name))
        {
            cmd.Parameters.AddWithValue("$name", name);
        }
        if (!string.IsNullOrEmpty(searchLike))
        {
            cmd.Parameters.AddWithValue("$kw", searchLike);
        }

        var artists = new List<MediaLibraryArtist>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            artists.Add(ReadArtist(reader));
        }

        return artists;
    }

    /// <summary>Returns whether any track is credited to the given (non-null) artist.</summary>
    public async Task<bool> ArtistExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("SELECT EXISTS(SELECT 1 FROM MediaFiles WHERE Artist = $name);");
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    #endregion

    #region Mutations

    /// <summary>
    /// Inserts a media file and assigns its generated <see cref="DbMediaFile.Id"/>.
    /// Participates in the active transaction when one is open.
    /// </summary>
    public async Task InsertMediaFileAsync(DbMediaFile file, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "INSERT INTO MediaFiles (" +
                    "Duration, TotalDiscs, DiscNumber, TotalTracks, Description, Copyright, Comments, Grouping, " +
                    "Genre, TrackNumber, AlbumArtist, Performer, Composer, Date, Album, Artist, Title, Path, " +
                    "DatabaseItemAddedDate, FileLastModifiedDate, StartTime) VALUES (" +
                    "$duration, $totalDiscs, $discNumber, $totalTracks, $description, $copyright, $comments, $grouping, " +
                    "$genre, $trackNumber, $albumArtist, $performer, $composer, $date, $album, $artist, $title, $path, " +
                    "$addedDate, $modifiedDate, $startTime); SELECT last_insert_rowid();");
        cmd.Parameters.AddWithValue("$duration", FormatTimeSpan(file.Duration));
        AddNullableString(cmd, "$totalDiscs", file.TotalDiscs);
        AddNullableString(cmd, "$discNumber", file.DiscNumber);
        AddNullableString(cmd, "$totalTracks", file.TotalTracks);
        AddNullableString(cmd, "$description", file.Description);
        AddNullableString(cmd, "$copyright", file.Copyright);
        AddNullableString(cmd, "$comments", file.Comments);
        AddNullableString(cmd, "$grouping", file.Grouping);
        AddNullableString(cmd, "$genre", file.Genre);
        AddNullableString(cmd, "$trackNumber", file.TrackNumber);
        AddNullableString(cmd, "$albumArtist", file.AlbumArtist);
        AddNullableString(cmd, "$performer", file.Performer);
        AddNullableString(cmd, "$composer", file.Composer);
        AddNullableString(cmd, "$date", file.Date);
        AddNullableString(cmd, "$album", file.Album);
        AddNullableString(cmd, "$artist", file.Artist);
        AddNullableString(cmd, "$title", file.Title);
        AddNullableString(cmd, "$path", file.Path);
        cmd.Parameters.AddWithValue("$addedDate", FormatDateTimeOffset(file.DatabaseItemAddedDate));
        cmd.Parameters.AddWithValue("$modifiedDate", FormatDateTimeOffset(file.FileLastModifiedDate));
        cmd.Parameters.AddWithValue("$startTime", file.StartTime.HasValue ? (object)file.StartTime.Value : DBNull.Value);

        file.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes the media file with the given id.</summary>
    public async Task DeleteMediaFileAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("DELETE FROM MediaFiles WHERE Id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }

    #endregion

    #region Video file queries

    /// <summary>Returns the total number of indexed videos.</summary>
    public async Task<int> CountVideoFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("SELECT COUNT(*) FROM VideoFiles;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Loads every indexed video, ordered by folder then file name.</summary>
    public async Task<List<DbVideoFile>> GetAllVideoFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {VideoFileColumns} FROM VideoFiles ORDER BY FolderPath, FileName;");
        return await ReadVideoFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>Loads one keyset page of videos ordered by display name.</summary>
    public async Task<PagedResult<DbVideoFile>> GetVideoFilesPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null : DecodeVideoCursor(request);
        const string name = "lightplayer_video_title(Title, FileName)";
        var sql = new StringBuilder($"SELECT {VideoFileColumns} FROM VideoFiles");
        if (cursor is not null)
        {
            sql.Append(" WHERE ")
                .Append(name).Append(" COLLATE LIGHTPLAYER_NOCASE > $name ")
                .Append("OR (").Append(name).Append(" COLLATE LIGHTPLAYER_NOCASE = $name AND ")
                .Append(name).Append(" COLLATE BINARY > $exactName) ")
                .Append("OR (").Append(name).Append(" COLLATE LIGHTPLAYER_NOCASE = $name AND ")
                .Append(name).Append(" COLLATE BINARY = $exactName AND Id > $id)");
        }

        sql.Append(" ORDER BY ")
            .Append(name).Append(" COLLATE LIGHTPLAYER_NOCASE, ")
            .Append(name).Append(" COLLATE BINARY, Id LIMIT $limit;");
        await
                using var cmd = CreateCommand(sql.ToString());
        cmd.Parameters.AddWithValue("$name", cursor?.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$exactName", cursor?.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$id", cursor?.Id ?? 0);
        cmd.Parameters.AddWithValue("$limit", request.PageSize + 1);
        var videos = new List<DbVideoFile>(request.PageSize + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            videos.Add(ReadVideoFile(reader));
        }

        string nextCursor = null;
        if (videos.Count > request.PageSize)
        {
            videos.RemoveAt(request.PageSize);
            var last = videos[^1];
            nextCursor = EncodeCursor(
                new VideoPageCursor(VideoTitle(last.Title, last.FileName), last.Id),
                MediaFilePagingJsonContext.Default.VideoPageCursor);
        }

        var totalCount = string.IsNullOrEmpty(request.Cursor) ? await CountVideoFilesAsync(cancellationToken: cancellationToken) : (int?)null;
        return new PagedResult<DbVideoFile>(videos.ToArray(), nextCursor, totalCount);
    }

    /// <summary>Loads the videos directly inside a folder.</summary>
    public async Task<List<DbVideoFile>> GetVideoFilesInFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {VideoFileColumns} FROM VideoFiles WHERE FolderPath IS $folder " +
                    "ORDER BY lightplayer_video_title(Title, FileName) COLLATE LIGHTPLAYER_NOCASE, Id;");
        cmd.Parameters.AddWithValue("$folder", string.IsNullOrEmpty(folderPath) ? DBNull.Value : folderPath);
        return await ReadVideoFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>Loads at most <paramref name="limit"/> videos in the requested pick order.</summary>
    public async Task<List<DbVideoFile>> GetVideoFilePicksAsync(VideoPickOrder order, int limit, CancellationToken cancellationToken = default)
    {
        var ordering = order == VideoPickOrder.Random
            ? "RANDOM()"
            : "lightplayer_utc_ticks(DatabaseItemAddedDate) DESC, Id DESC";
        await using var cmd = CreateCommand(
                    $"SELECT {VideoFileColumns} FROM VideoFiles ORDER BY {ordering} LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", limit);
        return await ReadVideoFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Searches videos whose title tag or file name contains <paramref name="likePattern"/> (a SQL
    /// <c>LIKE</c> pattern, e.g. <c>%foo%</c>).
    /// </summary>
    public async Task<List<DbVideoFile>> SearchVideoFilesAsync(string likePattern, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {VideoFileColumns} FROM VideoFiles WHERE Title LIKE $kw OR FileName LIKE $kw " +
                    "ORDER BY lightplayer_video_title(Title, FileName) COLLATE LIGHTPLAYER_NOCASE, Id;");
        cmd.Parameters.AddWithValue("$kw", likePattern);
        return await ReadVideoFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    /// <summary>Finds a video by its identifier, or <see langword="null"/>.</summary>
    public async Task<DbVideoFile> GetVideoFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    $"SELECT {VideoFileColumns} FROM VideoFiles WHERE Path = $path LIMIT 1;");
        cmd.Parameters.AddWithValue("$path", string.IsNullOrEmpty(path) ? DBNull.Value : path);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        return await reader.ReadAsync(cancellationToken: cancellationToken) ? ReadVideoFile(reader) : null;
    }

    /// <summary>
    /// Returns one row per folder that directly contains videos. The folder tree shown in the UI is
    /// derived from this, which keeps path-separator handling out of SQL.
    /// </summary>
    public async Task<List<VideoFolderCount>> GetVideoFolderCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "SELECT LibraryRoot, FolderPath, COUNT(*) FROM VideoFiles " +
                    "GROUP BY LibraryRoot, FolderPath ORDER BY FolderPath;");
        var folders = new List<VideoFolderCount>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            folders.Add(new VideoFolderCount
            {
                LibraryRoot = GetNullableString(reader, 0),
                FolderPath = GetNullableString(reader, 1),
                VideoCount = reader.GetInt32(2),
            });
        }

        return folders;
    }

    #endregion

    #region Video file mutations

    /// <summary>
    /// Inserts a video row, or replaces the existing row for the same path, and assigns
    /// <see cref="DbVideoFile.Id"/>. Participates in the active transaction when one is open.
    /// </summary>
    public async Task UpsertVideoFileAsync(DbVideoFile file, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "INSERT INTO VideoFiles (" +
                    "Path, FolderPath, LibraryRoot, FileName, Title, Duration, Width, Height, FrameRate, " +
                    "VideoCodec, FileSize, BitRate, VideoStreamIndex, MediaKind, DiscTitle, AudioStreams, " +
                    "ExternalAudioPaths, ExternalSubtitlePaths, DatabaseItemAddedDate, FileLastModifiedDate) VALUES (" +
                    "$path, $folder, $root, $fileName, $title, $duration, $width, $height, $frameRate, " +
                    "$videoCodec, $fileSize, $bitRate, $videoStreamIndex, $mediaKind, $discTitle, $audioStreams, " +
                    "$audioPaths, $subtitlePaths, $addedDate, $modifiedDate) " +
                    "ON CONFLICT(Path) DO UPDATE SET " +
                    "FolderPath = excluded.FolderPath, LibraryRoot = excluded.LibraryRoot, " +
                    "FileName = excluded.FileName, Title = excluded.Title, Duration = excluded.Duration, " +
                    "Width = excluded.Width, Height = excluded.Height, FrameRate = excluded.FrameRate, " +
                    "VideoCodec = excluded.VideoCodec, FileSize = excluded.FileSize, " +
                    "BitRate = excluded.BitRate, VideoStreamIndex = excluded.VideoStreamIndex, " +
                    "MediaKind = excluded.MediaKind, DiscTitle = excluded.DiscTitle, AudioStreams = excluded.AudioStreams, " +
                    "ExternalAudioPaths = excluded.ExternalAudioPaths, ExternalSubtitlePaths = excluded.ExternalSubtitlePaths, " +
                    "FileLastModifiedDate = excluded.FileLastModifiedDate; " +
                    "SELECT Id FROM VideoFiles WHERE Path = $path;");
        AddNullableString(cmd, "$path", file.Path);
        AddNullableString(cmd, "$folder", file.FolderPath);
        AddNullableString(cmd, "$root", file.LibraryRoot);
        AddNullableString(cmd, "$fileName", file.FileName);
        AddNullableString(cmd, "$title", file.Title);
        cmd.Parameters.AddWithValue("$duration", FormatTimeSpan(file.Duration));
        cmd.Parameters.AddWithValue("$width", file.Width);
        cmd.Parameters.AddWithValue("$height", file.Height);
        cmd.Parameters.AddWithValue("$frameRate", file.FrameRate);
        AddNullableString(cmd, "$videoCodec", file.VideoCodec);
        cmd.Parameters.AddWithValue("$fileSize", file.FileSize);
        cmd.Parameters.AddWithValue("$bitRate", file.BitRate);
        cmd.Parameters.AddWithValue("$videoStreamIndex", file.VideoStreamIndex);
        cmd.Parameters.AddWithValue("$mediaKind", (int)file.MediaKind);
        cmd.Parameters.AddWithValue("$discTitle", file.DiscTitle);
        AddNullableString(cmd, "$audioStreams", SerializeAudioStreams(file.AudioStreams));
        AddNullableString(cmd, "$audioPaths", file.ExternalAudioPaths);
        AddNullableString(cmd, "$subtitlePaths", file.ExternalSubtitlePaths);
        cmd.Parameters.AddWithValue("$addedDate", FormatDateTimeOffset(file.DatabaseItemAddedDate));
        cmd.Parameters.AddWithValue("$modifiedDate", FormatDateTimeOffset(file.FileLastModifiedDate));

        file.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes the video with the given id.</summary>
    public async Task DeleteVideoFileAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("DELETE FROM VideoFiles WHERE Id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }

    #endregion

    #region Image files

    public async Task<int> CountImageFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("SELECT COUNT(*) FROM ImageFiles;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<List<DbImageFile>> GetAllImageFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {ImageFileColumns} FROM ImageFiles ORDER BY FolderPath, FileName;");
        return await ReadImageFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    public async Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var cursor = string.IsNullOrEmpty(request.Cursor) ? null :
            DecodeCursor(request.Cursor, MediaFilePagingJsonContext.Default.MediaPageCursor);
        var sql = new StringBuilder(
            $"SELECT * FROM (SELECT {VideoFileColumns}, NULL AS DateTaken, NULL AS FileCreatedDate, " +
            "0 AS IsImage, lightplayer_video_title(Title, FileName) AS BrowseName, " +
            "NULL AS Country, NULL AS State, NULL AS City FROM VideoFiles UNION ALL " +
            "SELECT Id, Path, FolderPath, LibraryRoot, FileName, NULL, '00:00:00', Width, Height, 0, " +
            "NULL, FileSize, 0, -1, 0, 0, NULL, NULL, NULL, DatabaseItemAddedDate, FileLastModifiedDate, " +
            "DateTaken, FileCreatedDate, 1, FileName, Country, State, City FROM ImageFiles)");
        if (cursor is not null)
        {
            sql.Append(" WHERE BrowseName COLLATE LIGHTPLAYER_NOCASE > $name ")
                .Append("OR (BrowseName COLLATE LIGHTPLAYER_NOCASE = $name AND BrowseName COLLATE BINARY > $name) ")
                .Append("OR (BrowseName COLLATE LIGHTPLAYER_NOCASE = $name AND BrowseName COLLATE BINARY = $name ")
                .Append("AND (IsImage > $image OR (IsImage = $image AND Id > $id)))");
        }

        sql.Append(" ORDER BY BrowseName COLLATE LIGHTPLAYER_NOCASE, BrowseName COLLATE BINARY, IsImage, Id LIMIT $limit;");
        await using var cmd = CreateCommand(sql.ToString());
        cmd.Parameters.AddWithValue("$name", cursor?.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$id", cursor?.Id ?? 0);
        cmd.Parameters.AddWithValue("$image", cursor?.IsImage == true ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", request.PageSize + 1);
        var items = new List<IndexedMediaFile>(request.PageSize + 1);
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(reader.GetInt32(23) == 0
                    ? new IndexedMediaFile(Video: ReadVideoFile(reader))
                    : new IndexedMediaFile(Image: new DbImageFile
                    {
                        Id = reader.GetInt32(0),
                        Path = reader.GetString(1),
                        FolderPath = GetNullableString(reader, 2),
                        LibraryRoot = GetNullableString(reader, 3),
                        FileName = reader.GetString(4),
                        FileSize = reader.GetInt64(11),
                        Width = reader.GetInt32(7),
                        Height = reader.GetInt32(8),
                        DateTaken = reader.IsDBNull(21) ? null : ParseDateTimeOffset(reader.GetString(21)),
                        FileCreatedDate = reader.IsDBNull(22) ? null : ParseDateTimeOffset(reader.GetString(22)),
                        FileLastModifiedDate = ParseDateTimeOffset(reader.GetString(20)),
                        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(19)),
                        Country = GetNullableString(reader, 25),
                        State = GetNullableString(reader, 26),
                        City = GetNullableString(reader, 27),
                    }));
            }
        }

        string nextCursor = null;
        if (items.Count > request.PageSize)
        {
            items.RemoveAt(request.PageSize);
            var last = items[^1];
            nextCursor = EncodeCursor(new MediaPageCursor(last.DisplayName,
                last.Image?.Id ?? last.Video.Id, last.Image is not null),
                MediaFilePagingJsonContext.Default.MediaPageCursor);
        }

        return new PagedResult<IndexedMediaFile>(items.ToArray(), nextCursor,
            string.IsNullOrEmpty(request.Cursor) ? await CountImageFilesAsync(cancellationToken: cancellationToken) + await CountVideoFilesAsync(cancellationToken: cancellationToken) : null);
    }

    public async Task<List<DbImageFile>> GetImageFilesInFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {ImageFileColumns} FROM ImageFiles WHERE FolderPath IS $folder " +
                    "ORDER BY FileName COLLATE LIGHTPLAYER_NOCASE, Id;");
        cmd.Parameters.AddWithValue("$folder", string.IsNullOrEmpty(folderPath) ? DBNull.Value : folderPath);
        return await ReadImageFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    public async Task<List<DbImageFile>> SearchImageFilesAsync(string likePattern, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand($"SELECT {ImageFileColumns} FROM ImageFiles WHERE FileName LIKE $kw ESCAPE '\\' " +
                    "ORDER BY FileName COLLATE LIGHTPLAYER_NOCASE, Id;");
        cmd.Parameters.AddWithValue("$kw", likePattern);
        return await ReadImageFilesAsync(cmd, cancellationToken: cancellationToken);
    }

    public async Task UpsertImageFileAsync(DbImageFile file, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "INSERT INTO ImageFiles (Path, FolderPath, LibraryRoot, FileName, FileSize, Width, Height, " +
                    "DateTaken, FileCreatedDate, FileLastModifiedDate, DatabaseItemAddedDate, Latitude, Longitude, " +
                    "Country, State, City, LocationLanguage) VALUES (" +
                    "$path, $folder, $root, $name, $size, $width, $height, $taken, $created, $modified, $added, " +
                    "$latitude, $longitude, $country, $state, $city, $language) " +
                    "ON CONFLICT(Path) DO UPDATE SET FolderPath = excluded.FolderPath, LibraryRoot = excluded.LibraryRoot, " +
                    "FileName = excluded.FileName, FileSize = excluded.FileSize, Width = excluded.Width, Height = excluded.Height, " +
                    "DateTaken = excluded.DateTaken, FileCreatedDate = excluded.FileCreatedDate, " +
                    "FileLastModifiedDate = excluded.FileLastModifiedDate, Latitude = excluded.Latitude, " +
                    "Longitude = excluded.Longitude, Country = excluded.Country, State = excluded.State, " +
                    "City = excluded.City, LocationLanguage = excluded.LocationLanguage; " +
                    "SELECT Id FROM ImageFiles WHERE Path = $path;");
        cmd.Parameters.AddWithValue("$path", file.Path);
        AddNullableString(cmd, "$folder", file.FolderPath);
        AddNullableString(cmd, "$root", file.LibraryRoot);
        cmd.Parameters.AddWithValue("$name", file.FileName);
        cmd.Parameters.AddWithValue("$size", file.FileSize);
        cmd.Parameters.AddWithValue("$width", file.Width);
        cmd.Parameters.AddWithValue("$height", file.Height);
        AddNullableString(cmd, "$taken", file.DateTaken is { } taken ? FormatDateTimeOffset(taken) : null);
        AddNullableString(cmd, "$created", file.FileCreatedDate is { } created ? FormatDateTimeOffset(created) : null);
        cmd.Parameters.AddWithValue("$modified", FormatDateTimeOffset(file.FileLastModifiedDate));
        cmd.Parameters.AddWithValue("$added", FormatDateTimeOffset(file.DatabaseItemAddedDate));
        cmd.Parameters.AddWithValue("$latitude", file.Latitude is { } latitude ? latitude : DBNull.Value);
        cmd.Parameters.AddWithValue("$longitude", file.Longitude is { } longitude ? longitude : DBNull.Value);
        AddNullableString(cmd, "$country", file.Country);
        AddNullableString(cmd, "$state", file.State);
        AddNullableString(cmd, "$city", file.City);
        AddNullableString(cmd, "$language", file.LocationLanguage);
        file.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task DeleteImageFileAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("DELETE FROM ImageFiles WHERE Id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }

    #endregion

    #region Transactions

    /// <summary>Begins a transaction used by subsequent mutations in this scope.</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        _transaction = await Connection.BeginTransactionAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Commits the active transaction, if any.</summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(cancellationToken: cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    #endregion

    #region Helpers

    public async Task RollbackTransactionAsync()
    {
        if (_transaction is null)
            return;
        try
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _transaction = null;
        }
    }

    private AsyncSqliteCommand CreateCommand(string sql)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        if (_transaction is not null)
        {
            cmd.Transaction = _transaction;
        }

        return cmd;
    }

    private async Task<List<DbMediaFile>> ReadMediaFilesAsync(AsyncSqliteCommand cmd, CancellationToken cancellationToken = default)
    {
        var files = new List<DbMediaFile>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            files.Add(ReadMediaFile(reader));
        }

        return files;
    }

    private DbMediaFile ReadMediaFile(IAsyncSqliteReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Duration = ParseTimeSpan(reader.GetString(1)),
        TotalDiscs = GetNullableString(reader, 2),
        DiscNumber = GetNullableString(reader, 3),
        TotalTracks = GetNullableString(reader, 4),
        Description = GetNullableString(reader, 5),
        Copyright = GetNullableString(reader, 6),
        Comments = GetNullableString(reader, 7),
        Grouping = GetNullableString(reader, 8),
        Genre = GetNullableString(reader, 9),
        TrackNumber = GetNullableString(reader, 10),
        AlbumArtist = GetNullableString(reader, 11),
        AlbumGroupingKey = GetNullableString(reader, 22),
        Performer = GetNullableString(reader, 12),
        Composer = GetNullableString(reader, 13),
        Date = GetNullableString(reader, 14),
        Album = GetNullableString(reader, 15),
        Artist = GetNullableString(reader, 16),
        Title = GetNullableString(reader, 17),
        Path = GetNullableString(reader, 18),
        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(19)),
        FileLastModifiedDate = ParseDateTimeOffset(reader.GetString(20)),
        StartTime = reader.IsDBNull(21) ? null : reader.GetInt32(21),
    };

    private static string GetNullableString(IAsyncSqliteReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<List<DbVideoFile>> ReadVideoFilesAsync(AsyncSqliteCommand cmd, CancellationToken cancellationToken = default)
    {
        var files = new List<DbVideoFile>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            files.Add(ReadVideoFile(reader));
        }

        return files;
    }

    private static DbVideoFile ReadVideoFile(IAsyncSqliteReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Path = GetNullableString(reader, 1),
        FolderPath = GetNullableString(reader, 2),
        LibraryRoot = GetNullableString(reader, 3),
        FileName = GetNullableString(reader, 4),
        Title = GetNullableString(reader, 5),
        Duration = ParseTimeSpan(reader.GetString(6)),
        Width = reader.GetInt32(7),
        Height = reader.GetInt32(8),
        FrameRate = reader.GetDouble(9),
        VideoCodec = GetNullableString(reader, 10),
        FileSize = reader.GetInt64(11),
        BitRate = reader.GetInt64(12),
        VideoStreamIndex = reader.GetInt32(13),
        MediaKind = (IO.VideoMediaKind)reader.GetInt32(14),
        DiscTitle = reader.GetInt32(15),
        AudioStreams = DeserializeAudioStreams(GetNullableString(reader, 16)),
        ExternalAudioPaths = GetNullableString(reader, 17),
        ExternalSubtitlePaths = GetNullableString(reader, 18),
        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(19)),
        FileLastModifiedDate = ParseDateTimeOffset(reader.GetString(20)),
    };

    private static async Task<List<DbImageFile>> ReadImageFilesAsync(AsyncSqliteCommand cmd, CancellationToken cancellationToken = default)
    {
        var files = new List<DbImageFile>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            files.Add(ReadImageFile(reader));
        }

        return files;
    }

    private static DbImageFile ReadImageFile(IAsyncSqliteReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Path = reader.GetString(1),
        FolderPath = GetNullableString(reader, 2),
        LibraryRoot = GetNullableString(reader, 3),
        FileName = reader.GetString(4),
        FileSize = reader.GetInt64(5),
        Width = reader.GetInt32(6),
        Height = reader.GetInt32(7),
        DateTaken = reader.IsDBNull(8) ? null : ParseDateTimeOffset(reader.GetString(8)),
        FileCreatedDate = reader.IsDBNull(9) ? null : ParseDateTimeOffset(reader.GetString(9)),
        FileLastModifiedDate = ParseDateTimeOffset(reader.GetString(10)),
        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(11)),
        Latitude = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        Longitude = reader.IsDBNull(13) ? null : reader.GetDouble(13),
        Country = GetNullableString(reader, 14),
        State = GetNullableString(reader, 15),
        City = GetNullableString(reader, 16),
        LocationLanguage = GetNullableString(reader, 17),
    };

    private static string SerializeAudioStreams(DbVideoAudioStream[] streams) =>
        streams is { Length: > 0 }
            ? JsonSerializer.Serialize(streams, VideoDatabaseJsonContext.Default.DbVideoAudioStreamArray)
            : null;

    private static DbVideoAudioStream[] DeserializeAudioStreams(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize(value, VideoDatabaseJsonContext.Default.DbVideoAudioStreamArray) ?? [];

    private static void AddNullableString(AsyncSqliteCommand cmd, string name, string value)
        => cmd.Parameters.AddWithValue(name, string.IsNullOrEmpty(value) ? DBNull.Value : value);

    private static TimeSpan ParseTimeSpan(string value)
        => TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    private static string FormatTimeSpan(TimeSpan value)
        => value.ToString("c", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static string FormatDateTimeOffset(DateTimeOffset value)
        => value.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture);

    private string AlbumGroupExpression => AlbumGrouping switch
    {
        AlbumGroupingMode.SameFolder =>
            "CASE WHEN NULLIF(AlbumArtist, '') IS NULL AND Album IN (" +
            "SELECT Album FROM MediaFiles WHERE NULLIF(AlbumArtist, '') IS NULL " +
            "GROUP BY Album HAVING COUNT(DISTINCT COALESCE(Artist, '')) > 1) " +
            "THEN lightplayer_parent_path(Path) END",
        AlbumGroupingMode.SeparateArtists =>
            "CASE WHEN NULLIF(AlbumArtist, '') IS NULL THEN COALESCE(Artist, '') END",
        _ => "NULL",
    };

    private string AlbumGroupBySql => $" GROUP BY Album, AlbumArtist, {AlbumGroupExpression}";

    private string ArtistGroupingSql =>
        "SELECT Artist AS Name, SUM(FileCount) AS FileCount, " +
        "SUM(CASE WHEN Album IS NOT NULL THEN 1 ELSE 0 END) AS AlbumCount, " +
        "SUM(CASE WHEN Album IS NULL THEN FileCount ELSE 0 END) AS LooseTrackCount, " +
        "MIN(DatabaseItemAddedDate) AS DatabaseItemAddedDate FROM (" +
        "SELECT Artist, Album, COUNT(*) AS FileCount, MIN(DatabaseItemAddedDate) AS DatabaseItemAddedDate " +
        "FROM MediaFiles WHERE Artist IS NOT NULL" + AlbumGroupBySql + ", Artist) GROUP BY Artist";

    private string AlbumGroupingSql =>
        "SELECT Album AS Title, AlbumArtist AS ArtistForQuery, COUNT(*) AS FileCount, " +
        "CASE WHEN AlbumArtist IS NOT NULL THEN AlbumArtist " +
        "WHEN (COUNT(DISTINCT Artist) + " +
        "(CASE WHEN SUM(CASE WHEN Artist IS NULL THEN 1 ELSE 0 END) > 0 THEN 1 ELSE 0 END)) = 1 " +
        "THEN MIN(Artist) ELSE $various END AS Artist, " +
        "MIN(Date) AS Date, MIN(Genre) AS Genre, MIN(Path) AS FirstFileInAlbum, " +
        "MIN(DatabaseItemAddedDate) AS DatabaseItemAddedDate, " +
        AlbumGroupExpression + " AS AlbumGroupingKey " +
        "FROM MediaFiles WHERE Album IS NOT NULL";

    private MediaLibraryAlbum ReadAlbum(IAsyncSqliteReader reader) => new()
    {
        Title = GetNullableString(reader, 0),
        ArtistForQuery = GetNullableString(reader, 1),
        AlbumGroupingKey = GetNullableString(reader, 8),
        FileCount = reader.GetInt32(2),
        Artist = GetNullableString(reader, 3),
        Date = GetNullableString(reader, 4),
        Genre = GetNullableString(reader, 5),
        FirstFileInAlbum = GetNullableString(reader, 6),
        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(7)),
    };

    private static MediaLibraryArtist ReadArtist(IAsyncSqliteReader reader) => new()
    {
        Name = reader.GetString(0),
        FileCount = reader.GetInt32(1),
        AlbumCount = reader.GetInt32(2) + (reader.GetInt32(3) > 0 ? 1 : 0),
        DatabaseItemAddedDate = ParseDateTimeOffset(reader.GetString(4)),
    };

    private async Task<string[]> GetArtistArtworkPathsAsync(string artist, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(
                    "SELECT MIN(Path) FROM MediaFiles WHERE Album IS NOT NULL AND " +
                    "(AlbumArtist = $artist COLLATE LIGHTPLAYER_NOCASE OR " +
                    "(AlbumArtist IS NULL AND Artist = $artist COLLATE LIGHTPLAYER_NOCASE)) " +
                    AlbumGroupBySql + " " +
                    "ORDER BY lightplayer_leading_int(MIN(Date)) DESC, " +
                    "Album COLLATE LIGHTPLAYER_NOCASE, AlbumArtist COLLATE LIGHTPLAYER_NOCASE LIMIT 12;");
        cmd.Parameters.AddWithValue("$artist", artist);
        var paths = new List<string>(12);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            var path = GetNullableString(reader, 0);
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths.ToArray();
    }

    private static List<MediaFileOrderKey> AlbumPageOrder(
        AlbumPageRequest request,
        AlbumPageCursor cursor)
    {
        const string title = "COALESCE(Title, '')";
        const string artist = "COALESCE(Artist, '')";
        const string queryArtist = "COALESCE(ArtistForQuery, '')";
        var order = new List<MediaFileOrderKey>(7);
        switch (request.Sort)
        {
            case AlbumSortField.Title:
                order.Add(new(title + " COLLATE LIGHTPLAYER_NOCASE", request.Ascending, cursor?.Title ?? string.Empty));
                order.Add(new(title + " COLLATE BINARY", true, cursor?.Title ?? string.Empty));
                break;
            case AlbumSortField.ArtistName:
                order.Add(new(artist + " COLLATE LIGHTPLAYER_NOCASE", request.Ascending, cursor?.Artist ?? string.Empty));
                order.Add(new(artist + " COLLATE BINARY", true, cursor?.Artist ?? string.Empty));
                order.Add(new(title + " COLLATE LIGHTPLAYER_NOCASE", true, cursor?.Title ?? string.Empty));
                order.Add(new(title + " COLLATE BINARY", true, cursor?.Title ?? string.Empty));
                break;
            case AlbumSortField.Year:
                order.Add(new("lightplayer_leading_int(Date)", request.Ascending, cursor?.Year ?? 0));
                order.Add(new(title + " COLLATE LIGHTPLAYER_NOCASE", true, cursor?.Title ?? string.Empty));
                order.Add(new(title + " COLLATE BINARY", true, cursor?.Title ?? string.Empty));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), "Unsupported album sort field.");
        }

        order.Add(new(queryArtist + " COLLATE LIGHTPLAYER_NOCASE", true, cursor?.ArtistForQuery ?? string.Empty));
        order.Add(new(queryArtist + " COLLATE BINARY", true, cursor?.ArtistForQuery ?? string.Empty));
        order.Add(new("COALESCE(AlbumGroupingKey, '') COLLATE BINARY", true, cursor?.AlbumGroupingKey ?? string.Empty));
        return order;
    }

    private static void AppendKeysetPage(
        StringBuilder sql,
        IReadOnlyList<MediaFileOrderKey> order,
        bool hasCursor)
    {
        if (hasCursor)
        {
            sql.Append(" WHERE ");
            for (var keyIndex = 0; keyIndex < order.Count; keyIndex++)
            {
                if (keyIndex > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append('(');
                for (var equalIndex = 0; equalIndex < keyIndex; equalIndex++)
                {
                    sql.Append(order[equalIndex].Expression)
                        .Append(" = $cursor").Append(equalIndex).Append(" AND ");
                }

                sql.Append(order[keyIndex].Expression)
                    .Append(order[keyIndex].Ascending ? " > $cursor" : " < $cursor")
                    .Append(keyIndex).Append(')');
            }
        }

        sql.Append(" ORDER BY ");
        for (var index = 0; index < order.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            sql.Append(order[index].Expression)
                .Append(order[index].Ascending ? " ASC" : " DESC");
        }
        sql.Append(" LIMIT $limit;");
    }

    private static void BindCursorParameters(
        AsyncSqliteCommand cmd,
        IReadOnlyList<MediaFileOrderKey> order,
        bool hasCursor)
    {
        if (!hasCursor)
        {
            return;
        }

        for (var index = 0; index < order.Count; index++)
        {
            cmd.Parameters.AddWithValue($"$cursor{index}", order[index].CursorValue);
        }
    }

    private AlbumPageCursor DecodeAlbumCursor(AlbumPageRequest request)
    {
        var cursor = DecodeCursor(request.Cursor, MediaFilePagingJsonContext.Default.AlbumPageCursor);
        if (cursor.Sort != request.Sort || cursor.Ascending != request.Ascending || cursor.GroupingMode != AlbumGrouping)
        {
            throw new ArgumentException("The page cursor does not match the requested album grouping or ordering.", nameof(request));
        }
        return cursor;
    }

    private static ArtistPageCursor DecodeArtistCursor(ArtistPageRequest request)
    {
        var cursor = DecodeCursor(request.Cursor, MediaFilePagingJsonContext.Default.ArtistPageCursor);
        if (cursor.Ascending != request.Ascending)
        {
            throw new ArgumentException("The page cursor does not match the requested artist ordering.", nameof(request));
        }
        return cursor;
    }

    private static List<MediaFileOrderKey> MediaFilePageOrder(
        MediaFilePageRequest request,
        MediaFilePageCursor cursor)
    {
        const string title = "lightplayer_track_title(Title, Path) COLLATE LIGHTPLAYER_NOCASE";
        var primaryText = cursor?.PrimaryText ?? string.Empty;
        var primaryNumber = cursor?.PrimaryNumber ?? 0L;
        var keys = new List<MediaFileOrderKey>(5);

        switch (request.Sort)
        {
            case MediaFileSortField.Title:
                keys.Add(new MediaFileOrderKey(title, request.Ascending, primaryText));
                break;
            case MediaFileSortField.AlbumName:
                keys.Add(new MediaFileOrderKey(
                    "COALESCE(Album, '') COLLATE LIGHTPLAYER_NOCASE",
                    request.Ascending,
                    primaryText));
                keys.Add(new MediaFileOrderKey(
                    "lightplayer_leading_int(DiscNumber)",
                    true,
                    cursor?.DiscNumber ?? 0));
                keys.Add(new MediaFileOrderKey(
                    "lightplayer_leading_int(TrackNumber)",
                    true,
                    cursor?.TrackNumber ?? 0));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            case MediaFileSortField.ArtistName:
                keys.Add(new MediaFileOrderKey(
                    "COALESCE(Artist, '') COLLATE LIGHTPLAYER_NOCASE",
                    request.Ascending,
                    primaryText));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            case MediaFileSortField.Genre:
                keys.Add(new MediaFileOrderKey(
                    "COALESCE(Genre, '') COLLATE LIGHTPLAYER_NOCASE",
                    request.Ascending,
                    primaryText));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            case MediaFileSortField.Year:
                keys.Add(new MediaFileOrderKey(
                    "lightplayer_leading_int(Date)",
                    request.Ascending,
                    primaryNumber));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            case MediaFileSortField.DateAdded:
                keys.Add(new MediaFileOrderKey(
                    "lightplayer_utc_ticks(DatabaseItemAddedDate)",
                    request.Ascending,
                    primaryNumber));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            case MediaFileSortField.Duration:
                keys.Add(new MediaFileOrderKey(
                    "lightplayer_duration_ticks(Duration)",
                    request.Ascending,
                    primaryNumber));
                keys.Add(new MediaFileOrderKey(title, true, cursor?.Title ?? string.Empty));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), "Unsupported media file sort field.");
        }

        keys.Add(new MediaFileOrderKey("Id", true, cursor?.Id ?? 0));
        return keys;
    }

    private static string EncodeMediaFileCursor(MediaFilePageRequest request, DbMediaFile file)
    {
        var cursor = new MediaFilePageCursor(
            request.Sort,
            request.Ascending,
            request.Sort switch
            {
                MediaFileSortField.AlbumName => file.Album ?? string.Empty,
                MediaFileSortField.ArtistName => file.Artist ?? string.Empty,
                MediaFileSortField.Genre => file.Genre ?? string.Empty,
                MediaFileSortField.Title => TrackTitle(file.Title, file.Path),
                _ => string.Empty,
            },
            request.Sort switch
            {
                MediaFileSortField.Year => ParseLeadingInt(file.Date),
                MediaFileSortField.DateAdded => file.DatabaseItemAddedDate.UtcTicks,
                MediaFileSortField.Duration => file.Duration.Ticks,
                _ => 0L,
            },
            ParseLeadingInt(file.DiscNumber),
            ParseLeadingInt(file.TrackNumber),
            TrackTitle(file.Title, file.Path),
            file.Id,
            request.FolderPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            cursor,
            MediaFilePagingJsonContext.Default.MediaFilePageCursor);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static MediaFilePageCursor DecodeMediaFileCursor(MediaFilePageRequest request)
    {
        try
        {
            var encoded = request.Cursor!.Replace('-', '+').Replace('_', '/');
            encoded = (encoded.Length % 4) switch
            {
                2 => encoded + "==",
                3 => encoded + "=",
                _ => encoded,
            };
            var cursor = JsonSerializer.Deserialize(
                Convert.FromBase64String(encoded),
                MediaFilePagingJsonContext.Default.MediaFilePageCursor);
            if (cursor is null || cursor.Sort != request.Sort || cursor.Ascending != request.Ascending ||
                cursor.FolderPath != request.FolderPath)
            {
                throw new ArgumentException("The page cursor does not match the requested folder or ordering.", nameof(request));
            }

            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The page cursor is invalid.", nameof(request), exception);
        }
    }

    private static int ParseLeadingInt(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var length = 0;
        while (length < value.Length && length < 9 && char.IsDigit(value[length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(value.AsSpan(0, length), out var parsed) ? parsed : 0;
    }

    private static string TrackTitle(string title, string path) =>
        string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileNameWithoutExtension(path)
            : title;

    private static string VideoTitle(string title, string fileName) =>
        string.IsNullOrWhiteSpace(title) ? fileName ?? string.Empty : title;

    private static long ShuffleKey(long id) => unchecked((long)((uint)id * 2654435761u));

    private static long ShufflePivot(long seed)
    {
        var value = unchecked((ulong)seed + 0x9e3779b97f4a7c15UL);
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return (uint)(value ^ (value >> 31));
    }

    private static ShuffledMediaFilePageCursor DecodeShuffleCursor(ShuffledMediaFilePageRequest request)
    {
        var cursor = DecodeCursor(
            request.Cursor,
            MediaFilePagingJsonContext.Default.ShuffledMediaFilePageCursor);
        if (cursor.Seed != request.Seed || cursor.Phase is < 0 or > 1)
        {
            throw new ArgumentException("The page cursor does not match the requested shuffle.", nameof(request));
        }

        return cursor;
    }

    private static VideoPageCursor DecodeVideoCursor(PageRequest request) =>
        DecodeCursor(request.Cursor, MediaFilePagingJsonContext.Default.VideoPageCursor);

    private static string EncodeCursor<T>(T cursor, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, typeInfo);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static T DecodeCursor<T>(string value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = (encoded.Length % 4) switch
            {
                2 => encoded + "==",
                3 => encoded + "=",
                _ => encoded,
            };
            return JsonSerializer.Deserialize(Convert.FromBase64String(encoded), typeInfo)
                ?? throw new ArgumentException("The page cursor is invalid.", nameof(value));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The page cursor is invalid.", nameof(value), exception);
        }
    }

    private static long ParseUtcTicks(string value) =>
        string.IsNullOrEmpty(value) ? 0 : ParseDateTimeOffset(value).UtcTicks;

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MediaFilePageCursor))]
[JsonSerializable(typeof(AlbumPageCursor))]
[JsonSerializable(typeof(ArtistPageCursor))]
[JsonSerializable(typeof(ShuffledMediaFilePageCursor))]
[JsonSerializable(typeof(VideoPageCursor))]
[JsonSerializable(typeof(MediaPageCursor))]
internal sealed partial class MediaFilePagingJsonContext : JsonSerializerContext;

internal sealed record MediaFilePageCursor(
    MediaFileSortField Sort,
    bool Ascending,
    string PrimaryText,
    long PrimaryNumber,
    int DiscNumber,
    int TrackNumber,
    string Title,
    int Id,
    string FolderPath = null);

internal sealed record AlbumPageCursor(
    AlbumSortField Sort,
    bool Ascending,
    string Title,
    string Artist,
    string ArtistForQuery,
    int Year,
    string AlbumGroupingKey = "",
    AlbumGroupingMode GroupingMode = AlbumGroupingMode.AlwaysCombine);

internal sealed record ArtistPageCursor(bool Ascending, string Name);

internal sealed record ShuffledMediaFilePageCursor(long Seed, int Phase, long Key, int Id);

internal sealed record VideoPageCursor(string Name, int Id);

internal sealed record MediaPageCursor(string Name, int Id, bool IsImage);

internal readonly record struct MediaFileOrderKey(string Expression, bool Ascending, object CursorValue);

[JsonSerializable(typeof(DbVideoAudioStream[]))]
internal sealed partial class VideoDatabaseJsonContext : JsonSerializerContext;
