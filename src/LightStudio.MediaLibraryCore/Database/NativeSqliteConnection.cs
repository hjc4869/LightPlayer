#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace LightStudio.MediaLibraryCore.Database;

public sealed class NativeSqliteConnection : IAsyncSqliteConnection
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DatabaseGates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim databaseGate;
    private readonly string connectionString;
    private readonly Action<SqliteConnection>? initialize;
    private SqliteConnection? connection;
    private Transaction? transaction;
    private Reader? reader;
    private bool ownsDatabaseGate;
    private bool disposed;

    public NativeSqliteConnection(string connectionString, Action<SqliteConnection>? initialize = null)
    {
        var options = new SqliteConnectionStringBuilder(connectionString);
        databaseGate = options.DataSource is "" or ":memory:"
            ? new SemaphoreSlim(1, 1)
            : DatabaseGates.GetOrAdd(Path.GetFullPath(options.DataSource), static _ => new SemaphoreSlim(1, 1));
        this.connectionString = options.ToString();
        this.initialize = initialize;
    }

    public AsyncSqliteCommand CreateCommand() => new Command(this);
    public long Generation => 0;

    public ValueTask<IAsyncSqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        RunAsync<IAsyncSqliteTransaction>(() =>
        {
            EnsureNoReader();
            if (transaction is not null)
                throw new InvalidOperationException("A transaction is already active.");
            transaction = new Transaction(this, Connection.BeginTransaction());
            return transaction;
        }, cancellationToken);

    public async ValueTask ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), "lightplayer-sqlite-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RunAsync(() =>
            {
                EnsureNoReader();
                if (transaction is not null)
                    throw new InvalidOperationException("Cannot export an uncommitted transaction.");
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString());
                backup.Open();
                Connection.BackupDatabase(backup);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private SqliteConnection Connection
    {
        get
        {
            if (connection is not null)
                return connection;
            var opened = new SqliteConnection(connectionString);
            try
            {
                opened.Open();
                initialize?.Invoke(opened);
                connection = opened;
                return opened;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }
    }

    private async ValueTask<TResult> RunAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken,
        bool closing = false)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                if (closing)
                    return default!;
                throw new ObjectDisposedException(nameof(NativeSqliteConnection));
            }
            if (!ownsDatabaseGate && !closing)
            {
                await databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                ownsDatabaseGate = true;
            }
            try
            {
                try
                {
                    return await Task.Run(action, cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("SQLite execution was interrupted.", exception, cancellationToken);
                }
            }
            finally
            {
                if (ownsDatabaseGate && transaction is null && reader is null)
                {
                    ownsDatabaseGate = false;
                    databaseGate.Release();
                }
            }
        }
        finally
        {
            operations.Release();
        }
    }

    private void EnsureNoReader()
    {
        if (reader is not null)
            throw new InvalidOperationException("Finish or dispose the active SQLite reader before executing another command.");
    }

    public async ValueTask DisposeAsync()
    {
        await RunAsync(() =>
        {
            try
            {
                reader?.Close();
                transaction?.Native.Dispose();
            }
            finally
            {
                transaction = null;
                connection?.Dispose();
                connection = null;
                disposed = true;
            }
            return true;
        }, CancellationToken.None, closing: true).ConfigureAwait(false);
    }

    private sealed class Command(NativeSqliteConnection owner) : AsyncSqliteCommand
    {
        private SqliteCommand CreateNative()
        {
            owner.EnsureNoReader();
            if (Transaction != owner.transaction)
                throw new InvalidOperationException("The command does not belong to the active SQLite transaction.");
            var command = owner.Connection.CreateCommand();
            command.CommandText = CommandText;
            command.Transaction = owner.transaction?.Native;
            foreach (var parameter in Parameters)
                command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
            return command;
        }

        public override ValueTask<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) =>
            owner.RunAsync(() =>
            {
                using var command = CreateNative();
                using var registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(owner.Connection.Handle));
                cancellationToken.ThrowIfCancellationRequested();
                return command.ExecuteNonQuery();
            }, cancellationToken);

        public override ValueTask<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) =>
            owner.RunAsync(() =>
            {
                using var command = CreateNative();
                using var registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(owner.Connection.Handle));
                cancellationToken.ThrowIfCancellationRequested();
                return command.ExecuteScalar();
            }, cancellationToken);

        public override ValueTask<IAsyncSqliteReader> ExecuteReaderAsync(CancellationToken cancellationToken = default) =>
            owner.RunAsync<IAsyncSqliteReader>(() =>
            {
                var command = CreateNative();
                try
                {
                    using var registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(owner.Connection.Handle));
                    cancellationToken.ThrowIfCancellationRequested();
                    owner.reader = new Reader(owner, command, command.ExecuteReader());
                    return owner.reader;
                }
                catch
                {
                    command.Dispose();
                    throw;
                }
            }, cancellationToken);
    }

    private sealed class Transaction(NativeSqliteConnection owner, SqliteTransaction native) : IAsyncSqliteTransaction
    {
        internal SqliteTransaction Native { get; } = native;

        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            await owner.RunAsync(() =>
            {
                if (owner.transaction != this)
                    throw new InvalidOperationException("The transaction is no longer active.");
                owner.EnsureNoReader();
                Native.Commit();
                Native.Dispose();
                owner.transaction = null;
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.transaction != this)
                return;
            await owner.RunAsync(() =>
            {
                if (owner.transaction == this)
                {
                    owner.reader?.Close();
                    try { Native.Dispose(); }
                    finally { owner.transaction = null; }
                }
                return true;
            }, CancellationToken.None, closing: true).ConfigureAwait(false);
        }
    }

    private sealed class Reader(NativeSqliteConnection owner, SqliteCommand command, SqliteDataReader native) : IAsyncSqliteReader
    {
        private readonly Queue<object[]> rows = new();
        private object[] current = [];
        private bool closed;

        public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == 0 && !closed)
            {
                await owner.RunAsync(() =>
                {
                    if (closed)
                        return false;
                    using var registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(owner.Connection.Handle));
                    try
                    {
                        var bytes = 0L;
                        while (rows.Count < 128 && bytes < 1024 * 1024)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!native.Read())
                            {
                                Close();
                                break;
                            }
                            var values = new object[native.FieldCount];
                            native.GetValues(values);
                            foreach (var value in values)
                                bytes += value is string text ? text.Length * 2L : value is byte[] blob ? blob.Length : 8;
                            rows.Enqueue(values);
                        }
                    }
                    catch
                    {
                        Close();
                        throw;
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            if (!rows.TryDequeue(out var row))
                return false;
            current = row;
            return true;
        }

        public bool IsDBNull(int ordinal) => current[ordinal] is DBNull;
        public string GetString(int ordinal) => (string)current[ordinal];
        public int GetInt32(int ordinal) => Convert.ToInt32(current[ordinal], CultureInfo.InvariantCulture);
        public long GetInt64(int ordinal) => Convert.ToInt64(current[ordinal], CultureInfo.InvariantCulture);
        public double GetDouble(int ordinal) => Convert.ToDouble(current[ordinal], CultureInfo.InvariantCulture);
        public float GetFloat(int ordinal) => Convert.ToSingle(current[ordinal], CultureInfo.InvariantCulture);

        internal void Close()
        {
            if (closed)
                return;
            closed = true;
            try { native.Dispose(); }
            finally
            {
                command.Dispose();
                owner.reader = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (closed)
            {
                rows.Clear();
                current = [];
                return;
            }
            await owner.RunAsync(() =>
            {
                Close();
                rows.Clear();
                current = [];
                return true;
            }, CancellationToken.None, closing: true).ConfigureAwait(false);
        }
    }
}
