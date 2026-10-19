using System;
using System.Buffers.Binary;
using System.IO;

namespace LightStudio.FfmpegShim.Media;

public sealed record JxrImageInfo(int Width, int Height, Guid PixelFormat, int Orientation, byte[] IccProfile);

internal sealed record JxrContainer(JxrImageInfo Info, int ImageOffset, int ImageLength, int AlphaOffset, int AlphaLength)
{
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x49 && data[1] == 0x49 && data[2] == 0xbc && data[3] == 1;

    public static JxrContainer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !HasSignature(data))
            throw new InvalidDataException("Invalid JPEG XR container signature.");

        int directory = CheckedOffset(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), data.Length, 2);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[directory..]);
        if (count > (data.Length - directory - 2) / 12)
            throw new InvalidDataException("Truncated JPEG XR image directory.");

        int width = 0, height = 0, orientation = 0;
        uint imageOffset = 0, imageLength = 0, alphaOffset = 0, alphaLength = 0;
        Guid pixelFormat = default;
        byte[] profile = [];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = data.Slice(directory + 2 + index * 12, 12);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            uint elements = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            if (tag == 0xbc01)
            {
                if (type != 1 || elements != 16)
                    throw new InvalidDataException("Invalid JPEG XR pixel format.");
                pixelFormat = new Guid(data.Slice(CheckedOffset(value, data.Length, 16), 16));
            }
            else if (tag == 0x8773)
            {
                if (type is not (1 or 7) || elements > 4 * 1024 * 1024)
                    throw new InvalidDataException("Invalid JPEG XR ICC profile.");
                profile = elements <= 4 ? entry.Slice(8, (int)elements).ToArray() :
                    data.Slice(CheckedOffset(value, data.Length, (int)elements), (int)elements).ToArray();
            }
            else if (tag is 0xbc02 or 0xbc80 or 0xbc81 or 0xbcc0 or 0xbcc1 or 0xbcc2 or 0xbcc3)
            {
                if (elements != 1 || type is not (1 or 3 or 4))
                    throw new InvalidDataException("Invalid JPEG XR image directory value.");
                value = type == 1 ? entry[8] : type == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]) : value;
                switch (tag)
                {
                    case 0xbc02: orientation = (int)value; break;
                    case 0xbc80: width = CheckedDimension(value); break;
                    case 0xbc81: height = CheckedDimension(value); break;
                    case 0xbcc0: imageOffset = value; break;
                    case 0xbcc1: imageLength = value; break;
                    case 0xbcc2: alphaOffset = value; break;
                    case 0xbcc3: alphaLength = value; break;
                }
            }
        }

        if (width == 0 || height == 0 || pixelFormat == default || orientation is < 0 or > 7 ||
            imageLength == 0 || imageLength > int.MaxValue || alphaLength > int.MaxValue ||
            (alphaOffset == 0) != (alphaLength == 0))
        {
            throw new InvalidDataException("Incomplete JPEG XR image directory.");
        }

        int imageStart = CheckedOffset(imageOffset, data.Length, (int)imageLength);
        if (alphaLength != 0 && alphaOffset <= data.Length && alphaLength > data.Length - alphaOffset &&
            alphaLength >= alphaOffset && alphaLength <= data.Length)
        {
            alphaLength -= alphaOffset;
        }
        int alphaStart = CheckedOffset(alphaOffset, data.Length, (int)alphaLength);
        return new JxrContainer(new JxrImageInfo(width, height, pixelFormat, orientation, profile),
            imageStart, (int)imageLength, alphaStart, (int)alphaLength);
    }

    private static int CheckedDimension(uint value) => value is > 0 and <= int.MaxValue ? (int)value :
        throw new InvalidDataException("Invalid JPEG XR image dimensions.");

    private static int CheckedOffset(uint value, int length, int count) => value <= length && count <= length - value ?
        (int)value : throw new InvalidDataException("JPEG XR data extends beyond the input.");
}