using System;
using System.IO;
using System.Threading.Tasks;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Constant;

namespace LightStudio.LocalServices;

public static class IndexDatabaseReset
{
    private const string PendingFileName = "index-database-reset.pending";

    public static Task QueueAsync(string directory) => Task.Run(() =>
    {
        Directory.CreateDirectory(directory);
        using var marker = new FileStream(Path.Combine(directory, PendingFileName),
            FileMode.Create, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
        AppLog.Information("Library.Reset", "Queued an index database reset for the next startup.");
    });

    /// <summary>Applies a queued reset before the platform loads configuration or opens databases.</summary>
    public static void ApplyPending(string directory)
    {
        var marker = Path.Combine(directory, PendingFileName);
        if (!File.Exists(marker))
            return;

        try
        {
            foreach (var name in new[] { DatabaseConstants.DbFileName, "photo-v1.sqlite" })
            {
                var path = Path.Combine(directory, name);
                foreach (var suffix in new[] { "-wal", "-shm", "-journal", "" })
                    File.Delete(path + suffix);
            }
            File.Delete(Path.Combine(directory, DatabaseConstants.DbConfigName));
            File.Delete(marker);
            AppLog.Information("Library.Reset", "Reset the library and semantic index databases.");
        }
        catch (Exception exception)
        {
            AppLog.Error("Library.Reset", "Could not reset the index databases; the reset remains queued.", exception);
            throw;
        }
    }
}
