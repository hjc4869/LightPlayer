using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

using ExifEntry = LightStudio.FfmpegShim.Media.ExifEntry;
using ExifMetadata = LightStudio.FfmpegShim.Media.ExifMetadata;
using StillImage = LightStudio.FfmpegShim.Media.StillImage;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Builds file facts, photo summaries, camera metadata, and audio/video stream details for both
/// the desktop and overlay properties dialogs through the media platform's inspection APIs.
/// </summary>
internal static class MediaPropertiesBuilder
{
    /// <summary>Container tags already shown under their own localized labels.</summary>
    private static readonly HashSet<string> MappedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "artist", "album", "album_artist", "albumartist", "genre", "date", "track",
        "disc", "composer", "encoder", "duration",
    };

    /// <summary>Stream tags shown under their own localized labels.</summary>
    private static readonly HashSet<string> MappedStreamTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "language",
    };

    /// <summary>Tag values longer than this are elided: some containers store whole documents.</summary>
    private const int MaxTagLength = 512;

    public static async Task<(IReadOnlyList<MediaPropertyItem> Items, string FileName)> BuildAsync(string filePath)
    {
        var resolver = new CapturedPlatformMediaResolver(IMultiMediaPlatformIO.Instance);
        return await BuildAsync(MediaReference.Local(filePath), resolver).ConfigureAwait(true);
    }

    public static async Task<(IReadOnlyList<MediaPropertyItem> Items, string FileName)> BuildAsync(
        MediaReference media,
        IClientMediaResolver mediaResolver)
    {
        var items = new List<MediaPropertyItem>();
        var filePath = media.Path;
        if (string.IsNullOrEmpty(filePath))
        {
            return (items, string.Empty);
        }

        var platform = await mediaResolver.ResolvePlatformAsync(media).ConfigureAwait(true);
        MediaInfo? info = null;
        try
        {
            info = await platform.GetMediaInfoAsync(filePath).ConfigureAwait(true);
        }
        catch
        {
            // Metadata is best-effort; the file properties and the probe below stand on their own.
        }

        MediaFileProperties? properties = null;
        try
        {
            properties = await platform
                .GetMediaFilePropertiesAsync(filePath)
                .ConfigureAwait(true);
        }
        catch
        {
            // File properties are independently best-effort.
        }

        MediaProbeReport? probe = null;
        try
        {
            probe = await platform.InspectMediaAsync(filePath).ConfigureAwait(true);
        }
        catch
        {
            // A server too old to inspect, or a file FFmpeg cannot open, still shows the rest.
        }

        bool isPhoto = MediaContainerFormats.IsImage(properties?.Name ?? filePath);
        AppendGeneral(items, filePath, properties, info, probe, isPhoto);
        if (isPhoto)
        {
            await AppendPhotoAsync(items, platform, filePath, probe).ConfigureAwait(true);
            await AppendExifAsync(items, platform, filePath, properties?.Name).ConfigureAwait(true);
        }
        else
        {
            AppendMetadata(items, info, probe);
            AppendStreams(items, probe);
            AppendChapters(items, probe);
        }

        return (items, properties?.Name ?? Path.GetFileName(filePath));
    }

    private static void AppendGeneral(
        List<MediaPropertyItem> items,
        string filePath,
        MediaFileProperties? properties,
        MediaInfo? info,
        MediaProbeReport? probe,
        bool isPhoto)
    {
        var name = properties?.Name ?? Path.GetFileName(filePath);
        Section(items, Strings.Resources.Props_SectionGeneral);
        Add(items, Strings.Resources.Props_FileName, name);
        Add(items, Strings.Resources.Props_Type, Path.GetExtension(name).TrimStart('.').ToUpperInvariant());
        if (properties is { Length: >= 0 })
        {
            Add(items, Strings.Resources.Props_Size, string.Format(
                CultureInfo.CurrentCulture,
                Strings.Resources.Props_FileSizeMbFormat,
                properties.Length / 1024.0 / 1024.0));
        }

        Add(items, Strings.Resources.Props_Location, properties?.DirectoryPath);
        if (!isPhoto)
            Add(items, Strings.Resources.Props_Container, DescribeContainer(probe));

        var duration = probe is { DurationMilliseconds: > 0 }
            ? TimeSpan.FromMilliseconds(probe.DurationMilliseconds)
            : info?.Duration ?? TimeSpan.Zero;
        if (!isPhoto && duration > TimeSpan.Zero)
        {
            Add(items, Strings.Resources.Props_Duration, FormatDuration(duration));
        }

        if (!isPhoto && probe is { BitRate: > 0 })
        {
            Add(items, Strings.Resources.Props_Bitrate, FormatBitRate(probe.BitRate));
        }
    }

    private static async Task AppendPhotoAsync(List<MediaPropertyItem> items, IMultiMediaPlatformIO platform,
        string filePath, MediaProbeReport? probe)
    {
        ImageProbeInfo? photo = null;
        try
        {
            await using Stream stream = await platform.OpenMediaFileAsync(filePath).ConfigureAwait(true);
            var size = await StillImage.ReadContainerPixelSizeAsync(stream).ConfigureAwait(true);
            if (size is { Width: > 0, Height: > 0 })
                photo = new ImageProbeInfo(size.Width, size.Height, null);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Media.Properties", $"Could not read container photo dimensions for {filePath}.", exception);
        }

        if (photo is null)
        {
            try
            {
                photo = await platform.ProbeImageAsync(filePath).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Media.Properties", $"Could not read photo dimensions for {filePath}.", exception);
            }
        }

        MediaStreamProbeReport[] images = probe?.Streams.Where(stream => stream.Kind == "video" &&
            stream.Width > 0 && stream.Height > 0).ToArray() ?? [];
        bool hasDimensions = photo is { Width: > 0, Height: > 0 };
        if (!hasDimensions && images.Length == 0)
            return;
        int width = hasDimensions ? photo!.Width : images.Length == 1 ? images[0].Width : 0;
        int height = hasDimensions ? photo!.Height : images.Length == 1 ? images[0].Height : 0;
        Section(items, Strings.Resources.Props_SectionPhoto);
        if (width > 0 && height > 0)
            Add(items, Strings.Resources.Props_Resolution, $"{width} \u00d7 {height} ({(double)width * height / 1_000_000:0.##} MP)");
        Add(items, Strings.Resources.Props_Codec, Values(images.Select(DescribePhotoCodec)));
        Add(items, Strings.Resources.Props_Profile, Values(images.Select(DescribeProfile)));
        Add(items, Strings.Resources.Props_PixelFormat, Values(images.Select(stream => stream.PixelFormat)));
        Add(items, Strings.Resources.Props_BitDepth, Values(images.Where(stream => stream.BitDepth > 0)
            .Select(stream => stream.BitDepth.ToString(CultureInfo.CurrentCulture))));
        Add(items, Strings.Resources.Props_Color, Values(images.Select(DescribeColor)));
        if (images.Length > 1)
            Add(items, Strings.Resources.Props_PhotoComponents, string.Join(Environment.NewLine,
                images.GroupBy(stream => (stream.Width, stream.Height, Codec: DescribePhotoCodec(stream)))
                    .OrderByDescending(group => group.Count())
                    .Select(group => string.Format(CultureInfo.CurrentCulture, Strings.Resources.Props_PhotoComponentFormat,
                        group.Count(), $"{group.Key.Width} \u00d7 {group.Key.Height}", group.Key.Codec))));

        static string Values(IEnumerable<string?> values) => string.Join(", ", values
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? DescribePhotoCodec(MediaStreamProbeReport stream) => stream.CodecName switch
    {
        "hevc" => "HEVC (H.265)",
        "av1" => "AV1",
        "mjpeg" or "jpeg" => "JPEG",
        "png" => "PNG",
        "webp" => "WebP",
        "jpegxl" or "libjxl" => "JPEG XL",
        "tiff" => "TIFF",
        _ => DescribeCodec(stream),
    };

    private static void AppendMetadata(List<MediaPropertyItem> items, MediaInfo? info, MediaProbeReport? probe)
    {
        var section = new List<MediaPropertyItem>();
        if (info is not null)
        {
            Add(section, Strings.Resources.Common_ColumnTitle, info.Title);
            Add(section, Strings.Resources.Common_ColumnArtist, info.Artist);
            Add(section, Strings.Resources.Common_ColumnAlbum, info.Album);
            Add(section, Strings.Resources.Props_AlbumArtist, info.AlbumArtist);
            Add(section, Strings.Resources.Common_ColumnGenre, info.Genre);
            Add(section, Strings.Resources.Props_Date, info.Date);
            Add(section, Strings.Resources.Props_Track, info.TrackNumber);
            Add(section, Strings.Resources.Props_Disc, info.DiscNumber);
            Add(section, Strings.Resources.Props_Composer, info.Composer);
        }

        if (probe is not null)
        {
            foreach (var tag in probe.Tags.Where(tag => !MappedTags.Contains(tag.Key)))
            {
                Add(section, tag.Key, Trim(tag.Value));
            }
        }

        if (section.Count == 0)
        {
            return;
        }

        Section(items, Strings.Resources.Props_SectionMetadata);
        items.AddRange(section);
    }

    /// <summary>
    /// What the camera recorded about the shot, which FFmpeg does not report and so has to be read
    /// out of the file itself. Only the head and, for a container that indexes its metadata, the
    /// index and the block it points at are read, not the picture.
    /// </summary>
    private static async Task AppendExifAsync(
        List<MediaPropertyItem> items,
        IMultiMediaPlatformIO platform,
        string filePath,
        string? fileName)
    {
        if (!MediaContainerFormats.IsImage(fileName ?? filePath))
        {
            return;
        }

        IReadOnlyList<ExifEntry> exif;
        try
        {
            await using Stream stream = await platform.OpenMediaFileAsync(filePath).ConfigureAwait(true);
            exif = await ExifMetadata.ReadAsync(stream).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Media.Properties", $"Could not read EXIF metadata for {filePath}.", exception);
            return;
        }

        if (exif.Count == 0)
        {
            return;
        }

        Section(items, Strings.Resources.Props_SectionExif);
        foreach (ExifEntry entry in exif)
        {
            Add(items, ExifFieldName(entry.Name), entry.Value);
        }
    }

    private static string ExifFieldName(string name) => name switch
    {
        "Make" => Strings.Resources.Props_ExifMake,
        "Model" => Strings.Resources.Props_ExifModel,
        "Software" => Strings.Resources.Props_ExifSoftware,
        "Artist" => Strings.Resources.Props_ExifArtist,
        "Copyright" => Strings.Resources.Props_ExifCopyright,
        "ImageDescription" => Strings.Resources.Props_ExifImageDescription,
        "DateTime" => Strings.Resources.Props_ExifDateTime,
        "Orientation" => Strings.Resources.Props_ExifOrientation,
        "Resolution" => Strings.Resources.Props_ExifResolution,
        "DateTimeOriginal" => Strings.Resources.Props_ExifDateTimeOriginal,
        "DateTimeDigitized" => Strings.Resources.Props_ExifDateTimeDigitized,
        "LensModel" => Strings.Resources.Props_ExifLensModel,
        "ExposureTime" => Strings.Resources.Props_ExifExposureTime,
        "FNumber" => Strings.Resources.Props_ExifFNumber,
        "ISO" => Strings.Resources.Props_ExifISO,
        "ExposureBias" => Strings.Resources.Props_ExifExposureBias,
        "FocalLength" => Strings.Resources.Props_ExifFocalLength,
        "FocalLengthIn35mmFilm" => Strings.Resources.Props_ExifFocalLengthIn35mmFilm,
        "ExposureProgram" => Strings.Resources.Props_ExifExposureProgram,
        "MeteringMode" => Strings.Resources.Props_ExifMeteringMode,
        "Flash" => Strings.Resources.Props_ExifFlash,
        "WhiteBalance" => Strings.Resources.Props_ExifWhiteBalance,
        "ColorSpace" => Strings.Resources.Props_ExifColorSpace,
        "GPSPosition" => Strings.Resources.Props_ExifGPSPosition,
        "GPSAltitude" => Strings.Resources.Props_ExifGPSAltitude,
        _ => name,
    };

    private static void AppendStreams(List<MediaPropertyItem> items, MediaProbeReport? probe)
    {
        if (probe is null)
        {
            return;
        }

        foreach (var stream in probe.Streams)
        {
            Section(items, string.Format(CultureInfo.CurrentCulture, SectionFormat(stream.Kind), stream.Index));
            Add(items, Strings.Resources.Props_Codec, DescribeCodec(stream));
            Add(items, Strings.Resources.Props_Profile, DescribeProfile(stream));

            if (stream.Tags.TryGetValue("title", out var title))
            {
                Add(items, Strings.Resources.Common_ColumnTitle, Trim(title));
            }

            if (stream.Tags.TryGetValue("language", out var language))
            {
                Add(items, Strings.Resources.Props_Language, Trim(language));
            }

            if (stream.Width > 0 && stream.Height > 0)
            {
                Add(items, Strings.Resources.Props_Resolution, DescribeResolution(stream));
                Add(items, Strings.Resources.Props_AspectRatio, DescribeAspectRatio(stream));
                Add(items, Strings.Resources.Props_FrameRate, DescribeFrameRate(stream));
                Add(items, Strings.Resources.Props_PixelFormat, stream.PixelFormat);
                if (stream.BitDepth > 0)
                {
                    Add(items, Strings.Resources.Props_BitDepth, stream.BitDepth.ToString(CultureInfo.CurrentCulture));
                }

                Add(items, Strings.Resources.Props_Color, DescribeColor(stream));
            }

            if (stream.SampleRate > 0)
            {
                Add(items, Strings.Resources.Props_SampleRate, string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Resources.Props_SampleRateKhzFormat,
                    stream.SampleRate / 1000.0));
            }

            if (stream.ChannelCount > 0)
            {
                Add(items, Strings.Resources.Props_Channels, DescribeChannels(stream));
            }

            Add(items, Strings.Resources.Props_SampleFormat, stream.SampleFormat);

            var bitRate = stream.BitRate > 0 ? stream.BitRate : BitRateFromTags(stream);
            if (bitRate > 0)
            {
                Add(items, Strings.Resources.Props_Bitrate, FormatBitRate(bitRate));
            }

            if (stream.DurationMilliseconds > 0 && stream.DurationMilliseconds != probe.DurationMilliseconds)
            {
                Add(items, Strings.Resources.Props_Duration, FormatDuration(
                    TimeSpan.FromMilliseconds(stream.DurationMilliseconds)));
            }

            if (stream.FrameCount > 0)
            {
                Add(items, Strings.Resources.Props_Frames, stream.FrameCount.ToString("N0", CultureInfo.CurrentCulture));
            }

            Add(items, Strings.Resources.Props_Disposition, stream.Disposition);

            foreach (var tag in stream.Tags.Where(tag => !MappedStreamTags.Contains(tag.Key)))
            {
                Add(items, tag.Key, Trim(tag.Value));
            }
        }
    }

    private static void AppendChapters(List<MediaPropertyItem> items, MediaProbeReport? probe)
    {
        if (probe is null || probe.Chapters.Length == 0)
        {
            return;
        }

        Section(items, Strings.Resources.Props_SectionChapters);
        for (int index = 0; index < probe.Chapters.Length; index++)
        {
            var chapter = probe.Chapters[index];
            var start = FormatDuration(TimeSpan.FromMilliseconds(chapter.StartMilliseconds));
            Add(items, start, chapter.Title ?? (index + 1).ToString(CultureInfo.CurrentCulture));
        }
    }

    private static string SectionFormat(string kind) => kind switch
    {
        "video" => Strings.Resources.Props_SectionVideoStreamFormat,
        "audio" => Strings.Resources.Props_SectionAudioStreamFormat,
        "subtitle" => Strings.Resources.Props_SectionSubtitleStreamFormat,
        _ => Strings.Resources.Props_SectionStreamFormat,
    };

    private static string? DescribeContainer(MediaProbeReport? probe)
    {
        if (probe is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(probe.FormatLongName) && !string.IsNullOrWhiteSpace(probe.FormatName))
        {
            return $"{probe.FormatLongName} ({probe.FormatName})";
        }

        return probe.FormatLongName ?? probe.FormatName;
    }

    private static string? DescribeCodec(MediaStreamProbeReport stream)
    {
        var name = string.IsNullOrWhiteSpace(stream.CodecLongName) || stream.CodecLongName == stream.CodecName
            ? stream.CodecName
            : $"{stream.CodecLongName} ({stream.CodecName})";
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(stream.CodecTag) ? name : $"{name} [{stream.CodecTag}]";
    }

    private static string? DescribeProfile(MediaStreamProbeReport stream)
    {
        if (string.IsNullOrWhiteSpace(stream.Profile))
        {
            return null;
        }

        // FFmpeg reports an unset level as -99, and each codec numbers its levels differently.
        var level = stream.Level <= 0 ? null : stream.CodecName switch
        {
            "hevc" => (stream.Level / 30.0).ToString("0.#", CultureInfo.InvariantCulture),
            "h264" => (stream.Level / 10.0).ToString("0.#", CultureInfo.InvariantCulture),
            _ => stream.Level.ToString(CultureInfo.InvariantCulture),
        };

        return level is null ? stream.Profile : $"{stream.Profile} @ L{level}";
    }

    /// <summary>
    /// Matroska states per-track bit rates in a <c>BPS</c> tag rather than in the stream header,
    /// which is where most of this player's library actually keeps them.
    /// </summary>
    private static long BitRateFromTags(MediaStreamProbeReport stream)
    {
        foreach (var tag in stream.Tags)
        {
            if ((tag.Key.Equals("BPS", StringComparison.OrdinalIgnoreCase)
                    || tag.Key.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                && long.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// The coded size, with the display matrix beside it: the stream really does hold the picture
    /// the other way up, and hiding that would make the number look wrong against the picture.
    /// </summary>
    private static string DescribeResolution(MediaStreamProbeReport stream)
    {
        var text = $"{stream.Width} \u00d7 {stream.Height}";
        if (stream.Rotation != 0)
        {
            text += $" \u21bb {stream.Rotation}\u00b0";
        }

        return stream.MirrorHorizontally ? $"{text} \u2194" : text;
    }

    private static string DescribeAspectRatio(MediaStreamProbeReport stream) =>
        string.IsNullOrWhiteSpace(stream.SampleAspectRatio)
            ? stream.DisplayAspectRatio ?? string.Empty
            : $"{stream.DisplayAspectRatio} (SAR {stream.SampleAspectRatio})";

    private static string DescribeFrameRate(MediaStreamProbeReport stream)
    {
        var rate = stream.FrameRate > 0 ? stream.FrameRate : stream.AverageFrameRate;
        return rate > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Resources.Props_FrameRateFpsFormat, rate)
            : string.Empty;
    }

    private static string DescribeColor(MediaStreamProbeReport stream)
    {
        var parts = new[] { stream.ColorSpace, stream.ColorPrimaries, stream.ColorTransfer, stream.ColorRange }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(", ", parts);
    }

    private static string DescribeChannels(MediaStreamProbeReport stream) =>
        string.IsNullOrWhiteSpace(stream.ChannelLayout)
            ? stream.ChannelCount.ToString(CultureInfo.CurrentCulture)
            : $"{stream.ChannelCount} ({stream.ChannelLayout})";

    private static string FormatBitRate(long bitsPerSecond) => string.Format(
        CultureInfo.CurrentCulture,
        Strings.Resources.Props_BitrateKbpsFormat,
        bitsPerSecond / 1000.0);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalDays >= 1 ? duration.ToString(@"d\.hh\:mm\:ss") : duration.ToString(@"hh\:mm\:ss");

    private static string Trim(string value) =>
        value.Length <= MaxTagLength ? value : value[..MaxTagLength] + "\u2026";

    private static void Section(List<MediaPropertyItem> items, string header) =>
        items.Add(new MediaPropertyItem(header, string.Empty, IsSection: true));

    private static void Add(List<MediaPropertyItem> items, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new MediaPropertyItem(key, value));
        }
    }
}
