using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services;

/// <summary>A JPEG embedded in a DNG and the EXIF orientation it is shown in.</summary>
internal readonly record struct DngPreview(long Offset, int Length, uint Width, uint Height, uint Orientation);

// DNG directory parsing. Free of rendering dependencies, so the browser's media worker shows the same
// embedded previews.
internal static unsafe partial class PhotoDecoder
{
    internal static StillImageThumbnail? DecodeDngThumbnail(Stream stream, int maxDimension, CancellationToken cancellationToken)
    {
        using NativeImageThumbnail? thumbnail = DecodeNativeDngThumbnail(stream, maxDimension, cancellationToken);
        return thumbnail is null ? null : new StillImageThumbnail
        {
            Width = thumbnail.Width, Height = thumbnail.Height, Pixels = thumbnail.Pixels.GetSpan().ToArray(),
        };
    }

    internal static NativeImageThumbnail? DecodeNativeDngThumbnail(Stream stream, int maxDimension, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[2];
            foreach (DngPreview preview in FindDngPreviews(stream, cancellationToken, includeReduced: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preview.Length < signature.Length)
                    continue;
                stream.Position = preview.Offset;
                stream.ReadExactly(signature);
                if (!signature.SequenceEqual((ReadOnlySpan<byte>)[0xff, 0xd8]))
                    continue;
                using var embedded = new SubStream(stream, preview.Offset, preview.Length);
                try
                {
                    return StillImage.DecodeNativeThumbnail(embedded, maxDimension, true, cancellationToken, (int)preview.Orientation);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    AppLog.Warning("Photo", "Could not decode the embedded DNG thumbnail.", exception);
                }
            }
            return null;
        }
        finally { stream.Position = origin; }
    }

    /// <summary>Lists JPEG previews of a DNG, largest first, requiring full size unless reduced previews are requested.</summary>
    internal static List<DngPreview> FindDngPreviews(Stream stream, CancellationToken cancellationToken, bool includeReduced = false)
    {
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length ||
                (!header.StartsWith("II*\0"u8) && !header.StartsWith("MM\0*"u8)))
                return [];

            var tiff = new DngDirectories(stream, origin, header[0] == 'M', cancellationToken);
            List<DngDirectory>? directories = tiff.Read(ReadUInt32(header[4..], header[0] == 'M'));
            if (directories is not { Count: > 0 } || !directories[0].IsDng)
                return [];
            DngDirectory? raw = directories.Where(directory => directory.Photometric is 32803 or 34892)
                .OrderByDescending(directory => (ulong)directory.Width * directory.Height).FirstOrDefault();
            if (raw is null || raw.Width == 0 || raw.Height == 0)
                return [];
            uint width = raw.CropWidth is > 0 && raw.CropWidth <= raw.Width ? raw.CropWidth : raw.Width;
            uint height = raw.CropHeight is > 0 && raw.CropHeight <= raw.Height ? raw.CropHeight : raw.Height;

            return directories.Where(directory => directory.Photometric is 2 or 6 &&
                    directory.Compression is 6 or 7 && directory.Width > 0 && directory.Height > 0 &&
                    (includeReduced || directory.Width >= width && directory.Height >= height) &&
                    directory.Offset > 0 && directory.Length is > 0 and <= MaxGainMapBytes &&
                    directory.Offset <= stream.Length - origin && directory.Length <= stream.Length - origin - directory.Offset)
                .OrderByDescending(directory => (ulong)directory.Width * directory.Height)
                .Select(directory => new DngPreview(origin + directory.Offset, (int)directory.Length, directory.Width, directory.Height,
                    directory.Orientation is >= 1 and <= 8 ? directory.Orientation : directories[0].Orientation))
                .ToList();
        }
        catch (EndOfStreamException)
        {
            return [];
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private sealed class DngDirectory
    {
        public bool IsDng;
        public uint Width, Height, CropWidth, CropHeight, Compression, Photometric, Orientation, Offset, Length;
    }

    private sealed class DngDirectories(Stream stream, long origin, bool bigEndian, CancellationToken cancellationToken)
    {
        private const int MaximumDirectories = 64;
        private const int MaximumEntries = 4096;

        public List<DngDirectory>? Read(uint first)
        {
            var result = new List<DngDirectory>();
            var visited = new HashSet<uint>();
            var pending = new Queue<uint>();
            pending.Enqueue(first);
            Span<byte> countBytes = stackalloc byte[2];
            while (pending.TryDequeue(out uint offset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset == 0 || !visited.Add(offset))
                    continue;
                if (visited.Count > MaximumDirectories || offset < 8 || offset > stream.Length - origin - 2)
                    return null;
                stream.Position = origin + offset;
                stream.ReadExactly(countBytes);
                int count = ReadUInt16(countBytes, bigEndian);
                if (count > MaximumEntries || count * 12L + 4 > stream.Length - stream.Position)
                    return null;
                byte[] entries = ReadBytes(stream, count * 12 + 4);
                var directory = new DngDirectory();
                for (int index = 0; index < count; index++)
                {
                    ReadOnlySpan<byte> entry = entries.AsSpan(index * 12, 12);
                    ushort tag = ReadUInt16(entry, bigEndian);
                    ushort type = ReadUInt16(entry[2..], bigEndian);
                    uint elements = ReadUInt32(entry[4..], bigEndian);
                    if (tag == 50706 && type == 1 && elements == 4)
                        directory.IsDng = true;
                    if (tag is not (256 or 257 or 259 or 262 or 273 or 274 or 279 or 330 or 513 or 514 or 50720))
                        continue;
                    uint[] values = ReadValues(entry, tag == 330 ? MaximumDirectories : tag == 50720 ? 2 : 1);
                    if (values.Length == 0)
                        continue;
                    switch (tag)
                    {
                        case 256: directory.Width = values[0]; break;
                        case 257: directory.Height = values[0]; break;
                        case 259: directory.Compression = values[0]; break;
                        case 262: directory.Photometric = values[0]; break;
                        case 273 or 513: directory.Offset = values[0]; break;
                        case 274: directory.Orientation = values[0]; break;
                        case 279 or 514: directory.Length = values[0]; break;
                        case 330:
                            if (pending.Count + values.Length > MaximumDirectories)
                                return null;
                            foreach (uint child in values)
                                pending.Enqueue(child);
                            break;
                        case 50720 when values.Length == 2:
                            directory.CropWidth = values[0];
                            directory.CropHeight = values[1];
                            break;
                    }
                }
                result.Add(directory);
                if (result.Count == 1 && !directory.IsDng)
                    return null;
                uint next = ReadUInt32(entries.AsSpan(count * 12), bigEndian);
                if (next != 0)
                    pending.Enqueue(next);
            }
            return result;
        }

        private uint[] ReadValues(ReadOnlySpan<byte> entry, int maximumCount)
        {
            ushort type = ReadUInt16(entry[2..], bigEndian);
            uint count = ReadUInt32(entry[4..], bigEndian);
            int size = type switch { 3 => 2, 4 or 13 => 4, 5 => 8, _ => 0 };
            if (size == 0 || count == 0 || count > maximumCount)
                return [];
            int length = (int)count * size;
            ReadOnlySpan<byte> data;
            if (length <= 4)
                data = entry.Slice(8, length);
            else
            {
                uint offset = ReadUInt32(entry[8..], bigEndian);
                if (offset > stream.Length - origin || length > stream.Length - origin - offset)
                    return [];
                stream.Position = origin + offset;
                data = ReadBytes(stream, length);
            }
            uint[] values = new uint[count];
            for (int index = 0; index < values.Length; index++)
            {
                ReadOnlySpan<byte> value = data[(index * size)..];
                if (type == 5)
                {
                    uint denominator = ReadUInt32(value[4..], bigEndian);
                    if (denominator == 0)
                        return [];
                    ulong numerator = ReadUInt32(value, bigEndian);
                    values[index] = (uint)((numerator + denominator - 1) / denominator);
                }
                else
                    values[index] = size == 2 ? ReadUInt16(value, bigEndian) : ReadUInt32(value, bigEndian);
            }
            return values;
        }
    }
}
