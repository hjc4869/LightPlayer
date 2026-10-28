using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    /// <summary>
    /// The EXIF item, with its TIFF header offset prefix, and the XMP packet describing the primary image
    /// of a HEIF or AVIF file; each is empty when the file has none.
    /// </summary>
    internal static (byte[] Exif, byte[] Xmp) ReadIsoMetadata(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            while (ReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type == FourCc("meta"u8) && end - start is > 4 and <= MaxContainerMetadataBytes)
                    return new IsoImageItems(ReadBytes(stream, (int)(end - start)), origin).FindMetadata(stream);
                stream.Position = end;
            }

            return ([], []);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private sealed partial class IsoImageItems
    {
        /// <summary>
        /// Metadata items that describe the primary image, its derived tone-mapped image, or no image in
        /// particular. Items describing others, such as a gain map's own XMP, are skipped.
        /// </summary>
        public (byte[] Exif, byte[] Xmp) FindMetadata(Stream stream)
        {
            byte[] exif = [], xmp = [];
            foreach ((uint id, Item item) in items.OrderBy(pair => Describes(pair.Key) ?? false ? 0 : 1).ThenBy(pair => pair.Key))
            {
                if (Describes(id) == false)
                    continue;
                if (exif.Length == 0 && item.Type == FourCc("Exif"u8))
                    exif = ReadItem(stream, id);
                else if (xmp.Length == 0 && item.Type == FourCc("mime"u8) && item.ContentType == "application/rdf+xml" &&
                    string.IsNullOrEmpty(item.ContentEncoding))
                    xmp = ReadItem(stream, id);
            }

            return (exif, xmp);
        }

        /// <summary>Whether an item's <c>cdsc</c> references name the primary image; null when it names none.</summary>
        private bool? Describes(uint id)
        {
            uint[] targets = references.Where(reference => reference.Type == FourCc("cdsc"u8) && reference.From == id)
                .SelectMany(reference => reference.To).ToArray();
            if (targets.Length == 0)
                return null;
            return targets.Any(target => target == primary ||
                references.Any(reference => reference.Type == FourCc("dimg"u8) &&
                    (reference.From == target && reference.To.Contains(primary) && IsToneMap(target) ||
                        reference.From == primary && reference.To.Contains(target) && IsToneMap(primary))));
        }

        private bool IsToneMap(uint id) => items.TryGetValue(id, out Item? item) && item.Type == FourCc("tmap"u8);
    }
}
