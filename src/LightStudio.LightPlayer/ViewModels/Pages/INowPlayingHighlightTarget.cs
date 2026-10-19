using System;
using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Implemented by pages that show <see cref="TrackRowModel"/> rows and want the row
/// backing the currently playing track visually highlighted. The shell pushes the
/// current track identity whenever playback changes or the page becomes active, and
/// each page re-applies it after it (re)builds its rows.
/// </summary>
public interface INowPlayingHighlightTarget
{
    /// <summary>
    /// Highlights the row whose file (and CUE start offset) matches the currently
    /// playing track and clears the highlight from all others. A null
    /// <paramref name="filePath"/> (nothing playing) clears every row.
    /// </summary>
    void SetNowPlaying(MediaItemKey? mediaItem);
}

/// <summary>
/// Shared now-playing highlight matching used by every track list. Matches on file
/// path plus CUE start offset so distinct CUE tracks of one audio file are told apart
/// (they share a path but differ in <see cref="TrackRowModel.StartTimeMs"/>).
/// </summary>
internal static class NowPlayingHighlight
{
    public static void Apply(IEnumerable<TrackRowModel> rows, MediaItemKey? mediaItem)
    {
        foreach (var row in rows)
        {
            row.IsPlaying = mediaItem is { } playing
                && row.SourceId == playing.Media.SourceId
                && string.Equals(
                    row.FilePath,
                    playing.Media.Path,
                    PathComparison(row.SourceId))
                && row.StartTimeMs == playing.StartTimeMs;
        }
    }

    // Windows and macOS file systems are case-insensitive; Linux is case-sensitive.
    private static StringComparison PathComparison(LibrarySourceId sourceId) =>
        sourceId == LibrarySourceId.Local && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
