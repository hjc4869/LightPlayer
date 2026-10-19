#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Reads chapter marks directly from Blu-ray folder playlists.</summary>
internal static class BlurayPlaylistReader
{
    private const byte EntryMark = 0x01;
    private const ulong ClockRate = 45_000;
    private const ulong MinimumTitleDuration = 180 * ClockRate;
    private const int MaximumPlaylistBytes = 16 * 1024 * 1024;
    private const int MarkSize = 14;

    public static bool TryRead(string path, out int playlist, out MediaChapter[] chapters)
    {
        playlist = 0;
        chapters = [];

        try
        {
            string? playlistDirectory = FindPlaylistDirectory(path);
            if (playlistDirectory is null)
                return false;

            Playlist? selected = null;
            foreach (string filePath in Directory.EnumerateFiles(playlistDirectory))
            {
                if (!TryGetPlaylistNumber(filePath, out int candidateNumber) ||
                    !TryParse(filePath, candidateNumber, out Playlist? candidate) ||
                    candidate is null ||
                    candidate.Duration < MinimumTitleDuration ||
                    HasRepeatedSegment(candidate.PlayItems))
                {
                    continue;
                }

                if (selected is null || candidate.Duration > selected.Duration)
                    selected = candidate;
            }

            if (selected is null)
                return false;

            playlist = selected.Number;
            chapters = CreateChapters(selected);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Video.Disc", $"Could not read Blu-ray playlists under '{path}'.", exception);
            return false;
        }
    }

    private static string? FindPlaylistDirectory(string path)
    {
        if (!Directory.Exists(path))
            return null;

        string discName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string? bdmv = string.Equals(discName, "BDMV", StringComparison.OrdinalIgnoreCase)
            ? path
            : FindDirectory(path, "BDMV");
        return bdmv is null ? null : FindDirectory(bdmv, "PLAYLIST");
    }

    private static string? FindDirectory(string parent, string name)
    {
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(Path.GetFileName(directory), name, StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    private static bool TryGetPlaylistNumber(string path, out int playlist)
    {
        playlist = 0;
        string fileName = Path.GetFileName(path);
        return fileName.Length == 10 &&
            fileName.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fileName.AsSpan(0, 5), NumberStyles.None, CultureInfo.InvariantCulture, out playlist);
    }

    private static bool TryParse(string path, int number, out Playlist? playlist)
    {
        playlist = null;
        using FileStream stream = File.OpenRead(path);
        if (stream.Length < 20 || stream.Length > MaximumPlaylistBytes)
            return false;

        byte[] data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        ReadOnlySpan<byte> content = data;
        if (!content[..4].SequenceEqual("MPLS"u8))
            return false;

        uint playlistOffset = BinaryPrimitives.ReadUInt32BigEndian(content[8..]);
        uint markOffset = BinaryPrimitives.ReadUInt32BigEndian(content[12..]);
        if (!TryReadPlayItems(content, playlistOffset, out PlayItem[] playItems, out ulong duration) ||
            !TryReadMarks(content, markOffset, out PlaylistMark[] marks))
        {
            return false;
        }

        playlist = new Playlist(number, duration, playItems, marks);
        return true;
    }

    private static bool TryReadPlayItems(
        ReadOnlySpan<byte> content,
        uint sectionOffset,
        out PlayItem[] playItems,
        out ulong duration)
    {
        playItems = [];
        duration = 0;
        if (!TryGetSection(content, sectionOffset, 6, out ReadOnlySpan<byte> section))
            return false;

        int count = BinaryPrimitives.ReadUInt16BigEndian(section[2..]);
        var items = new PlayItem[count];
        int cursor = 6;
        for (int index = 0; index < count; index++)
        {
            if (cursor > section.Length - sizeof(ushort))
                return false;

            int itemLength = BinaryPrimitives.ReadUInt16BigEndian(section[cursor..]);
            cursor += sizeof(ushort);
            if (itemLength < 20 || itemLength > section.Length - cursor)
                return false;

            ReadOnlySpan<byte> item = section.Slice(cursor, itemLength);
            uint clipId = BinaryPrimitives.ReadUInt32BigEndian(item);
            byte clipIdTail = item[4];
            uint inTime = BinaryPrimitives.ReadUInt32BigEndian(item[12..]);
            uint outTime = BinaryPrimitives.ReadUInt32BigEndian(item[16..]);
            if (outTime < inTime)
                return false;

            items[index] = new PlayItem(clipId, clipIdTail, inTime, outTime, duration);
            duration += outTime - (ulong)inTime;
            cursor += itemLength;
        }

        playItems = items;
        return true;
    }

    private static bool TryReadMarks(
        ReadOnlySpan<byte> content,
        uint sectionOffset,
        out PlaylistMark[] marks)
    {
        marks = [];
        if (!TryGetSection(content, sectionOffset, sizeof(ushort), out ReadOnlySpan<byte> section))
            return false;

        int count = BinaryPrimitives.ReadUInt16BigEndian(section);
        if (count > (section.Length - sizeof(ushort)) / MarkSize)
            return false;

        var parsed = new PlaylistMark[count];
        int cursor = sizeof(ushort);
        for (int index = 0; index < count; index++, cursor += MarkSize)
        {
            ReadOnlySpan<byte> mark = section.Slice(cursor, MarkSize);
            parsed[index] = new PlaylistMark(
                mark[1],
                BinaryPrimitives.ReadUInt16BigEndian(mark[2..]),
                BinaryPrimitives.ReadUInt32BigEndian(mark[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(mark[10..]));
        }

        marks = parsed;
        return true;
    }

    private static bool TryGetSection(
        ReadOnlySpan<byte> content,
        uint offset,
        int minimumLength,
        out ReadOnlySpan<byte> section)
    {
        section = default;
        if (offset > int.MaxValue || (int)offset > content.Length - sizeof(uint))
            return false;

        int start = (int)offset;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(content[start..]);
        start += sizeof(uint);
        if (length > int.MaxValue || (int)length < minimumLength || (int)length > content.Length - start)
            return false;

        section = content.Slice(start, (int)length);
        return true;
    }

    private static bool HasRepeatedSegment(IReadOnlyList<PlayItem> playItems)
    {
        var occurrences = new Dictionary<ClipSegment, int>();
        foreach (PlayItem item in playItems)
        {
            var segment = new ClipSegment(item.ClipId, item.ClipIdTail, item.InTime, item.OutTime);
            occurrences.TryGetValue(segment, out int count);
            if (count >= 2)
                return true;
            occurrences[segment] = count + 1;
        }

        return false;
    }

    private static MediaChapter[] CreateChapters(Playlist playlist)
    {
        var chapters = new List<Chapter>();
        foreach (PlaylistMark mark in playlist.Marks)
        {
            if (mark.Type != EntryMark || mark.PlayItem >= playlist.PlayItems.Length)
                continue;

            PlayItem item = playlist.PlayItems[mark.PlayItem];
            if (mark.Time < item.InTime || mark.Time > item.OutTime)
                continue;

            ulong start = item.TitleStart + mark.Time - item.InTime;
            chapters.Add(new Chapter(start, mark.Duration));
        }

        for (int index = 0; index < chapters.Count; index++)
        {
            Chapter chapter = chapters[index];
            if (chapter.Duration == 0)
            {
                ulong end = index + 1 < chapters.Count ? chapters[index + 1].Start : playlist.Duration;
                chapter = chapter with { Duration = end > chapter.Start ? end - chapter.Start : 0 };
                chapters[index] = chapter;
            }
        }

        var result = new MediaChapter[chapters.Count];
        for (int index = 0; index < result.Length; index++)
        {
            TimeSpan start = From45Khz(chapters[index].Start);
            TimeSpan duration = From45Khz(chapters[index].Duration);
            TimeSpan end = duration > TimeSpan.MaxValue - start ? TimeSpan.MaxValue : start + duration;
            result[index] = new MediaChapter(index, start, end, string.Empty);
        }

        return result;
    }

    private static TimeSpan From45Khz(ulong value)
    {
        ulong seconds = value / ClockRate;
        if (seconds > (ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond))
            return TimeSpan.MaxValue;

        ulong remainder = value % ClockRate;
        long wholeTicks = (long)seconds * TimeSpan.TicksPerSecond;
        ulong remainderTicks = remainder * TimeSpan.TicksPerSecond / ClockRate;
        return remainderTicks > (ulong)(TimeSpan.MaxValue.Ticks - wholeTicks)
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(wholeTicks + (long)remainderTicks);
    }

    private sealed record Playlist(
        int Number,
        ulong Duration,
        PlayItem[] PlayItems,
        PlaylistMark[] Marks);

    private readonly record struct PlayItem(
        uint ClipId,
        byte ClipIdTail,
        uint InTime,
        uint OutTime,
        ulong TitleStart);

    private readonly record struct PlaylistMark(byte Type, ushort PlayItem, uint Time, uint Duration);

    private readonly record struct ClipSegment(uint ClipId, byte ClipIdTail, uint InTime, uint OutTime);

    private readonly record struct Chapter(ulong Start, ulong Duration);
}