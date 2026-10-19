#nullable enable

using System;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Reads navigation metadata that FFmpeg's Blu-ray byte-stream protocol does not expose.</summary>
public static unsafe class BlurayChapterReader
{
    private const byte RelevantTitles = 0x03;
    private const uint MinimumTitleLengthSeconds = 180;
    private const uint MaximumPlaylist = 99_999;

    private static readonly delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr> open;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, void> close;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, byte, uint, uint> getTitles;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint, BlurayTitleInfo*> getTitleInfo;
    private static readonly delegate* unmanaged[Cdecl]<BlurayTitleInfo*, void> freeTitleInfo;
    private static readonly int chapterSize;

    static BlurayChapterReader()
    {
        if (!TryLoad(out IntPtr library))
            return;

        if (!NativeLibrary.TryGetExport(library, "bd_open", out IntPtr openAddress) ||
            !NativeLibrary.TryGetExport(library, "bd_close", out IntPtr closeAddress) ||
            !NativeLibrary.TryGetExport(library, "bd_get_titles", out IntPtr getTitlesAddress) ||
            !NativeLibrary.TryGetExport(library, "bd_get_title_info", out IntPtr getTitleInfoAddress) ||
            !NativeLibrary.TryGetExport(library, "bd_free_title_info", out IntPtr freeTitleInfoAddress) ||
            !NativeLibrary.TryGetExport(library, "bd_get_version", out IntPtr getVersionAddress))
        {
            NativeLibrary.Free(library);
            return;
        }

        open = (delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr>)openAddress;
        close = (delegate* unmanaged[Cdecl]<IntPtr, void>)closeAddress;
        getTitles = (delegate* unmanaged[Cdecl]<IntPtr, byte, uint, uint>)getTitlesAddress;
        getTitleInfo = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, BlurayTitleInfo*>)getTitleInfoAddress;
        freeTitleInfo = (delegate* unmanaged[Cdecl]<BlurayTitleInfo*, void>)freeTitleInfoAddress;

        int major = 0;
        int minor = 0;
        int micro = 0;
        ((delegate* unmanaged[Cdecl]<int*, int*, int*, void>)getVersionAddress)(&major, &minor, &micro);
        chapterSize = major > 1 || (major == 1 && minor >= 5)
            ? sizeof(NamedBlurayTitleChapter)
            : sizeof(BlurayTitleChapter);
        IsAvailable = true;
    }

    private static bool IsAvailable { get; }

    public static bool TryRead(string path, out int playlist, out MediaChapter[] chapters)
    {
        if (TryReadNative(path, out playlist, out chapters))
            return true;

        return BlurayPlaylistReader.TryRead(path, out playlist, out chapters);
    }

    private static bool TryReadNative(string path, out int playlist, out MediaChapter[] chapters)
    {
        playlist = 0;
        chapters = [];
        if (!IsAvailable)
            return false;

        IntPtr pathBuffer = Marshal.StringToCoTaskMemUTF8(path);
        IntPtr disc;
        try
        {
            disc = open((byte*)pathBuffer, null);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathBuffer);
        }

        if (disc == IntPtr.Zero)
            return false;

        try
        {
            uint titleCount = getTitles(disc, RelevantTitles, MinimumTitleLengthSeconds);
            ulong longestDuration = 0;
            uint longestTitle = 0;
            bool found = false;
            for (uint title = 0; title < titleCount; title++)
            {
                BlurayTitleInfo* info = getTitleInfo(disc, title, 0);
                if (info == null)
                    continue;

                try
                {
                    if (info->Duration > longestDuration)
                    {
                        longestDuration = info->Duration;
                        longestTitle = title;
                        found = true;
                    }
                }
                finally
                {
                    freeTitleInfo(info);
                }
            }

            if (!found)
                return false;

            BlurayTitleInfo* selected = getTitleInfo(disc, longestTitle, 0);
            if (selected == null)
                return false;

            try
            {
                if (selected->Playlist > MaximumPlaylist)
                    return false;

                playlist = (int)selected->Playlist;
                chapters = ReadChapters(selected);
                return true;
            }
            finally
            {
                freeTitleInfo(selected);
            }
        }
        finally
        {
            close(disc);
        }
    }

    private static MediaChapter[] ReadChapters(BlurayTitleInfo* title)
    {
        if (title->Chapters == IntPtr.Zero || title->ChapterCount == 0 || title->ChapterCount > int.MaxValue)
            return [];

        int count = (int)title->ChapterCount;
        var chapters = new MediaChapter[count];
        for (int index = 0; index < count; index++)
        {
            var chapter = (BlurayTitleChapter*)((byte*)title->Chapters + (index * chapterSize));
            TimeSpan start = From90Khz(chapter->Start);
            TimeSpan duration = From90Khz(chapter->Duration);
            TimeSpan end = duration > TimeSpan.MaxValue - start ? TimeSpan.MaxValue : start + duration;
            string chapterName = chapterSize == sizeof(NamedBlurayTitleChapter)
                ? Marshal.PtrToStringUTF8(((NamedBlurayTitleChapter*)chapter)->Name) ?? string.Empty
                : string.Empty;
            chapters[index] = new MediaChapter(index, start, end, chapterName);
        }

        return chapters;
    }

    private static TimeSpan From90Khz(ulong value)
    {
        const ulong unitsPerSecond = 90_000;
        ulong seconds = value / unitsPerSecond;
        if (seconds > (ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond))
            return TimeSpan.MaxValue;

        ulong remainder = value % unitsPerSecond;
        long wholeTicks = (long)seconds * TimeSpan.TicksPerSecond;
        ulong remainderTicks = remainder * TimeSpan.TicksPerSecond / unitsPerSecond;
        return remainderTicks > (ulong)(TimeSpan.MaxValue.Ticks - wholeTicks)
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(wholeTicks + (long)remainderTicks);
    }

    private static bool TryLoad(out IntPtr library)
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["libbluray-2.dll", "libbluray.dll", "bluray.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libbluray.2.dylib", "libbluray.dylib"]
                : ["libbluray.so.2", "libbluray.so"];

        foreach (string name in names)
        {
            if (NativeLibrary.TryLoad(name, out library))
                return true;
        }

        library = IntPtr.Zero;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurayTitleInfo
    {
        public uint Index;
        public uint Playlist;
        public ulong Duration;
        public uint ClipCount;
        public byte AngleCount;
        public uint ChapterCount;
        public uint MarkCount;
        public IntPtr Clips;
        public IntPtr Chapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurayTitleChapter
    {
        public uint Index;
        public ulong Start;
        public ulong Duration;
        public ulong Offset;
        public uint ClipReference;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NamedBlurayTitleChapter
    {
        public BlurayTitleChapter Chapter;
        public IntPtr Name;
    }
}