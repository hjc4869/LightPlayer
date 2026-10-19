using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Platform.Storage;

using LightStudio.Logging;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

internal static class LogExportService
{
    private static readonly FilePickerFileType ZipType = new("ZIP archive")
    {
        Patterns = new[] { "*.zip" },
        MimeTypes = new[] { "application/zip" },
    };

    public static async Task ExportAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null || AppLog.CurrentFilePath is null)
        {
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Resources.About_ExportLogs,
            SuggestedFileName = $"lightplayer-logs-{timestamp}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = new[] { ZipType },
        });
        if (file is null)
        {
            return;
        }

        await using Stream destination = await file.OpenWriteAsync();
        if (destination.CanSeek)
        {
            destination.SetLength(0);
        }

        await AppLog.WriteArchiveAsync(destination);
    }
}