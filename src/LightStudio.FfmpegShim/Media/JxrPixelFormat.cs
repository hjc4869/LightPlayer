using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.FfmpegShim.Media;

public enum JxrColorModel
{
    Gray = 0,
    Yuv420 = 1,
    Yuv422 = 2,
    Yuv444 = 3,
    Cmyk = 4,
    Multichannel = 6,
    Rgb = 7,
    Rgbe = 8,
}

public enum JxrSampleType
{
    Bit1 = 0,
    Unsigned8 = 1,
    Unsigned16 = 2,
    Fixed16 = 3,
    Float16 = 4,
    Unsigned32 = 5,
    Fixed32 = 6,
    Float32 = 7,
    Packed555 = 8,
    Packed101010 = 9,
    Packed565 = 10,
}

public sealed partial record JxrPixelFormat(
    Guid Id, string Name, int Channels, JxrColorModel ColorModel, JxrSampleType SampleType,
    int BitsPerUnit, bool HasAlpha = false, bool IsPremultiplied = false, bool IsBgr = false)
{
    static JxrPixelFormat() => SupportedFormats = Array.AsReadOnly(Formats);

    public static IReadOnlyList<JxrPixelFormat> SupportedFormats { get; }

    public bool IsLinear => SampleType is JxrSampleType.Float16 or JxrSampleType.Float32 or
        JxrSampleType.Fixed16 or JxrSampleType.Fixed32 || ColorModel == JxrColorModel.Rgbe;

    public static JxrPixelFormat FromId(Guid id)
    {
        foreach (JxrPixelFormat format in Formats)
        {
            if (format.Id == id)
                return format;
        }
        throw new InvalidDataException($"Unsupported JPEG XR pixel format: {id}.");
    }
}