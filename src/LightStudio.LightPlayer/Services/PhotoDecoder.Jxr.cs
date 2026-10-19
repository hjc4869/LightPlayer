using System;
using System.IO;
using System.Numerics;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static JxrImage? ReadJxr(Stream stream, CancellationToken cancellationToken, int reduction)
    {
        long origin = stream.Position;
        Span<byte> signature = stackalloc byte[4];
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = origin;
        return read == 4 && JxrDecoder.IsJxr(signature) ? JxrDecoder.Decode(stream, reduction, cancellationToken) : null;
    }

    internal static VideoFrame? DecodeJxr(Stream stream, CancellationToken cancellationToken, int reduction = 1)
    {
        JxrImage? image = ReadJxr(stream, cancellationToken, reduction);
        return image is null ? null : CreateJxrFrame(image, cancellationToken);
    }

    private static VideoFrame CreateJxrFrame(JxrImage image, CancellationToken cancellationToken)
    {
        bool pq = IsJxrPqProfile(image.Info.IccProfile);
        bool hdr = pq || image.PixelFormat.IsLinear;
        using SKColorSpace sourceSpace = CreateJxrColorSpace(image, pq);
        using SKColorSpace linearSpace = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Rec2020);
        float[]? pqMatrix = pq ? SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(),
            ReadJxrIccGamut(image.Info.IccProfile) ?? SKColorSpaceXyz.Rec2020).Values : null;
        VideoFrame frame = CreateJxrTarget(image, hdr);
        float[] peaks = new float[image.Height];
        try
        {
            ForRows(image.Width, image.Height, cancellationToken, (firstRow, lastRow) =>
            {
                var source = new Vector4[image.Width];
                var converted = new Vector4[image.Width];
                fixed (Vector4* input = source)
                fixed (Vector4* output = converted)
                {
                    var inputInfo = new SKImageInfo(image.Width, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul, sourceSpace);
                    var outputInfo = new SKImageInfo(image.Width, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul, linearSpace);
                    using var pixmap = new SKPixmap(inputInfo, (IntPtr)input, image.Width * 16);
                    for (int row = firstRow; row < lastRow; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        image.CopyRgbaRow(row, source);
                        if (!pq && !pixmap.ReadPixels(outputInfo, (IntPtr)output, image.Width * 16))
                            throw new InvalidDataException("JPEG XR color conversion failed.");
                        peaks[row] = StoreJxrRow(frame, row, pq ? source : converted, hdr, pqMatrix);
                    }
                }
            });
            foreach (float peak in peaks)
                frame.HdrPeakLuminance = Math.Max(frame.HdrPeakLuminance, peak);
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }

    private static SKColorSpace CreateJxrColorSpace(JxrImage image, bool pq)
    {
        SKColorSpace? profile = image.Info.IccProfile.Length == 0 ? null : SKColorSpace.CreateIcc(image.Info.IccProfile);
        if (pq)
        {
            SKColorSpaceXyz gamut = ReadJxrIccGamut(image.Info.IccProfile) ?? SKColorSpaceXyz.Rec2020;
            profile?.Dispose();
            return SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Pq, gamut);
        }
        if (profile is not null)
            return profile;
        if (image.Info.IccProfile.Length != 0)
            throw new InvalidDataException("Unsupported JPEG XR ICC color profile.");
        return image.PixelFormat.IsLinear ? SKColorSpace.CreateSrgbLinear() : SKColorSpace.CreateSrgb();
    }

    private static SKColorSpaceXyz? ReadJxrIccGamut(ReadOnlySpan<byte> profile) =>
        ReadJxrIccMatrix(profile) is { } matrix ? new SKColorSpaceXyz(matrix) : null;
}