using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using LightStudio.MediaLibraryCore.Database;
using Microsoft.Data.Sqlite;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserSqliteConnection : IAsyncSqliteConnection
{
    private const string ResetPendingKey = "indexDatabaseReset.pending";
    private static int nextRequest;
    private readonly string path;
    private readonly SemaphoreSlim operations = new(1, 1);
    private string? identity;
    private Transaction? transaction;
    private Reader? reader;
    private bool disposed;
    private long generation;

    public long Generation => Volatile.Read(ref generation);

    internal BrowserSqliteConnection(string connectionString)
    {
        var options = new SqliteConnectionStringBuilder(connectionString);
        path = options.DataSource == ":memory:" ? options.DataSource : Path.GetFullPath(options.DataSource);
    }

    internal static async Task RegisterAsync()
    {
        await BrowserAssets.ImportAsync("BrowserSqlite", "sqlite/client.js");
        var casing = new List<int>();
        var uppercase = new List<(int Scalar, string Text)>();
        var lowercase = new List<(int Scalar, string Text)>();
        var turn = Stopwatch.GetTimestamp();
        for (var scalar = 0; scalar <= 0x10ffff; scalar++)
        {
            if ((scalar & 1023) == 0 && Stopwatch.GetElapsedTime(turn).TotalMilliseconds >= 4)
            {
                await NextTurnAsync();
                turn = Stopwatch.GetTimestamp();
            }
            if (!Rune.IsValid(scalar)) continue;
            var source = new Rune(scalar);
            if (scalar > char.MaxValue)
            {
                var category = Rune.GetUnicodeCategory(source);
                if (category == UnicodeCategory.UppercaseLetter)
                    uppercase.Add((scalar, source.ToString()));
                else if (category is UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter)
                    lowercase.Add((scalar, source.ToString()));
            }
            else
            {
                var upper = Rune.ToUpperInvariant(source);
                if (source != upper && string.Equals(source.ToString(), upper.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    casing.Add(scalar);
                    casing.Add(upper.Value);
                }
            }
        }
        foreach (var source in lowercase)
        {
            if (Stopwatch.GetElapsedTime(turn).TotalMilliseconds >= 4)
            {
                await NextTurnAsync();
                turn = Stopwatch.GetTimestamp();
            }
            var low = 0;
            var high = uppercase.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var comparison = string.Compare(source.Text, uppercase[middle].Text, StringComparison.OrdinalIgnoreCase);
                if (comparison == 0)
                {
                    casing.Add(source.Scalar);
                    casing.Add(uppercase[middle].Scalar);
                    break;
                }
                if (comparison < 0) high = middle - 1;
                else low = middle + 1;
            }
        }
        ConfigureOrdinalCasing(casing.ToArray());
        SqliteDatabase.ConnectionFactory = static (connectionString, _) => new BrowserSqliteConnection(connectionString);
    }

    public AsyncSqliteCommand CreateCommand() => new Command(this);

    internal static async Task DeleteDatabaseAsync(string path) =>
        await SendAsync(new SqliteRequest { Operation = "delete", Path = Path.GetFullPath(path) }, CancellationToken.None).ConfigureAwait(false);

    internal static Task QueueIndexDatabaseResetAsync() => BrowserStorage.SetAsync(ResetPendingKey, "true");

    internal static async Task ApplyPendingIndexDatabaseResetAsync()
    {
        if (!BrowserStorage.GetBoolean(ResetPendingKey))
            return;

        await SendAsync(new SqliteRequest { Operation = "resetIndexes" }, CancellationToken.None).ConfigureAwait(false);
        await BrowserStorage.RemoveAsync(ResetPendingKey).ConfigureAwait(false);
        LightStudio.Logging.AppLog.Information("Web.Sqlite", "Reset the local library and semantic index databases.");
    }

    private static async Task<SqliteReply> SendAsync(SqliteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = Interlocked.Increment(ref nextRequest);
        if (requestId <= 0) throw new InvalidOperationException("SQLite request identities were exhausted.");
        return await BrowserMainThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = Request(requestId, JsonSerializer.Serialize(request, SqliteTransportJsonContext.Default.SqliteRequest));
            using var registration = cancellationToken.Register(() => _ = CancelAsync(requestId));
            try
            {
                var json = await pending;
                return JsonSerializer.Deserialize(json, SqliteTransportJsonContext.Default.SqliteReply)
                    ?? throw new InvalidDataException("The SQLite worker returned an empty response.");
            }
            catch (JSException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("SQLite execution was canceled.", exception, cancellationToken);
            }
        }).ConfigureAwait(false);
    }

    private static async Task CancelAsync(int requestId)
    {
        try
        {
            await BrowserMainThread.InvokeAsync(() =>
            {
                Cancel(requestId);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LightStudio.Logging.AppLog.Warning("Web.Sqlite", "Could not deliver SQLite cancellation.", exception);
        }
    }

    private async ValueTask<SqliteReply> ExecuteAsync(SqliteRequest request, CancellationToken cancellationToken,
        IAsyncSqliteTransaction? expectedTransaction = null, bool command = false, Action<SqliteReply>? completed = null)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (command && (reader is not null || expectedTransaction != transaction))
                throw new InvalidOperationException("The SQLite command has an active reader or mismatched transaction.");
            identity ??= (await SendAsync(new SqliteRequest { Operation = "open", Path = path }, cancellationToken).ConfigureAwait(false)).Connection
                ?? throw new InvalidDataException("The SQLite worker did not return a connection identity.");
            var result = await SendAsync(request with { Connection = identity }, cancellationToken).ConfigureAwait(false);
            completed?.Invoke(result);
            return result;
        }
        catch
        {
            if (identity is not null && !await BrowserMainThread.InvokeAsync(() => Task.FromResult(IsConnectionAlive(identity))).ConfigureAwait(false))
            {
                identity = null;
                transaction = null;
                reader = null;
                Interlocked.Increment(ref generation);
            }
            throw;
        }
        finally { operations.Release(); }
    }

    public async ValueTask<IAsyncSqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        Transaction? opened = null;
        await ExecuteAsync(new SqliteRequest { Operation = "begin" }, cancellationToken, command: true,
            completed: result => transaction = opened = new Transaction(this,
                result.Transaction ?? throw new InvalidDataException("Missing SQLite transaction identity."))).ConfigureAwait(false);
        return opened!;
    }

    public async ValueTask ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(new SqliteRequest { Operation = "exportBegin" }, cancellationToken, command: true).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var reply = await ExecuteAsync(new SqliteRequest { Operation = "exportRead" }, cancellationToken).ConfigureAwait(false);
                if (reply.Done) break;
                var bytes = Convert.FromBase64String(reply.Data ?? throw new InvalidDataException("Missing SQLite export block."));
                if (bytes.Length > 65536) throw new InvalidDataException("SQLite export exceeded its transfer budget.");
                await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ExecuteAsync(new SqliteRequest { Operation = "exportEnd" }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (identity is not null)
                await SendAsync(new SqliteRequest { Operation = "close", Connection = identity }, CancellationToken.None).ConfigureAwait(false);
            identity = null;
            reader = null;
            transaction = null;
        }
        finally { operations.Release(); }
    }

    private sealed class Command(BrowserSqliteConnection owner) : AsyncSqliteCommand
    {
        private ValueTask<SqliteReply> ExecuteAsync(string operation, CancellationToken cancellationToken, Action<SqliteReply>? completed = null)
        {
            var bytes = CommandText.Length * 2L;
            foreach (var parameter in Parameters)
            {
                bytes += parameter.Key.Length * 2L + (parameter.Value switch
                {
                    string text => text.Length * 2L,
                    byte[] blob => blob.Length * 3L,
                    _ => 32,
                });
                if (bytes > 4 * 1024 * 1024)
                    throw new ArgumentException("The SQLite command exceeds its byte budget.");
            }
            return owner.ExecuteAsync(new SqliteRequest
            {
                Operation = operation,
                Sql = CommandText,
                Transaction = (Transaction as Transaction)?.Identity,
                Parameters = Parameters.Select(parameter => new SqliteParameter(parameter.Key, SqliteValue.From(parameter.Value))).ToArray(),
            }, cancellationToken, Transaction, command: true, completed);
        }

        public override async ValueTask<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) =>
            (await ExecuteAsync("execute", cancellationToken).ConfigureAwait(false)).Changes;

        public override async ValueTask<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) =>
            (await ExecuteAsync("scalar", cancellationToken).ConfigureAwait(false)).Value?.ToObject();

        public override async ValueTask<IAsyncSqliteReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        {
            Reader? opened = null;
            await ExecuteAsync("reader", cancellationToken, result =>
            {
                opened = new Reader(owner, result);
                if (!result.Done) owner.reader = opened;
            }).ConfigureAwait(false);
            return opened!;
        }
    }

    private sealed class Transaction(BrowserSqliteConnection owner, string identity) : IAsyncSqliteTransaction
    {
        internal string Identity { get; } = identity;

        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            if (owner.transaction != this) throw new InvalidOperationException("The SQLite transaction is no longer active.");
            await owner.ExecuteAsync(new SqliteRequest { Operation = "commit", Transaction = Identity }, cancellationToken, this, command: true,
                completed: _ => owner.transaction = null).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.disposed || owner.transaction != this) return;
            await owner.ExecuteAsync(new SqliteRequest { Operation = "rollback", Transaction = Identity }, CancellationToken.None,
                completed: _ => { owner.transaction = null; owner.reader = null; }).ConfigureAwait(false);
        }
    }

    private sealed class Reader(BrowserSqliteConnection owner, SqliteReply first) : IAsyncSqliteReader
    {
        private SqliteValue[][] rows = first.Rows;
        private int position = -1;
        private bool done = first.Done;

        public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++position < rows.Length) return true;
            if (done || owner.disposed) return false;
            if (owner.reader != this) throw new InvalidOperationException("The SQLite reader belongs to an obsolete connection generation.");
            var batch = await owner.ExecuteAsync(new SqliteRequest { Operation = "read" }, cancellationToken).ConfigureAwait(false);
            rows = batch.Rows;
            position = 0;
            done = batch.Done;
            if (done) owner.reader = null;
            return rows.Length > 0;
        }

        public bool IsDBNull(int ordinal) => rows[position][ordinal].Kind == 5;
        public string GetString(int ordinal) => rows[position][ordinal].Text!;
        public int GetInt32(int ordinal) => checked((int)GetInt64(ordinal));
        public long GetInt64(int ordinal) => long.Parse(GetString(ordinal), CultureInfo.InvariantCulture);
        public double GetDouble(int ordinal) => double.Parse(GetString(ordinal), CultureInfo.InvariantCulture);
        public float GetFloat(int ordinal) => float.Parse(GetString(ordinal), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            if (!done && !owner.disposed && owner.reader == this)
                await owner.ExecuteAsync(new SqliteRequest { Operation = "finish" }, CancellationToken.None).ConfigureAwait(false);
            done = true;
            rows = [];
            if (owner.reader == this) owner.reader = null;
        }
    }

    [JSImport("request", "BrowserSqlite")]
    private static partial Task<string> Request(int requestId, string json);

    [JSImport("cancel", "BrowserSqlite")]
    private static partial void Cancel(int requestId);

    [JSImport("configureOrdinalCasing", "BrowserSqlite")]
    private static partial void ConfigureOrdinalCasing(int[] pairs);

    [JSImport("nextTurn", "BrowserSqlite")]
    private static partial Task NextTurnAsync();

    [JSImport("isConnectionAlive", "BrowserSqlite")]
    private static partial bool IsConnectionAlive(string identity);
}

internal sealed record SqliteRequest
{
    public required string Operation { get; init; }
    public string? Connection { get; init; }
    public string? Path { get; init; }
    public string? Sql { get; init; }
    public string? Transaction { get; init; }
    public SqliteParameter[] Parameters { get; init; } = [];
}

internal sealed record SqliteParameter(string Name, SqliteValue Value);

internal sealed record SqliteValue(int Kind, string? Text)
{
    internal static SqliteValue From(object? value) => value switch
    {
        null or DBNull => new(5, null),
        string text => new(3, text),
        byte[] bytes => new(4, Convert.ToBase64String(bytes)),
        bool boolean => new(1, boolean ? "1" : "0"),
        byte or sbyte or short or ushort or int or uint or long => new(1, Convert.ToString(value, CultureInfo.InvariantCulture)),
        float or double => new(2, Convert.ToString(value, CultureInfo.InvariantCulture)),
        _ => throw new NotSupportedException($"SQLite parameter type {value.GetType().Name} is not supported."),
    };

    internal object? ToObject() => Kind switch
    {
        5 => null,
        1 => long.Parse(Text!, CultureInfo.InvariantCulture),
        2 => double.Parse(Text!, CultureInfo.InvariantCulture),
        3 => Text,
        4 => Convert.FromBase64String(Text!),
        _ => throw new InvalidDataException("Invalid SQLite value type."),
    };
}

internal sealed record SqliteReply
{
    public string? Connection { get; init; }
    public string? Transaction { get; init; }
    public int Changes { get; init; }
    public SqliteValue? Value { get; init; }
    public SqliteValue[][] Rows { get; init; } = [];
    public bool Done { get; init; }
    public string? Data { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SqliteRequest))]
[JsonSerializable(typeof(SqliteReply))]
internal sealed partial class SqliteTransportJsonContext : JsonSerializerContext;
