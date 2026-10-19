using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Software;
using LightStudio.FfmpegShim.Core;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

public static unsafe partial class StillImage
{
    /// <summary>Reduces CPU-backed frame pixels to an upright 200-nit, 1x-headroom sRGB preview.</summary>
    public static NativeImageThumbnail RenderThumbnail(VideoFrame frame, int maxDimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDimension, 0);
        ComputeSize(frame.Width, frame.Height, av_d2q(frame.PixelAspectRatio, int.MaxValue),
            maxDimension, out int width, out int height);
        NativeBuffer pixels = RenderSoftwarePixels(frame, width, height, cancellationToken);
        return Orient(pixels, width, height, frame.Rotation, frame.MirrorHorizontally);
    }

    internal static NativeBuffer RenderSoftwarePixels(VideoFrame frame, int width, int height,
        CancellationToken cancellationToken = default)
    {
        var pixels = new NativeBuffer(checked(width * height * 4));
        try
        {
            VideoCpuPreview.Render(frame, width, height, pixels.GetSpan(), cancellationToken);
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static ReadOnlySpan<byte> ReadSoftwareProfile(AVFrame* frame)
    {
        AVFrameSideData* profile = av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
        if (profile == null || profile->data == null || profile->size == 0)
            return default;
        if (profile->size > 4 * 1024 * 1024)
            throw new InvalidDataException("The image ICC profile exceeds the 4 MiB limit.");
        return new ReadOnlySpan<byte>(profile->data, (int)profile->size);
    }

    internal static bool NeedsSoftwareColor(AVFrame* frame, AVCodecParameters* parameters) =>
        ReadTransfer(frame, parameters) is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 or AVColorTransferCharacteristic.AVCOL_TRC_LINEAR ||
        ReadPrimaries(frame, parameters) != VideoColorPrimaries.Bt709 ||
        av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA) != null ||
        av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null ||
        PqIccProfile.IsPq(ReadSoftwareProfile(frame));

    internal static VideoFrame RetainSoftwareFrame(AVFrame* input, AVCodecParameters* parameters)
    {
        VideoFrame frame = RetainNative(input, parameters);
        if (frame is null)
            throw new InvalidDataException("Could not retain pixels for software color conversion.");
        try
        {
            ApplyColor(frame, input, parameters);
            CodecParameters description = CodecParameters.FromNative(parameters, new AVRational { num = 1, den = 1 }, default);
            DolbyVisionMetadataReader.TryRead(input, description.DolbyVisionProfile,
                description.DolbyVisionLevel, description.DolbyVisionCompatibilityId, frame.DolbyVision);
            Hdr10PlusMetadataReader.TryRead(input, frame.Hdr10Plus);
            if (PqIccProfile.Read(ReadSoftwareProfile(input), frame.HasAlpha) is { } profile)
            {
                frame.PhotoColor = profile;
                frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
                frame.ColorTransfer = VideoColorTransfer.Pq;
            }
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }
}
