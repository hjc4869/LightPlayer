using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static List<byte[]> FindJpegThumbnails(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        var thumbnails = new List<byte[]>();
        try
        {
            if (!ReadHeader(stream, stream.Length, out _, out ImageRange[]? images, out _, thumbnails))
                return thumbnails;
            if (images is not null)
            {
                foreach (ImageRange image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (image.Type is not (0x010001 or 0x010002) || image.Length > 16 * 1024 * 1024 || thumbnails.Count >= 16)
                        continue;
                    stream.Position = image.Offset;
                    thumbnails.Add(ReadBytes(stream, image.Length));
                }
            }
            return thumbnails;
        }
        finally { stream.Position = origin; }
    }

    private static byte[]? ReadJfifThumbnail(ReadOnlySpan<byte> data)
    {
        bool indexed = false;
        if (data.StartsWith("JFIF\0"u8) && data.Length >= 14)
            data = data[12..];
        else if (data.StartsWith("JFXX\0"u8) && data.Length >= 6)
        {
            if (data[5] == 0x10)
                return data[6..].ToArray();
            if (data[5] is not (0x11 or 0x13))
                return null;
            indexed = data[5] == 0x11;
            data = data[6..];
        }
        else
            return null;
        if (data.Length < 2)
            return null;
        int width = data[0];
        int height = data[1];
        data = data[2..];
        int pixels = width * height;
        if (pixels == 0 || data.Length < (indexed ? 768 + pixels : pixels * 3))
            return null;
        byte[] header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"P6\n{width} {height}\n255\n"));
        byte[] result = new byte[header.Length + pixels * 3];
        header.CopyTo(result, 0);
        Span<byte> output = result.AsSpan(header.Length);
        if (indexed)
        {
            for (int index = 0; index < pixels; index++)
                data.Slice(data[768 + index] * 3, 3).CopyTo(output.Slice(index * 3, 3));
        }
        else
            data[..output.Length].CopyTo(output);
        return result;
    }

    private static bool TryReadJpegMetadata(XElement root, Stream stream, out GainMap metadata)
    {
        foreach (XElement element in root.DescendantsAndSelf(RdfNamespace + "Description"))
        {
            if (Value(element, "Version") == "1.0" && GainMap.TryRead(element, out metadata))
                return true;
            if (TryReadApple(element, null, out metadata))
                return true;
            XNamespace apple = "http://ns.apple.com/HDRGainMap/1.0/";
            if ((element.Attribute(apple + "HDRGainMapVersion") is not null || element.Element(apple + "HDRGainMapVersion") is not null) &&
                TryReadApple(element, PhotoHdrMetadata.ReadAppleHeadroom(stream), out metadata))
                return true;
        }
        metadata = default;
        return false;
    }

    private static ImageRange[]? ReadContainerImages(Stream stream, long origin, XElement? metadata)
    {
        XNamespace container = "http://ns.google.com/photos/1.0/container/";
        XNamespace itemNamespace = "http://ns.google.com/photos/1.0/container/item/";
        XElement[]? entries = metadata?.Descendants(container + "Item").ToArray();
        if (entries is not { Length: >= 2 and <= 64 } || entries[0].Attribute(itemNamespace + "Semantic")?.Value != "Primary")
            return null;
        long end = FindJpegEnd(stream, origin);
        if (end <= origin || end - origin > int.MaxValue)
            return null;
        var images = new List<ImageRange> { new(origin, (int)(end - origin)) };
        if (!long.TryParse(entries[0].Attribute(itemNamespace + "Padding")?.Value ?? "0",
            NumberStyles.None, CultureInfo.InvariantCulture, out long padding) || padding > stream.Length - end)
            return null;
        long offset = end + padding;
        foreach (XElement item in entries.AsSpan(1))
        {
            if (!int.TryParse(item.Attribute(itemNamespace + "Length")?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int length) || length <= 0 || length > stream.Length - offset)
                return null;
            if (item.Attribute(itemNamespace + "Semantic")?.Value == "GainMap" &&
                item.Attribute(itemNamespace + "Mime")?.Value == "image/jpeg")
                images.Add(new ImageRange(offset, length));
            offset += length;
            if (!long.TryParse(item.Attribute(itemNamespace + "Padding")?.Value ?? "0",
                NumberStyles.None, CultureInfo.InvariantCulture, out padding) || padding > stream.Length - offset)
                return null;
            offset += padding;
        }
        return images.Count > 1 ? images.ToArray() : null;
    }

    private static long FindJpegEnd(Stream stream, long origin)
    {
        stream.Position = origin + 2;
        bool entropy = false;
        while (stream.Position < stream.Length)
        {
            int value = stream.ReadByte();
            if (value != 0xff)
            {
                if (entropy)
                    continue;
                return -1;
            }
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker == 0xd9)
                return stream.Position;
            if ((entropy && marker == 0) || marker is 1 or >= 0xd0 and <= 0xd7)
                continue;
            if (marker <= 0 || marker == 0xd8 || stream.Position > stream.Length - 2)
                return -1;
            int length = (stream.ReadByte() << 8) | stream.ReadByte();
            if (length < 2 || length - 2 > stream.Length - stream.Position)
                return -1;
            stream.Position += length - 2;
            entropy = marker == 0xda;
        }
        return -1;
    }
}