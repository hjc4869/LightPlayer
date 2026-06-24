using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Reads and writes the extended M3U playlist format (<c>.m3u</c> / <c>.m3u8</c>).
/// Cross-platform: paths are resolved against the playlist file's own directory
/// so relative entries survive being moved with their media. Replaces the WinRT
/// <c>Windows.Media.Playlists</c> import/export path.
/// </summary>
public static class M3uPlaylistFormat
{
    private const string ExtInfPrefix = "#EXTINF:";

    /// <summary>Parses a playlist file into entries. Unknown lines are skipped.</summary>
    public static IReadOnlyList<PlaylistItemModel> Parse(string filePath)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var items = new List<PlaylistItemModel>();

        string? pendingArtist = null;
        string? pendingTitle = null;
        var pendingDuration = TimeSpan.Zero;

        foreach (var rawLine in File.ReadLines(filePath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(ExtInfPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ParseExtInf(line, out pendingArtist, out pendingTitle, out pendingDuration);
                continue;
            }

            if (line.StartsWith('#'))
            {
                // #EXTM3U and any other directive/comment.
                continue;
            }

            var resolvedPath = ResolvePath(baseDirectory, line);
            var title = string.IsNullOrWhiteSpace(pendingTitle)
                ? Path.GetFileNameWithoutExtension(resolvedPath)
                : pendingTitle!;

            items.Add(new PlaylistItemModel(
                title,
                pendingArtist ?? string.Empty,
                string.Empty,
                resolvedPath,
                pendingDuration));

            pendingArtist = null;
            pendingTitle = null;
            pendingDuration = TimeSpan.Zero;
        }

        return items;
    }

    /// <summary>Writes the entries as an extended M3U8 file.</summary>
    public static void Write(string filePath, IReadOnlyList<PlaylistItemModel> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");

        foreach (var item in items)
        {
            var seconds = item.Duration > TimeSpan.Zero
                ? (int)Math.Round(item.Duration.TotalSeconds)
                : -1;
            var label = string.IsNullOrWhiteSpace(item.Artist)
                ? item.Title
                : $"{item.Artist} - {item.Title}";

            builder.Append(ExtInfPrefix)
                   .Append(seconds.ToString(CultureInfo.InvariantCulture))
                   .Append(',')
                   .AppendLine(label);
            builder.AppendLine(item.FilePath);
        }

        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ParseExtInf(string line, out string? artist, out string? title, out TimeSpan duration)
    {
        artist = null;
        title = null;
        duration = TimeSpan.Zero;

        var payload = line[ExtInfPrefix.Length..];
        var commaIndex = payload.IndexOf(',');
        if (commaIndex < 0)
        {
            return;
        }

        var durationText = payload[..commaIndex].Trim();
        if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        var display = payload[(commaIndex + 1)..].Trim();
        var separatorIndex = display.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            artist = display[..separatorIndex].Trim();
            title = display[(separatorIndex + 3)..].Trim();
        }
        else
        {
            title = display;
        }
    }

    private static string ResolvePath(string baseDirectory, string entry)
    {
        if (Path.IsPathRooted(entry) || baseDirectory.Length == 0)
        {
            return entry;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, entry));
        }
        catch (ArgumentException)
        {
            return entry;
        }
    }
}
