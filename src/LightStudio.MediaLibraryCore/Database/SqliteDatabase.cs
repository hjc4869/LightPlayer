#nullable enable

using System;
using Microsoft.Data.Sqlite;

namespace LightStudio.MediaLibraryCore.Database;

public static class SqliteDatabase
{
    public static Func<string, bool, IAsyncSqliteConnection> ConnectionFactory { get; set; } = CreateNative;
    public static Action<SqliteConnection>? InitializeVectors { get; set; }

    public static IAsyncSqliteConnection CreateConnection(string connectionString, bool vectors = false) =>
        ConnectionFactory(connectionString, vectors);

    private static IAsyncSqliteConnection CreateNative(string connectionString, bool vectors) =>
        new NativeSqliteConnection(connectionString, connection =>
        {
            MediaLibraryDatabase.ConfigureConnection(connection);
            if (vectors)
                (InitializeVectors ?? throw new InvalidOperationException("The SQLite vector extension is not registered."))(connection);
        });
}
