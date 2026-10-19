using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LightStudio.LightPlayer.Models;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Keeps a copy of every subtitle file imported off this device, so a video opened again shows
/// the subtitles it was last watched with even when the file the user chose has since moved or was
/// handed over by a picker that gave no path at all (Android, the browser). Each video's copies
/// live in a folder of their own, which is what makes them droppable one video at a time.
/// </summary>
public static class SubtitleCache
{
    private const string DirectoryName = "Subtitles";

    /// <summary>Writes a copy for <paramref name="videoPath"/> and returns where it went.</summary>
    public static string Store(string videoPath, string? fileName, byte[] content)
        => Store(MediaReference.Local(videoPath), fileName, content);

    public static string Store(MediaReference video, string? fileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(video.Path);
        ArgumentNullException.ThrowIfNull(content);

        var folder = FolderFor(video);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ImportName(fileName));
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Whether anything has been imported for this video, and so is there to drop.</summary>
    public static bool HasImports(string? videoPath) =>
        !string.IsNullOrEmpty(videoPath) && HasImports(MediaReference.Local(videoPath));

    public static bool HasImports(MediaReference video) =>
        !string.IsNullOrEmpty(video.Path) && Directory.Exists(FolderFor(video));

    /// <summary>Every copy imported for this video, oldest first (the names carry the moment).</summary>
    public static IReadOnlyList<string> Imports(string? videoPath)
        => string.IsNullOrEmpty(videoPath) ? [] : Imports(MediaReference.Local(videoPath));

    public static IReadOnlyList<string> Imports(MediaReference video)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return [];
        }

        try
        {
            var folder = FolderFor(video);
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder).OrderBy(path => path, StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Drops every copy imported for this video.</summary>
    public static void Clear(string? videoPath)
    {
        if (!string.IsNullOrEmpty(videoPath))
        {
            Clear(MediaReference.Local(videoPath));
        }
    }

    public static void Clear(MediaReference video)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return;
        }

        try
        {
            Directory.Delete(FolderFor(video), recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FolderFor(MediaReference video) =>
        Path.Combine(AppPaths.CacheDirectory, DirectoryName, KeyOf(video));

    /// <summary>The video's identifier is not a file name, so it is kept only as a short digest.</summary>
    private static string KeyOf(MediaReference video) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            video.SourceId == LibrarySourceId.Local ? video.Path : MediaReferenceUri.Format(video))))
            .ToLowerInvariant()[..16];

    /// <summary>
    /// Names a copy after the moment it was imported rather than after the file it came from: the
    /// picked name may be anything at all, and this is what the subtitle picker goes on to show.
    /// </summary>
    private static string ImportName(string? fileName)
    {
        // Kept, so the loader still has an extension to fall back on when the contents are a tie.
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".srt";
        }

        var label = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Resources.VideoPlayer_SubtitleImportedFormat,
            DateTime.Now.ToString("yyyy-MM-dd HH_mm_ss", CultureInfo.InvariantCulture));
        return SafeName(label) + extension;
    }

    /// <summary>Reduces a label to something every file system will take.</summary>
    private static string SafeName(string label)
    {
        var text = new StringBuilder(label.Length);
        foreach (var character in label)
        {
            text.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) < 0 ? character : '_');
        }

        return text.Length == 0 ? "Imported" : text.ToString();
    }
}
