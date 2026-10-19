#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database;

public interface IAsyncSqliteConnection : IAsyncDisposable
{
    long Generation { get; }
    AsyncSqliteCommand CreateCommand();
    ValueTask<IAsyncSqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    ValueTask ExportAsync(Stream destination, CancellationToken cancellationToken = default);
}

public interface IAsyncSqliteTransaction : IAsyncDisposable
{
    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}

public interface IAsyncSqliteReader : IAsyncDisposable
{
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default);
    bool IsDBNull(int ordinal);
    string GetString(int ordinal);
    int GetInt32(int ordinal);
    long GetInt64(int ordinal);
    double GetDouble(int ordinal);
    float GetFloat(int ordinal);
}

public sealed class SqliteParameters : Dictionary<string, object?>
{
    public void AddWithValue(string name, object? value) => this[name] = value is DBNull ? null : value;
}

public abstract class AsyncSqliteCommand : IAsyncDisposable
{
    public string CommandText { get; set; } = string.Empty;
    public SqliteParameters Parameters { get; } = new();
    public IAsyncSqliteTransaction? Transaction { get; set; }

    public abstract ValueTask<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask<IAsyncSqliteReader> ExecuteReaderAsync(CancellationToken cancellationToken = default);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
