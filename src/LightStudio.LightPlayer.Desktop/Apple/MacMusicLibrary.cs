using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>Reads the Music app library through iTunesLibrary.framework for <see cref="MacMediaLibraryProvider"/>.</summary>
internal static unsafe class MacMusicLibrary
{
    private const string Framework = "/System/Library/Frameworks/iTunesLibrary.framework/iTunesLibrary";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const nint SongKind = 2;
    private const nint FileLocation = 1;
    private const uint DatalessFlag = 0x40000000;
    private const int StatSize = 144;
    private const int StatFlagsOffset = 116;

    private static readonly object OpenGate = new();
    private static readonly object ReadGate = new();
    private static readonly Lazy<IntPtr> Lstat = new(() => NativeLibrary.GetExport(NativeLibrary.Load(LibSystem),
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "lstat$INODE64" : "lstat"));
    private static IntPtr library;
    private static bool reload;

    public static bool IsOpen => Volatile.Read(ref library) != IntPtr.Zero;

    /// <summary>Opens the library, which asks for media library access the first time.</summary>
    public static bool Open()
    {
        lock (OpenGate)
        {
            if (library != IntPtr.Zero)
                return true;
            ObjC.LoadLibrary(Framework);
            var pool = ObjC.PushAutoreleasePool();
            var version = ObjC.CreateString("1.0");
            try
            {
                IntPtr error = IntPtr.Zero;
                var opened = ObjC.Send(ObjC.GetClass("ITLibrary"), ObjC.Sel("libraryWithAPIVersion:error:"), version, (IntPtr)(&error));
                if (opened == IntPtr.Zero)
                {
                    AppLog.Warning("macOS.MediaLibrary", "Could not open the Music library: " +
                        (ObjC.ReadString(ObjC.Send(error, ObjC.Sel("localizedDescription"))) ?? "unknown error"));
                    return false;
                }

                Volatile.Write(ref library, ObjC.Send(opened, ObjC.Sel("retain")));
                return true;
            }
            finally
            {
                ObjC.CFRelease(version);
                ObjC.PopAutoreleasePool(pool);
            }
        }
    }

    /// <summary>Makes the next read reload the library from the Music app.</summary>
    public static void Invalidate() => Volatile.Write(ref reload, true);

    /// <summary>Reads local, unprotected songs; empty until <see cref="Open"/> succeeds.</summary>
    public static SystemLibraryTrack[] Read(CancellationToken cancellationToken)
    {
        if (!IsOpen)
            return [];
        lock (ReadGate)
        {
            var pool = ObjC.PushAutoreleasePool();
            try
            {
                if (reload)
                {
                    reload = false;
                    ObjC.SendBool(library, ObjC.Sel("reloadData"));
                }

                var mediaFolder = ObjC.ReadString(ObjC.Send(ObjC.Send(library, ObjC.Sel("mediaFolderLocation")), ObjC.Sel("path")));
                var items = ObjC.Send(library, ObjC.Sel("allMediaItems"));
                var count = (long)ObjC.Send(items, ObjC.Sel("count"));
                var tracks = new List<SystemLibraryTrack>();
                var skipped = 0;
                for (long index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var itemPool = ObjC.PushAutoreleasePool();
                    try
                    {
                        var item = ObjC.Send(items, ObjC.Sel("objectAtIndex:"), (IntPtr)index);
                        if (ObjC.Send(item, ObjC.Sel("mediaKind")) != SongKind)
                            continue;
                        var path = ObjC.Send(item, ObjC.Sel("locationType")) == FileLocation &&
                            ObjC.SendBool(item, ObjC.Sel("isDRMProtected")) == ObjC.No
                                ? ObjC.ReadString(ObjC.Send(ObjC.Send(item, ObjC.Sel("location")), ObjC.Sel("path")))
                                : null;
                        if (path is null || !IsLocalFile(path))
                        {
                            skipped++;
                            continue;
                        }

                        tracks.Add(ReadTrack(item, path, tracks.Count + 1, mediaFolder));
                    }
                    finally
                    {
                        ObjC.PopAutoreleasePool(itemPool);
                    }
                }

                if (skipped > 0)
                    AppLog.Information("macOS.MediaLibrary", $"Skipped {skipped} songs that are protected or not stored on this Mac.");
                return tracks.ToArray();
            }
            finally
            {
                ObjC.PopAutoreleasePool(pool);
            }
        }
    }

    private static SystemLibraryTrack ReadTrack(IntPtr item, string path, int id, string? mediaFolder)
    {
        var album = ObjC.Send(item, ObjC.Sel("album"));
        var artist = Text(ObjC.Send(ObjC.Send(item, ObjC.Sel("artist")), ObjC.Sel("name")));
        var albumArtist = Text(ObjC.Send(album, ObjC.Sel("albumArtist")));
        var albumId = (ulong)ObjC.Send(ObjC.Send(album, ObjC.Sel("persistentID")), ObjC.Sel("unsignedLongLongValue"));
        var added = Date(ObjC.Send(item, ObjC.Sel("addedDate")));
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        return new SystemLibraryTrack(new DbMediaFile
        {
            Id = id,
            Path = path,
            Title = Text(ObjC.Send(item, ObjC.Sel("title"))) is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(path),
            Artist = artist,
            Album = Text(ObjC.Send(album, ObjC.Sel("title"))),
            AlbumArtist = albumArtist.Length > 0 ? albumArtist : artist,
            AlbumGroupingKey = "itunes:" + albumId.ToString(CultureInfo.InvariantCulture),
            Genre = Text(ObjC.Send(item, ObjC.Sel("genre"))),
            Composer = Text(ObjC.Send(item, ObjC.Sel("composer"))),
            Date = Positive(ObjC.Send(item, ObjC.Sel("year"))),
            DiscNumber = Positive(ObjC.Send(album, ObjC.Sel("discNumber"))),
            TrackNumber = Positive(ObjC.Send(item, ObjC.Sel("trackNumber"))),
            TotalDiscs = Positive(ObjC.Send(album, ObjC.Sel("discCount"))),
            TotalTracks = Positive(ObjC.Send(album, ObjC.Sel("trackCount"))),
            Description = string.Empty,
            Copyright = string.Empty,
            Comments = Text(ObjC.Send(item, ObjC.Sel("comments"))),
            Grouping = Text(ObjC.Send(item, ObjC.Sel("grouping"))),
            Performer = string.Empty,
            Duration = TimeSpan.FromMilliseconds((long)ObjC.Send(item, ObjC.Sel("totalTime"))),
            DatabaseItemAddedDate = added,
            FileLastModifiedDate = ObjC.Send(item, ObjC.Sel("modifiedDate")) is var modified && modified != IntPtr.Zero
                ? Date(modified) : added,
        }, folder, Root(folder, mediaFolder));
    }

    // Songs under the Music media folder are rooted at its top-level folders; others at their own folder.
    private static string Root(string folder, string? mediaFolder)
    {
        if (string.IsNullOrEmpty(mediaFolder))
            return folder;
        var relative = Path.GetRelativePath(mediaFolder, folder);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return folder;
        return Path.Combine(mediaFolder, relative.Split(Path.DirectorySeparatorChar)[0]);
    }

    // Files evicted by iCloud Drive and other file providers keep their entries but have no local data.
    private static bool IsLocalFile(string path)
    {
        var buffer = stackalloc byte[StatSize];
        var name = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var lstat = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Lstat.Value;
            return lstat(name, buffer) == 0 && (*(uint*)(buffer + StatFlagsOffset) & DatalessFlag) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static string Text(IntPtr value) => ObjC.ReadString(value) ?? string.Empty;

    private static string Positive(nint value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static DateTimeOffset Date(IntPtr date) => date == IntPtr.Zero
        ? DateTimeOffset.UnixEpoch
        : DateTimeOffset.FromUnixTimeMilliseconds((long)(ObjC.SendDouble(date, ObjC.Sel("timeIntervalSince1970")) * 1000));
}
