using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.CueIndex;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Expands externally opened files (file picker, command-line activation, drag-and-drop) into
/// the concrete queue items to play. A <c>.cue</c> sheet, or an audio file that embeds one, is
/// split into one item per track carrying the track's start offset, length, and tags so the
/// playback engine decodes only that segment. Plain audio files pass through as a single item.
/// </summary>
internal static class CueFileExpander
{
    private const string CueExtension = ".cue";
    private const string CueSheetProperty = "cuesheet";

    /// <summary>
    /// Expands <paramref name="paths"/> into queue items. Cue sheets are resolved first so a
    /// companion audio file opened in the same batch is consumed by its sheet instead of being
    /// queued twice. Failures for a single media file fall back to a placeholder so the rest of the
    /// selection still queues; cue sheets that cannot be resolved to a playable audio file are
    /// reported through <see cref="CueExpansionResult.Failures"/> rather than dropped silently.
    /// </summary>
    public static async Task<CueExpansionResult> ExpandAsync(IReadOnlyList<string> paths)
    {
        var items = new List<MusicPlaybackItemModel>();
        var failures = new List<CueResolutionFailure>();

        // Split the selection so each cue sheet can claim its companion audio file (below) before
        // the remaining media files are queued on their own.
        var cuePaths = new List<string>();
        var mediaPaths = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            (IsCueSheet(path) ? cuePaths : mediaPaths).Add(path);
        }

        foreach (var cuePath in cuePaths)
        {
            try
            {
                var (cueItems, failure) = await ExpandCueFileAsync(cuePath, mediaPaths).ConfigureAwait(false);
                if (failure is not null)
                {
                    failures.Add(failure);
                }

                items.AddRange(cueItems);
            }
            catch
            {
                // Corrupt sheet or an I/O error reading it: surface it instead of failing silently.
                failures.Add(new CueResolutionFailure(cuePath, string.Empty));
            }
        }

        foreach (var mediaPath in mediaPaths)
        {
            try
            {
                items.AddRange(await ExpandMediaFileAsync(mediaPath).ConfigureAwait(false));
            }
            catch
            {
                // Corrupt file or decoder failure: keep the selection usable by queuing the file
                // under its name rather than dropping it silently.
                items.Add(Placeholder(mediaPath));
            }
        }

        return new CueExpansionResult(items, failures);
    }

    private static bool IsCueSheet(string path) =>
        string.Equals(Path.GetExtension(path), CueExtension, StringComparison.OrdinalIgnoreCase);

    private static MusicPlaybackItemModel Placeholder(string path) =>
        new(Path.GetFileNameWithoutExtension(path), string.Empty, string.Empty, TimeSpan.Zero, path);

    private static async Task<IReadOnlyList<MusicPlaybackItemModel>> ExpandMediaFileAsync(string audioPath)
    {
        var info = await IMultiMediaPlatformIO.Instance.GetMediaInfoAsync(audioPath).ConfigureAwait(false);
        if (info is null)
        {
            return new[] { Placeholder(audioPath) };
        }

        // A single audio file can embed a full-album cue sheet in its metadata.
        if (info.AllProperties is not null &&
            info.AllProperties.TryGetValue(CueSheetProperty, out var embedded) &&
            !string.IsNullOrWhiteSpace(embedded))
        {
            var embeddedCue = CueFile.CreateFromString(embedded);
            if (embeddedCue.Indices.Count > 0)
            {
                return BuildCueItems(audioPath, embeddedCue, info.Duration);
            }
        }

        var title = string.IsNullOrWhiteSpace(info.Title)
            ? Path.GetFileNameWithoutExtension(audioPath)
            : info.Title.Trim();
        return new[]
        {
            new MusicPlaybackItemModel(title, info.Artist?.Trim() ?? string.Empty, info.Album?.Trim() ?? string.Empty, info.Duration, audioPath),
        };
    }

    /// <summary>
    /// Resolves a standalone <c>.cue</c> sheet to its tracks. The referenced audio file is looked up
    /// in <paramref name="batchMediaPaths"/> first — and removed from it when found so it is not also
    /// queued on its own — before falling back to the file system. Returns a
    /// <see cref="CueResolutionFailure"/> (and no items) when the audio file cannot be found or opened.
    /// </summary>
    private static async Task<(IReadOnlyList<MusicPlaybackItemModel> Items, CueResolutionFailure? Failure)> ExpandCueFileAsync(
        string cueFilePath,
        List<string> batchMediaPaths)
    {
        CueFile cue;
        await using (var stream = await IMultiMediaPlatformIO.Instance.OpenMediaFileAsync(cueFilePath).ConfigureAwait(false))
        {
            cue = await CueFile.CreateFromStreamAsync(stream).ConfigureAwait(false);
        }

        if (cue.Indices.Count == 0 || string.IsNullOrWhiteSpace(cue.FileName))
        {
            return (Array.Empty<MusicPlaybackItemModel>(), new CueResolutionFailure(cueFilePath, cue.FileName ?? string.Empty));
        }

        // Prefer a companion audio file the user opened alongside the sheet. On sandboxed platforms
        // (UWP/Flatpak) the referenced file is frequently unreachable through the file system
        // — only files opened explicitly are granted access — so opening the sheet and its audio
        // file together is the supported way around that limitation.
        var audioPath = TakeMatchingBatchFile(batchMediaPaths, cue.FileName)
            ?? ResolveReferencedAudioFile(cueFilePath, cue.FileName);
        if (audioPath is null)
        {
            // The audio file the sheet points at is missing or outside the sandbox.
            return (Array.Empty<MusicPlaybackItemModel>(), new CueResolutionFailure(cueFilePath, cue.FileName));
        }

        var info = await IMultiMediaPlatformIO.Instance.GetMediaInfoAsync(audioPath).ConfigureAwait(false);
        return (BuildCueItems(audioPath, cue, info?.Duration ?? TimeSpan.Zero), null);
    }

    /// <summary>
    /// Removes and returns the batch file whose name matches the sheet's referenced audio file, if
    /// the user opened it in the same selection. Matching by file name (case-insensitive) and lets
    /// a sandboxed app use a companion file it could not otherwise reach through the file system.
    /// </summary>
    private static string? TakeMatchingBatchFile(List<string> batchMediaPaths, string referencedName)
    {
        var name = Path.GetFileName(referencedName);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        for (var i = 0; i < batchMediaPaths.Count; i++)
        {
            if (string.Equals(Path.GetFileName(batchMediaPaths[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                var match = batchMediaPaths[i];
                batchMediaPaths.RemoveAt(i);
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the audio file a cue sheet references, relative to the sheet's own directory.
    /// Falls back to a case-insensitive file-name match so sheets authored on a case-insensitive
    /// file system still resolve on Linux.
    /// </summary>
    private static string? ResolveReferencedAudioFile(string cueFilePath, string referencedName)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cueFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, referencedName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        try
        {
            var name = Path.GetFileName(referencedName);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory not enumerable: treat the audio file as missing.
        }

        return null;
    }

    private static IReadOnlyList<MusicPlaybackItemModel> BuildCueItems(string audioPath, CueFile cue, TimeSpan totalDuration)
    {
        var items = new List<MusicPlaybackItemModel>(cue.Indices.Count);
        foreach (var track in cue.Indices.OrderBy(t => ParseTrackNumber(t.TrackInfo?.TrackNumber)))
        {
            // Skip tracks whose start lands past the end of the backing file (misaligned sheet).
            if (totalDuration > TimeSpan.Zero && track.StartTime >= totalDuration)
            {
                continue;
            }

            var duration = track.Duration;
            if (duration <= TimeSpan.Zero && totalDuration > track.StartTime)
            {
                // The final track's length is left open in most sheets: run it to the file end.
                duration = totalDuration - track.StartTime;
            }

            var info = track.TrackInfo;
            var title = string.IsNullOrWhiteSpace(info?.Title)
                ? Path.GetFileNameWithoutExtension(audioPath)
                : info!.Title.Trim();
            var artist = info?.Artist?.Trim() ?? string.Empty;
            var album = info?.Album?.Trim() ?? string.Empty;

            items.Add(new MusicPlaybackItemModel(
                title,
                artist,
                album,
                duration,
                audioPath,
                (int)track.StartTime.TotalMilliseconds));
        }

        return items;
    }

    private static int ParseTrackNumber(string? value) =>
        int.TryParse(value, out var number) ? number : int.MaxValue;
}

/// <summary>
/// Outcome of expanding a batch of opened files: the queue items to play and any cue sheets that
/// could not be resolved to a playable audio file.
/// </summary>
/// <param name="Items">Queue items produced from the selection, in play order.</param>
/// <param name="Failures">Cue sheets whose referenced audio file could not be found or opened.</param>
internal sealed record CueExpansionResult(
    IReadOnlyList<MusicPlaybackItemModel> Items,
    IReadOnlyList<CueResolutionFailure> Failures);

/// <summary>
/// A cue sheet that could not be resolved to a playable audio file.
/// </summary>
/// <param name="CueFilePath">Absolute path of the <c>.cue</c> sheet.</param>
/// <param name="ReferencedFileName">The audio file name the sheet references, or empty when unknown.</param>
internal sealed record CueResolutionFailure(string CueFilePath, string ReferencedFileName);
