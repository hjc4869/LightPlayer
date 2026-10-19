using System;
using System.IO;
using System.Threading;
using LightStudio.FfmpegShim.Core;

namespace LightStudio.FfmpegShim.Media;

public sealed partial record JxrImage(JxrImageInfo Info, JxrPixelFormat PixelFormat, int Stride, byte[] Pixels) : IDisposable
{
    public int Width => Info.Width;

    public int Height => Info.Height;

    internal NativeBuffer NativePixels { get; init; }
    public ReadOnlySpan<byte> PixelBytes => NativePixels is { } native ? native.GetSpan() : Pixels;
    public void Dispose() => NativePixels?.Dispose();
}

public static unsafe partial class JxrDecoder
{
    public static bool IsJxr(ReadOnlySpan<byte> data) => JxrContainer.HasSignature(data);

    public static JxrImageInfo Identify(ReadOnlySpan<byte> data) => JxrContainer.Read(data).Info;

    public static JxrImage Decode(Stream stream, CancellationToken cancellationToken = default) => Decode(stream, 1, cancellationToken);

    public static JxrImage Decode(Stream stream, int reduction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using var data = new MemoryStream();
        byte[] buffer = new byte[65536];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length + read > 512 * 1024 * 1024)
                throw new InvalidDataException("JPEG XR encoded size limit exceeded.");
            data.Write(buffer, 0, read);
        }
        return Decode(data.GetBuffer().AsSpan(0, (int)data.Length), reduction, cancellationToken);
    }

    public static JxrImage Decode(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default) => Decode(data, 1, cancellationToken);

    public static JxrImage Decode(ReadOnlySpan<byte> data, int reduction, CancellationToken cancellationToken = default)
        => DecodeCore(data, reduction, cancellationToken, false);

    /// <summary>Decodes a seekable source using native input and pixel storage. Dispose the returned image after use.</summary>
    public static JxrImage DecodeNative(Stream stream, int reduction = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        long length = stream.Length - stream.Position;
        if (length is < 0 or > 512 * 1024 * 1024)
            throw new InvalidDataException("JPEG XR encoded size limit exceeded.");
        using var data = new NativeBuffer((int)length);
        for (int offset = 0; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(65536, data.Length - offset);
            stream.ReadExactly(data.GetSpan().Slice(offset, count));
            offset += count;
        }
        return DecodeCore(data.GetSpan(), reduction, cancellationToken, true);
    }

    private static JxrImage DecodeCore(ReadOnlySpan<byte> data, int reduction, CancellationToken cancellationToken, bool native)
    {
        if (reduction is not (1 or 4 or 16))
            throw new ArgumentOutOfRangeException(nameof(reduction));
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > 512 * 1024 * 1024)
            throw new InvalidDataException("JPEG XR encoded size limit exceeded.");
        JxrContainer container = JxrContainer.Read(data);
        JxrPixelFormat format = JxrPixelFormat.FromId(container.Info.PixelFormat);
        if (reduction != 1 && format.ColorModel is JxrColorModel.Yuv420 or JxrColorModel.Yuv422)
            throw new NotSupportedException("Reduced JPEG XR decoding requires a full-resolution component layout.");
        int width = (container.Info.Width + reduction - 1) / reduction;
        int height = (container.Info.Height + reduction - 1) / reduction;
        if ((long)container.Info.Width * container.Info.Height > 100_000_000)
            throw new InvalidDataException("JPEG XR pixel count limit exceeded.");
        int unitWidth = format.ColorModel is JxrColorModel.Yuv420 or JxrColorModel.Yuv422 ? (width + 1) / 2 : width;
        int rows = format.ColorModel == JxrColorModel.Yuv420 ? (height + 1) / 2 : height;
        int stride = checked((int)(((long)unitWidth * format.BitsPerUnit + 7) / 8));
        long length = (long)stride * rows;
        if (length > 512 * 1024 * 1024)
            throw new InvalidDataException("JPEG XR output size limit exceeded.");
        NativeBuffer storage = native ? new NativeBuffer((int)length) : null;
        byte[] pixels = native ? [] : new byte[(int)length];
        try
        {
            Span<byte> target = storage is null ? pixels : storage.GetSpan();
            fixed (byte* source = data)
            fixed (byte* destination = target)
            {
                DecodePlane(source + container.ImageOffset, container.ImageLength, destination, stride, container.Info.Width, container.Info.Height,
                    format, false, container.AlphaLength == 0, reduction, cancellationToken);
                if (container.AlphaLength != 0 && format.HasAlpha)
                    DecodePlane(source + container.AlphaOffset, container.AlphaLength, destination, stride, container.Info.Width, container.Info.Height,
                        format, true, false, reduction, cancellationToken);
            }
            return new JxrImage(container.Info with { Width = width, Height = height }, format, stride, pixels) { NativePixels = storage };
        }
        catch
        {
            storage?.Dispose();
            throw;
        }
    }

    private static void DecodePlane(byte* data, int length, byte* pixels, int stride, int width, int height,
        JxrPixelFormat format, bool alphaOnly, bool interleavedAlpha, int reduction, CancellationToken cancellationToken)
    {
        void* stream = null;
        Check(JxrNative.CreateWS_Memory(&stream, data, (nuint)length));
        try
        {
            JxrNative.ImageInfo image = default;
            JxrNative.CodecParameters parameters = default;
            parameters.pWStream = stream;
            parameters.uAlphaMode = interleavedAlpha && format.HasAlpha ? (byte)2 : (byte)0;
            Check(JxrNative.ImageStrDecGetInfo(&image, &parameters));
            if (image.cWidth != (nuint)width || image.cHeight != (nuint)height || image.bdBitDepth != (int)format.SampleType)
                throw new InvalidDataException("JPEG XR container and codestream disagree.");
            int colorChannels = format.Channels - (format.HasAlpha ? 1 : 0);
            bool compatible = alphaOnly ? parameters.cfColorFormat == 0 : format.ColorModel switch
            {
                JxrColorModel.Gray => parameters.cfColorFormat == 0,
                JxrColorModel.Rgb or JxrColorModel.Rgbe => parameters.cfColorFormat is >= 0 and <= 4,
                JxrColorModel.Cmyk => parameters.cfColorFormat == 4,
                JxrColorModel.Multichannel => parameters.cfColorFormat == 6 && parameters.cChannel == (nuint)colorChannels,
                _ => parameters.cfColorFormat == (int)format.ColorModel,
            };
            if (!compatible || interleavedAlpha && format.HasAlpha && parameters.uAlphaMode == 0)
                throw new InvalidDataException("JPEG XR component layout does not match the container.");
            image.cfColorFormat = alphaOnly ? 0 : (int)format.ColorModel;
            image.cBitsPerUnit = (nuint)format.BitsPerUnit;
            image.bRGB = format.IsBgr ? 0 : 1;
            image.oOrientation = 0;
            image.cThumbnailWidth = image.cROIWidth = (nuint)((width + reduction - 1) / reduction);
            image.cThumbnailHeight = image.cROIHeight = (nuint)((height + reduction - 1) / reduction);
            parameters.bBlackWhite = 1;
            if (alphaOnly)
            {
                int componentBytes = format.SampleType is JxrSampleType.Unsigned8 ? 1 :
                    format.SampleType is JxrSampleType.Unsigned16 or JxrSampleType.Fixed16 or JxrSampleType.Float16 ? 2 : 4;
                image.cLeadingPadding = (nuint)(format.BitsPerUnit / 8 / componentBytes - 1);
            }
            void* decoder = null;
            Check(JxrNative.ImageStrDecInit(&image, &parameters, &decoder));
            try
            {
                JxrNative.ImageBuffer output = new()
                {
                    pv = pixels,
                    cbStride = (nuint)stride,
                    cLine = image.cThumbnailHeight,
                };
                for (uint row = 0; row <= ((uint)height + 15) / 16; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.uiFirstMBRow = row;
                    output.uiLastMBRow = row;
                    nuint decodedLines = 0;
                    Check(JxrNative.ImageStrDecDecode(decoder, &output, &decodedLines));
                }
            }
            finally
            {
                Check(JxrNative.ImageStrDecTerm(decoder));
            }
        }
        finally
        {
            Check(JxrNative.CloseWS_Memory(&stream));
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new InvalidDataException($"Invalid JPEG XR codestream ({result}).");
    }
}