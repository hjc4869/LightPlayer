using System;
using System.IO;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

public static unsafe partial class StillImage
{
    internal static bool IsAlphaPremultiplied(AVFrame* frame, bool fallback = false)
    {
        AVAlphaMode mode = frame->alpha_mode;
        return mode == AVAlphaMode.AVALPHA_MODE_PREMULTIPLIED || mode == AVAlphaMode.AVALPHA_MODE_UNSPECIFIED && fallback;
    }

    private static bool IsAssociatedTiff(Stream stream)
    {
        byte[] metadata = ExifBlock.Read(stream, out bool standalone);
        return standalone && ExifOrientation.TryReadShort(metadata, 0x0152, out int value) && value == 1;
    }

    private static void UnpremultiplyPhoto(VideoFrame frame)
    {
        for (int row = 0; row < frame.Height; row++)
        {
            byte* pixels = (byte*)frame.Data + (long)row * frame.Stride;
            if (frame.Format == VideoPixelFormat.Rgba64)
            {
                var samples = (ushort*)pixels;
                for (int column = 0; column < frame.Width; column++)
                {
                    uint alpha = samples[column * 4 + 3];
                    for (int channel = 0; channel < 3; channel++)
                        samples[column * 4 + channel] = alpha == 0 ? (ushort)0 :
                            (ushort)Math.Min(65535u, ((uint)samples[column * 4 + channel] * 65535 + alpha / 2) / alpha);
                }
            }
            else
            {
                for (int column = 0; column < frame.Width; column++)
                {
                    int alpha = pixels[column * 4 + 3];
                    for (int channel = 0; channel < 3; channel++)
                        pixels[column * 4 + channel] = alpha == 0 ? (byte)0 :
                            (byte)Math.Min(255, (pixels[column * 4 + channel] * 255 + alpha / 2) / alpha);
                }
            }
        }
    }

    private static AVFrame* CombinePhotoAlpha(AVFrame* color, AVCodecParameters* colorParameters,
        AVFrame* alpha, AVCodecParameters* alphaParameters, bool premultiplied)
    {
        if (alpha->width == colorParameters->width && alpha->height == colorParameters->height &&
            TryReadCrop(colorParameters, alpha->width, alpha->height, out var crop))
        {
            alpha->crop_top = crop.Top;
            alpha->crop_bottom = crop.Bottom;
            alpha->crop_left = crop.Left;
            alpha->crop_right = crop.Right;
            int cropped = av_frame_apply_cropping(alpha, FrameCropUnaligned);
            if (cropped < 0)
                throw new FfmpegException($"Could not crop photo alpha: {FfmpegError.Describe(cropped)}", cropped);
        }
        AVFrame* pixels = IccImage.DecodeRgb(color, colorParameters);
        SwsContext* scaler = null;
        try
        {
            int copied = av_frame_copy_props(pixels, color);
            if (copied < 0)
                throw new FfmpegException($"Could not copy photo color properties: {FfmpegError.Describe(copied)}", copied);
            pixels->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            pixels->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            scaler = sws_getContext(alpha->width, alpha->height, (AVPixelFormat)alpha->format,
                pixels->width, pixels->height, AVPixelFormat.AV_PIX_FMT_GRAY16LE, SwsBilinear, null, null, null);
            if (scaler == null)
                throw new InvalidDataException("The photo alpha image cannot be converted.");
            AVColorRange range = alpha->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
                ? alphaParameters->color_range : alpha->color_range;
            int configured = sws_setColorspaceDetails(scaler,
                sws_getCoefficients(SwsColorspaceBt709), range == AVColorRange.AVCOL_RANGE_MPEG ? 0 : 1,
                sws_getCoefficients(SwsColorspaceBt709), 1, 0, 1 << 16, 1 << 16);
            if (configured < 0)
                throw new FfmpegException($"Could not configure photo alpha conversion: {FfmpegError.Describe(configured)}", configured);
            byte** sourceData = stackalloc byte*[4] { alpha->data[0], alpha->data[1], alpha->data[2], alpha->data[3] };
            int* sourceStrides = stackalloc int[4] { alpha->linesize[0], alpha->linesize[1], alpha->linesize[2], alpha->linesize[3] };
            byte** targetData = stackalloc byte*[4] { pixels->data[3], null, null, null };
            int* targetStrides = stackalloc int[4] { pixels->linesize[3], 0, 0, 0 };
            if (sws_scale(scaler, sourceData, sourceStrides, 0, alpha->height, targetData, targetStrides) != pixels->height)
                throw new InvalidDataException("The photo alpha image is incomplete.");
            if (premultiplied)
            {
                for (int row = 0; row < pixels->height; row++)
                {
                    var opacity = (ushort*)(pixels->data[3] + (long)row * pixels->linesize[3]);
                    for (uint channel = 0; channel < 3; channel++)
                    {
                        var samples = (ushort*)(pixels->data[channel] + (long)row * pixels->linesize[channel]);
                        for (int column = 0; column < pixels->width; column++)
                            samples[column] = opacity[column] == 0 ? (ushort)0 :
                                (ushort)Math.Min(65535u, ((uint)samples[column] * 65535 + opacity[column] / 2u) / opacity[column]);
                    }
                }
            }
            pixels->alpha_mode = AVAlphaMode.AVALPHA_MODE_STRAIGHT;
            return pixels;
        }
        catch { av_frame_free(&pixels); throw; }
        finally { sws_freeContext(scaler); }
    }
}