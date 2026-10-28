using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
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

    /// <summary>
    /// Converts a region of CPU-backed frame pixels to linear BT.2020 light with straight alpha and
    /// without tone mapping. Absolute-luminance sources produce nits; others put SDR white at 1.
    /// </summary>
    /// <param name="target">Single-float RGBA destination of the region's first pixel.</param>
    /// <param name="targetStride">Distance between destination rows, in float components.</param>
    public static void RenderScene(VideoFrame frame, int left, int top, int width, int height,
        IntPtr target, int targetStride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Data == IntPtr.Zero || frame.Surface.IsValid || frame.Format == VideoPixelFormat.ExternalTexture)
            throw new ArgumentException("Scene conversion requires CPU-accessible pixels.", nameof(frame));
        if (left < 0 || top < 0 || width <= 0 || height <= 0 || left > frame.Width - width ||
            top > frame.Height - height || targetStride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(width), "The region does not fit the frame or the target.");

        var color = new VideoCpuColorPipeline(frame, sceneLight: true);
        VideoColorPrimaries primaries = frame.DolbyVision.IsPresent || frame.PhotoColor is not null
            ? VideoColorPrimaries.Bt2020 : frame.ColorPrimaries;
        float[] gamut = primaries == VideoColorPrimaries.Bt2020 ? null :
            Array.ConvertAll(VideoColorGamut.Matrix(primaries, VideoColorPrimaries.Bt2020), value => (float)value);
        VideoFrame enhancement = frame.DolbyVision.EnhancementLayerResidual ? frame.EnhancementLayer : null;
        int workers = OperatingSystem.IsBrowser() ? 1 : Math.Clamp((int)((long)width * height / 262144), 1,
            Math.Min(Environment.ProcessorCount, height));
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            worker =>
            {
                float[] scratch = ArrayPool<float>.Shared.Rent(width * 7);
                Span<float> r = scratch.AsSpan(0, width), g = scratch.AsSpan(width, width), b = scratch.AsSpan(width * 2, width),
                    alpha = scratch.AsSpan(width * 3, width), er = scratch.AsSpan(width * 4, width),
                    eg = scratch.AsSpan(width * 5, width), eb = scratch.AsSpan(width * 6, width);
                int first = (int)((long)height * worker / workers), last = (int)((long)height * (worker + 1) / workers);
                for (int row = first; row < last; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int y = top + row;
                    VideoCpuPreview.SampleRow(frame, left, y, r, g, b, alpha);
                    if (enhancement is not null)
                    {
                        for (int x = 0; x < width; x++)
                            VideoCpuPreview.Sample(enhancement, (left + x) * MathF.Min((float)enhancement.Width / frame.Width, 1),
                                (y + 0.5f) * MathF.Min((float)enhancement.Height / frame.Height, 1) - 0.5f,
                                out er[x], out eg[x], out eb[x], out _);
                    }
                    color.Decode(r, g, b, er, eg, eb);
                    float* output = (float*)target + (long)row * targetStride;
                    for (int x = 0; x < width; x++)
                    {
                        float red = r[x], green = g[x], blue = b[x];
                        if (gamut is not null)
                        {
                            red = gamut[0] * r[x] + gamut[1] * g[x] + gamut[2] * b[x];
                            green = gamut[3] * r[x] + gamut[4] * g[x] + gamut[5] * b[x];
                            blue = gamut[6] * r[x] + gamut[7] * g[x] + gamut[8] * b[x];
                        }
                        output[x * 4] = red;
                        output[x * 4 + 1] = green;
                        output[x * 4 + 2] = blue;
                        output[x * 4 + 3] = Math.Clamp(alpha[x], 0, 1);
                    }
                }

                ArrayPool<float>.Shared.Return(scratch);
            });
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
