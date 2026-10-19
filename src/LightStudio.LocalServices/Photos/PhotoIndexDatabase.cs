using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LocalServices.Photos;

internal sealed class PhotoIndexDatabase : IAsyncDisposable
{
    private readonly IAsyncSqliteConnection connection;
    private readonly SemaphoreSlim sync = new(1, 1);
    private bool initialized;
    private string? activeModelId;
    private int embeddingDimensions;
    private long modelGeneration = -1;

    internal PhotoIndexDatabase(string directory)
    {
        Directory.CreateDirectory(directory);
        SqliteDatabase.InitializeVectors ??= PhotoVectorExtension.Load;
        var path = Path.Combine(directory, "photo-v1.sqlite").Replace("\"", "\"\"");
        connection = SqliteDatabase.CreateConnection($"Data Source=\"{path}\";Pooling=False", vectors: true);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            if (initialized)
                return;
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA journal_mode={(OperatingSystem.IsBrowser() ? "DELETE" : "WAL")};
                PRAGMA synchronous={(OperatingSystem.IsBrowser() ? "FULL" : "NORMAL")};
                CREATE TABLE IF NOT EXISTS photos (
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    root TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    format TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc INTEGER NOT NULL,
                    created_utc INTEGER,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    date_taken TEXT,
                    camera_make TEXT,
                    camera_model TEXT,
                    lens TEXT,
                    exposure_seconds REAL,
                    aperture REAL,
                    focal_length REAL,
                    iso INTEGER,
                    orientation INTEGER,
                    latitude REAL,
                    longitude REAL,
                    altitude REAL,
                    description TEXT,
                    artist TEXT,
                    copyright TEXT,
                    alt_text TEXT,
                    ocr_text TEXT,
                    ocr_language TEXT,
                    ocr_model TEXT,
                    model_id TEXT NOT NULL,
                    last_seen TEXT NOT NULL,
                    error TEXT
                );
                CREATE INDEX IF NOT EXISTS photos_root ON photos(root, last_seen);
                CREATE INDEX IF NOT EXISTS photos_date ON photos(date_taken);
                CREATE TABLE IF NOT EXISTS photo_index_model (
                    id INTEGER PRIMARY KEY CHECK(id=1),
                    model_id TEXT NOT NULL,
                    dimensions INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            initialized = true;
        }
        finally { sync.Release(); }
    }

    internal async Task SetModelAsync(string modelId, int dimensions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 8192);
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            if (activeModelId == modelId && embeddingDimensions == dimensions && modelGeneration == connection.Generation)
                return;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken: cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM photo_index_model WHERE id=1 AND model_id=$model AND dimensions=$dimensions";
            command.Parameters.AddWithValue("$model", modelId);
            command.Parameters.AddWithValue("$dimensions", dimensions);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken: cancellationToken)) == 0)
            {
                var createVectors = OperatingSystem.IsBrowser()
                    ? "CREATE TABLE photo_vectors(rowid INTEGER PRIMARY KEY, embedding BLOB NOT NULL);"
                    : $"CREATE VIRTUAL TABLE photo_vectors USING vec0(embedding float[{dimensions.ToString(CultureInfo.InvariantCulture)}] distance_metric=cosine);";
                command.CommandText = $"""
                    DROP TABLE IF EXISTS photo_vectors;
                    DELETE FROM photos;
                    DELETE FROM photo_index_model;
                    {createVectors}
                    INSERT INTO photo_index_model(id,model_id,dimensions) VALUES(1,$model,$dimensions);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken: cancellationToken);
            activeModelId = modelId;
            embeddingDimensions = dimensions;
            modelGeneration = connection.Generation;
        }
        finally { sync.Release(); }
    }

    internal async Task<int> CountAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM photos WHERE model_id=$model AND error IS NULL";
            command.Parameters.AddWithValue("$model", modelId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken: cancellationToken));
        }
        finally { sync.Release(); }
    }

    internal async Task<bool> MarkUnchangedAsync(string path, string root, long size, DateTime modifiedUtc, string modelId, string run, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            EnsureModel(modelId);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE photos SET last_seen=$run, root=$root
                WHERE path=$path AND size_bytes=$size AND modified_utc=$modified AND model_id=$model AND error IS NULL
                """;
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$modified", modifiedUtc.Ticks);
            command.Parameters.AddWithValue("$model", modelId);
            return await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken) != 0;
        }
        finally { sync.Release(); }
    }

    internal async Task UpsertAsync(PhotoMetadata photo, float[]? embedding, string modelId, string run, string? error, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            EnsureModel(modelId);
            if (embedding is not null && embedding.Length != embeddingDimensions)
                throw new ArgumentException("The embedding dimensions do not match the active photo model.", nameof(embedding));
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken: cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO photos(path,root,file_name,format,size_bytes,modified_utc,created_utc,width,height,date_taken,
                    camera_make,camera_model,lens,exposure_seconds,aperture,focal_length,iso,orientation,
                    latitude,longitude,altitude,description,artist,copyright,model_id,last_seen,error)
                VALUES($path,$root,$name,$format,$size,$modified,$created,$width,$height,$date,
                    $make,$camera,$lens,$exposure,$aperture,$focal,$iso,$orientation,
                    $latitude,$longitude,$altitude,$description,$artist,$copyright,$model,$run,$error)
                ON CONFLICT(path) DO UPDATE SET root=excluded.root,file_name=excluded.file_name,format=excluded.format,
                    size_bytes=excluded.size_bytes,modified_utc=excluded.modified_utc,created_utc=excluded.created_utc,
                    width=excluded.width,height=excluded.height,date_taken=excluded.date_taken,camera_make=excluded.camera_make,
                    camera_model=excluded.camera_model,lens=excluded.lens,exposure_seconds=excluded.exposure_seconds,
                    aperture=excluded.aperture,focal_length=excluded.focal_length,iso=excluded.iso,orientation=excluded.orientation,
                    latitude=excluded.latitude,longitude=excluded.longitude,altitude=excluded.altitude,
                    description=excluded.description,artist=excluded.artist,copyright=excluded.copyright,
                    model_id=excluded.model_id,last_seen=excluded.last_seen,error=excluded.error
                RETURNING id
                """;
            Add("$path", photo.Path);
            Add("$root", photo.Root);
            Add("$name", photo.FileName);
            Add("$format", photo.Format);
            Add("$size", photo.SizeInBytes);
            Add("$modified", photo.LastModifiedUtc.Ticks);
            Add("$created", photo.CreatedUtc?.Ticks);
            Add("$width", photo.Width);
            Add("$height", photo.Height);
            Add("$date", photo.DateTaken);
            Add("$make", photo.CameraMake);
            Add("$camera", photo.CameraModel);
            Add("$lens", photo.Lens);
            Add("$exposure", photo.ExposureSeconds);
            Add("$aperture", photo.Aperture);
            Add("$focal", photo.FocalLength);
            Add("$iso", photo.Iso);
            Add("$orientation", photo.Orientation);
            Add("$latitude", photo.Latitude);
            Add("$longitude", photo.Longitude);
            Add("$altitude", photo.Altitude);
            Add("$description", photo.Description);
            Add("$artist", photo.Artist);
            Add("$copyright", photo.Copyright);
            Add("$model", embedding is null ? "" : modelId);
            Add("$run", run);
            Add("$error", error);
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken: cancellationToken));
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM photo_vectors WHERE rowid=$id";
            Add("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            if (embedding is not null)
            {
                command.CommandText = "INSERT INTO photo_vectors(rowid,embedding) VALUES($id,$embedding)";
                Add("$embedding", MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray());
                await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        finally { sync.Release(); }
    }

    internal async Task RemoveMissingAsync(string root, string run, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken: cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM photo_vectors WHERE rowid IN (SELECT id FROM photos WHERE root=$root AND last_seen<>$run);
                DELETE FROM photos WHERE root=$root AND last_seen<>$run;
                """;
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$run", run);
            await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken: cancellationToken);
        }
        finally { sync.Release(); }
    }

    internal async Task RemoveOtherRootsAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken: cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS active_photo_roots(path TEXT PRIMARY KEY); DELETE FROM active_photo_roots;";
            await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            command.CommandText = "INSERT OR IGNORE INTO active_photo_roots VALUES($root)";
            foreach (var root in roots)
            {
                command.Parameters.AddWithValue("$root", root);
                await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            }
            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM photo_vectors WHERE rowid IN (SELECT id FROM photos WHERE root NOT IN (SELECT path FROM active_photo_roots));
                DELETE FROM photos WHERE root NOT IN (SELECT path FROM active_photo_roots);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken: cancellationToken);
        }
        finally { sync.Release(); }
    }

    internal async Task<List<(long Id, double Score)>> RankAsync(float[] query, string modelId, IReadOnlyList<string> roots,
        Func<string, string, bool> includePath, CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
            return [];
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            EnsureModel(modelId);
            if (query.Length != embeddingDimensions)
                throw new ArgumentException("The query dimensions do not match the active photo model.", nameof(query));
            await using var command = connection.CreateCommand();
            var rootParameters = new List<string>(roots.Count);
            for (var index = 0; index < roots.Count; index++)
            {
                var parameter = "$root" + index.ToString(CultureInfo.InvariantCulture);
                rootParameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, roots[index]);
            }
            command.CommandText = $"""
                SELECT photo_vectors.rowid, vec_distance_cosine(embedding,$query) AS distance, photos.path, photos.root
                FROM photo_vectors JOIN photos ON photos.id=photo_vectors.rowid
                WHERE photos.model_id=$model AND photos.error IS NULL AND photos.root IN ({string.Join(",", rootParameters)})
                ORDER BY distance, photo_vectors.rowid LIMIT 100000
                """;
            var queryBytes = MemoryMarshal.AsBytes(query.AsSpan()).ToArray();
            command.Parameters.AddWithValue("$query", queryBytes);
            command.Parameters.AddWithValue("$model", modelId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
            var result = new List<(long Id, double Score)>();
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includePath(reader.GetString(2), reader.GetString(3)))
                    continue;
                var distance = reader.GetDouble(1);
                result.Add((reader.GetInt64(0), 1 - distance));
            }
            return result;
        }
        finally { sync.Release(); }
    }

    internal async Task<IReadOnlyList<PhotoSearchMatch>> ReadPageAsync(List<(long Id, double Score)> ranked, int offset, int count, string modelId, CancellationToken cancellationToken = default)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false); try
        {
            EnsureModel(modelId);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT path,file_name,size_bytes,width,height,date_taken FROM photos WHERE id=$id AND model_id=$model AND error IS NULL";
            command.Parameters.AddWithValue("$model", modelId);
            var result = new List<PhotoSearchMatch>(count);
            for (var index = offset; index < Math.Min(ranked.Count, offset + count); index++)
            {
                command.Parameters.AddWithValue("$id", ranked[index].Id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
                if (await reader.ReadAsync(cancellationToken: cancellationToken))
                    result.Add(new PhotoSearchMatch(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                        reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), ranked[index].Score));
            }
            return result;
        }
        finally { sync.Release(); }
    }

    private void EnsureModel(string modelId)
    {
        if (modelId != activeModelId || modelGeneration != connection.Generation)
            throw new InvalidOperationException("The photo index belongs to a different model.");
    }

    public async ValueTask DisposeAsync()
    {
        await sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            sync.Release();
        }
    }
}