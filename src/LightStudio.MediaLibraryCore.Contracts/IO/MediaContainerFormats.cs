#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>
/// Container extensions shared by the library scanner, the remote client, and drag and drop, so the
/// three agree on what is worth handing to FFmpeg. An extension never decides how a file opens:
/// that is settled by what a probe finds inside it.
/// </summary>
public static class MediaContainerFormats
{
    /// <summary>
    /// Containers that can hold video. Membership says nothing about a particular file: whether it
    /// really carries moving pictures is settled by <see cref="IMultiMediaPlatformIO.ProbeVideoAsync"/>,
    /// which is what keeps audio-only <c>.mp4</c>/<c>.webm</c> files out.
    /// </summary>
    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".m4v",
        ".mov",
        ".avi",
        ".webm",
        ".ts",
        ".m2ts",
        ".mts",
        ".flv",
        ".wmv",
        ".mpg",
        ".mpeg",
        ".ogv",
        ".rmvb",
        ".rv",
        ".rvx",
        ".3gp",
        ".3g2",
        ".3gp2",
        ".3gpp2",
        ".dv",
        ".fli",
        ".flc",
        ".m1u",
        ".m4u",
        ".mxu",
        ".asf",
        ".asx",
        ".wax",
        ".wmx",
        ".wvx",
    };

    /// <summary>Filesystem images that may contain a DVD or Blu-ray structure.</summary>
    public static readonly IReadOnlySet<string> DiscImage = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".iso",
    };

    /// <summary>
    /// Still pictures the photo viewer opens. These live in the video library rather than a library
    /// of their own, but they are never indexed: a folder lists whatever of these it holds at the
    /// moment it is opened, so nothing here needs a scan to have run first.
    /// </summary>
    /// <remarks>
    /// Decoding depends on the platform's libraries: DNG uses LibRaw on Linux x64 hosts,
    /// JPEG XL needs FFmpeg with <c>libjxl</c>, and AVIF needs an AV1 decoder.
    /// A file that fails to open is reported rather than kept out of the list.
    /// </remarks>
    public static readonly IReadOnlySet<string> Image = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".jpe",
        ".jfif",
        ".png",
        ".webp",

        // HDR-capable, and the reason the photo viewer draws through the video pipeline at all.
        ".avif",
        ".heic",
        ".heif",
        ".hif",
        ".jxl",
        ".jxr",
        ".wdp",
        ".hdp",
        ".dng",

        ".bmp",
        ".gif",
        ".tif",
        ".tiff",
        ".dib",
        ".ppm",
        ".pgm",
        ".pnm",
        ".tga",

        // Scene-referred floating point, which the tone mapper already knows what to do with.
        ".exr",
        ".hdr",
    };

    /// <summary>
    /// Subtitle scripts the player reads by itself rather than handing to FFmpeg.
    /// </summary>
    public static readonly IReadOnlySet<string> SubtitleScript = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt",
        ".ass",
        ".ssa",
    };

    /// <summary>Sidecar audio containers picked up as extra tracks for a video beside them.</summary>
    public static readonly IReadOnlySet<string> ExternalAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mka",
        ".mp3",
        ".flac",
        ".m4a",
        ".aac",
        ".ac3",
        ".eac3",
        ".dts",
        ".opus",
        ".ogg",
        ".wav",
    };

    /// <summary>Sidecar subtitle files picked up as extra tracks for a video beside them.</summary>
    public static readonly IReadOnlySet<string> ExternalSubtitle = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ass",
        ".ssa",
        ".srt",
        ".vtt",
        ".sub",
        ".lrc",
        ".sup",
        ".pgs",
    };

    /// <summary>
    /// Whether a path names a still picture, used where every file cannot be probed: listing a
    /// folder, or deciding whether a drop is worth accepting at all.
    /// </summary>
    public static bool IsImage(string? path) =>
        !string.IsNullOrEmpty(path) && Image.Contains(Path.GetExtension(path));

    /// <summary>Whether a path names a container or disc image worth probing as video.</summary>
    public static bool IsVideo(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (Video.Contains(Path.GetExtension(path)) || DiscImage.Contains(Path.GetExtension(path)));

    /// <summary>
    /// Media type of a still picture, for the platforms that name a file to another application by
    /// type rather than by extension. Falls back to the wildcard, which every chooser accepts.
    /// </summary>
    public static string ImageMediaType(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" or ".dib" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".avif" => "image/avif",
            ".heic" or ".heif" or ".hif" => "image/heif",
            ".jxl" => "image/jxl",
            ".jxr" => "image/jxr",
            ".wdp" or ".hdp" => "image/vnd.ms-photo",
            ".dng" => "image/x-adobe-dng",
            _ => "image/*",
        };

    /// <summary>
    /// The sidecar files of <paramref name="videoPath"/> among <paramref name="siblingPaths"/>:
    /// <c>video-name.&lt;anything&gt;.&lt;ext&gt;</c> and bare <c>video-name.&lt;ext&gt;</c>, which
    /// is the convention for language-tagged extra tracks such as <c>show.mkv</c> and <c>show.ja.ass</c>.
    /// </summary>
    public static IReadOnlyList<string> FindSidecars(
        string videoPath,
        IEnumerable<string> siblingPaths,
        IReadOnlySet<string> extensions)
    {
        var siblings = new List<(string Name, string Path)>();
        foreach (var sibling in siblingPaths)
        {
            siblings.Add((Path.GetFileName(sibling), sibling));
        }

        return FindSidecars(Path.GetFileName(videoPath), siblings, extensions);
    }

    /// <summary>
    /// The same match made on names rather than paths, for platforms whose folder listing gives an
    /// identifier a file name cannot be read back out of: an Android document URI, say.
    /// </summary>
    public static IReadOnlyList<string> FindSidecars(
        string videoFileName,
        IEnumerable<(string Name, string Path)> siblings,
        IReadOnlySet<string> extensions)
    {
        var stem = Path.GetFileNameWithoutExtension(videoFileName);
        if (string.IsNullOrEmpty(stem))
        {
            return [];
        }

        var prefix = stem + ".";
        var matches = new List<string>();
        foreach (var (name, path) in siblings)
        {
            if (string.Equals(name, videoFileName, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !extensions.Contains(Path.GetExtension(name)))
            {
                continue;
            }

            matches.Add(path);
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }
}
