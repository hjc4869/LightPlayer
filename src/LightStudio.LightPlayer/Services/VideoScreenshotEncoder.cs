using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Services;

/// <summary>Encodes captured video frames as image files.</summary>
public interface IVideoScreenshotEncoder
{
    /// <summary>The file format <see cref="EncodeAsync"/> writes the snapshot in.</summary>
    Task<PhotoExportFormat> ChooseFormatAsync(VideoFrameSnapshot snapshot);

    /// <summary>Encodes the snapshot, which must stay undisposed until the returned task completes.</summary>
    Task<byte[]> EncodeAsync(VideoFrameSnapshot snapshot, PhotoExportFormat format, CancellationToken cancellationToken);
}

/// <summary>
/// Encodes on this process's thread pool: 8-bit standard dynamic range pictures as 8-bit RGB PNG, and
/// deeper or HDR pictures as lossless 16-bit RGB JPEG XL, or 16-bit PNG without a JPEG XL encoder. Files
/// are tagged with the picture's own primaries and transfer function.
/// </summary>
internal sealed class InProcessVideoScreenshotEncoder : IVideoScreenshotEncoder
{
    public async Task<PhotoExportFormat> ChooseFormatAsync(VideoFrameSnapshot snapshot)
    {
        if (!snapshot.IsHdr && snapshot.BitDepth <= 8)
            return PhotoExportFormat.Png;
        return await FindJxlEncoderAsync().ConfigureAwait(false) is not null ? PhotoExportFormat.JpegXl : PhotoExportFormat.Png;
    }

    public async Task<byte[]> EncodeAsync(VideoFrameSnapshot snapshot, PhotoExportFormat format, CancellationToken cancellationToken)
    {
        StillImageEncoderInfo? jxl = format switch
        {
            PhotoExportFormat.Png => null,
            PhotoExportFormat.JpegXl => await FindJxlEncoderAsync().ConfigureAwait(false) ??
                throw new NotSupportedException("No JPEG XL encoder is available."),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return await Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            byte[] encoded = jxl is null ? EncodePng(snapshot, cancellationToken) : EncodeJxl(snapshot, jxl, cancellationToken);
            AppLog.Information("Video.Screenshot",
                $"Encoded {format} {snapshot.Width}x{snapshot.Height} from the {snapshot.Source} frame at {snapshot.Timestamp} " +
                $"({snapshot.BitDepth}-bit, primaries={snapshot.Primaries}, transfer={snapshot.Transfer}, " +
                $"pixelAspect={snapshot.PixelAspectRatio:0.####}, encoder={jxl?.Name ?? "png"}) in " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; {encoded.Length} bytes.");
            return encoded;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StillImageEncoderInfo?> FindJxlEncoderAsync() =>
        (await StillImageEncoders.ProbeAsync().ConfigureAwait(false)).FirstOrDefault(encoder =>
            encoder.Codec == StillImageCodec.JpegXl && encoder.Supports(AVPixelFormat.AV_PIX_FMT_RGB48LE));

    private static byte[] EncodePng(VideoFrameSnapshot snapshot, CancellationToken cancellationToken)
    {
        var chunks = new List<(byte[] Type, byte[] Data)>
        {
            ("cICP"u8.ToArray(), PngWriter.Cicp(CicpPrimaries(snapshot.Primaries), CicpTransfer(snapshot.Transfer))),
        };
        if (Math.Abs(snapshot.PixelAspectRatio - 1) > 1e-4)
            chunks.Add(("pHYs"u8.ToArray(), PngWriter.PixelAspectRatio(snapshot.PixelAspectRatio)));

        bool wide = snapshot.BytesPerSample == 2;
        using var output = new MemoryStream();
        PngWriter.Write(output, snapshot.Width, snapshot.Height, wide ? 16 : 8, 3, (y, row) =>
        {
            ReadOnlySpan<byte> source = snapshot.GetRow(y);
            if (!wide)
            {
                source.CopyTo(row);
                return;
            }

            // PNG stores samples big-endian; the snapshot keeps them in native little-endian words.
            for (int index = 0; index < source.Length; index += 2)
            {
                row[index] = source[index + 1];
                row[index + 1] = source[index];
            }
        }, chunks, 6, cancellationToken);
        return output.ToArray();
    }

    private static unsafe byte[] EncodeJxl(VideoFrameSnapshot snapshot, StillImageEncoderInfo encoder, CancellationToken cancellationToken)
    {
        using var picture = new StillPicture(snapshot.Width, snapshot.Height, AVPixelFormat.AV_PIX_FMT_RGB48LE)
        {
            Primaries = (AVColorPrimaries)CicpPrimaries(snapshot.Primaries),
            Transfer = (AVColorTransferCharacteristic)CicpTransfer(snapshot.Transfer),
            Matrix = AVColorSpace.AVCOL_SPC_RGB,
            Range = AVColorRange.AVCOL_RANGE_JPEG,
        };
        byte* plane = (byte*)picture.GetPlane(0);
        int stride = picture.GetStride(0);
        for (int y = 0; y < snapshot.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> source = snapshot.GetRow(y);
            var target = new Span<ushort>(plane + (long)y * stride, snapshot.Width * 3);
            if (snapshot.BytesPerSample == 2)
            {
                MemoryMarshal.Cast<byte, ushort>(source).CopyTo(target);
            }
            else
            {
                for (int index = 0; index < target.Length; index++)
                    target[index] = (ushort)(source[index] * 257);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return StillImageEncoder.Encode(encoder, picture, new StillEncodeSettings(100, StillEncodeSpeed.Fastest));
    }

    /// <summary>ITU-T H.273 colour_primaries.</summary>
    private static int CicpPrimaries(VideoColorPrimaries primaries) => primaries switch
    {
        VideoColorPrimaries.Bt2020 => 9,
        VideoColorPrimaries.Smpte170M => 6,
        VideoColorPrimaries.Bt470Bg => 5,
        VideoColorPrimaries.Bt470M => 4,
        VideoColorPrimaries.DisplayP3 => 12,
        VideoColorPrimaries.DciP3 => 11,
        _ => 1,
    };

    /// <summary>ITU-T H.273 transfer_characteristics.</summary>
    private static int CicpTransfer(VideoColorTransfer transfer) => transfer switch
    {
        VideoColorTransfer.Srgb => 13,
        VideoColorTransfer.Linear => 8,
        VideoColorTransfer.Pq => 16,
        VideoColorTransfer.Hlg => 18,
        _ => 1,
    };
}
