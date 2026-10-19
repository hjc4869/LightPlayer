using System;
using System.Buffers.Binary;
using System.IO;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Reads the EXIF orientation of a still picture, which is how a camera records that it was held
/// sideways without re-encoding the pixels.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg does not surface this. A container that has somewhere to put the transform -- HEIF and
/// AVIF carry <c>irot</c> and <c>imir</c> boxes -- has it turned into a display matrix by the
/// demuxer and read by <see cref="VideoDisplayMatrix"/>, but a JPEG's orientation lives in an EXIF
/// tag the decoder passes over, so it has to be found here or the picture is shown on its side.
/// </para>
/// <para>
/// Only the head of the file is read, and only IFD0 is walked: orientation is required to be
/// there, and following the offsets any further would mean parsing an arbitrary tag tree to answer
/// a question already settled.
/// </para>
/// </remarks>
public static class ExifOrientation
{
    private const ushort OrientationTag = 0x0112;

    /// <summary>
    /// The transform that brings the stored picture upright, expressed the way a frame carries it:
    /// a left-to-right mirror, then a clockwise turn. False when the file declares nothing.
    /// </summary>
    public static bool TryRead(Stream stream, out int rotation, out bool mirrorHorizontally)
    {
        rotation = 0;
        mirrorHorizontally = false;

        ReadOnlySpan<byte> tiff = ExifBlock.Read(stream);
        return !tiff.IsEmpty &&
               TryReadOrientation(tiff, out int orientation) &&
               TryDecode(orientation, out rotation, out mirrorHorizontally);
    }

    /// <inheritdoc cref="TryRead(Stream, out int, out bool)"/>
    public static bool TryRead(ReadOnlySpan<byte> header, out int rotation, out bool mirrorHorizontally)
    {
        rotation = 0;
        mirrorHorizontally = false;

        ReadOnlySpan<byte> tiff = ExifBlock.Find(header);
        return !tiff.IsEmpty &&
               TryReadOrientation(tiff, out int orientation) &&
               TryDecode(orientation, out rotation, out mirrorHorizontally);
    }

    /// <summary>Splits one of the eight EXIF orientations into a mirror and a clockwise turn.</summary>
    public static bool TryDecode(int orientation, out int rotation, out bool mirrorHorizontally)
    {
        // A mirror about the vertical axis, then a turn, reaches all eight symmetries of a
        // rectangle. Values 5 and 7 are the diagonal reflections, which is why they carry both.
        (rotation, mirrorHorizontally) = orientation switch
        {
            1 => (0, false),
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (270, true),
            6 => (90, false),
            7 => (90, true),
            8 => (270, false),
            _ => (-1, false),
        };

        if (rotation < 0)
        {
            rotation = 0;
            return false;
        }

        return true;
    }

    private static bool TryReadOrientation(ReadOnlySpan<byte> tiff, out int orientation)
        => TryReadShort(tiff, OrientationTag, out orientation);

    internal static bool TryReadShort(ReadOnlySpan<byte> tiff, ushort tag, out int orientation)
    {
        orientation = 0;
        if (tiff.Length < 8)
            return false;

        bool littleEndian = tiff[0] == 0x49;
        if (ReadUInt16(tiff[2..], littleEndian) != 42)
            return false;

        uint directory = ReadUInt32(tiff[4..], littleEndian);
        if (directory + 2 > (uint)tiff.Length)
            return false;

        ReadOnlySpan<byte> ifd = tiff[(int)directory..];
        int entries = ReadUInt16(ifd, littleEndian);
        if (2 + (entries * 12) > ifd.Length)
            return false;

        for (int i = 0; i < entries; i++)
        {
            ReadOnlySpan<byte> entry = ifd.Slice(2 + (i * 12), 12);
            if (ReadUInt16(entry, littleEndian) != tag)
                continue;

            // A single SHORT fits the entry's own value field, so no offset has to be followed.
            if (ReadUInt16(entry[2..], littleEndian) != 3 || ReadUInt32(entry[4..], littleEndian) != 1)
                return false;

            orientation = ReadUInt16(entry[8..], littleEndian);
            return true;
        }

        return false;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(value)
        : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadUInt32(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(value)
        : BinaryPrimitives.ReadUInt32BigEndian(value);
}
