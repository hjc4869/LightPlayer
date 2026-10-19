using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Software;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

internal sealed unsafe partial class IccImage : IDisposable
{
    private const string Library = "liblcms2";
    private const uint RgbSignature = 0x52474220;
    private const uint GraySignature = 0x47524159;
    private const uint Rgba16 = (4 << 16) | (1 << 7) | (3 << 3) | 2;
    private const uint GrayAlpha16 = (3 << 16) | (1 << 7) | (1 << 3) | 2;
    private const uint CopyAlphaAndBlackPointCompensation = 0x04002000;

    private AVFrame* frame;
    private IntPtr context;
    private IntPtr sourceProfile;
    private IntPtr targetProfile;
    private IntPtr transform;
    private GCHandle errorState;
    private string error;

    public AVFrame* Frame => frame;

    private IccImage() { }

    public static IccImage TryCreate(
        AVFrame* input, AVCodecParameters* parameters, bool wideGamut, CancellationToken cancellationToken,
        int width = 0, int height = 0, bool associatedAlpha = false)
    {
        AVFrameSideData* profile = input == null ? null :
            av_frame_get_side_data(input, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
        if (profile == null || profile->data == null || profile->size == 0)
            return null;
        if (profile->size > 4 * 1024 * 1024)
            throw new InvalidDataException("The image ICC profile exceeds the 4 MiB limit.");

        AVColorTransferCharacteristic transfer = input->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
            ? parameters->color_trc : input->color_trc;
        if (transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            return null;
        if (VideoFrameMetadata.TryReadIccCicp(input, out _, out transfer) &&
            transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            return null;
        if (PqIccProfile.IsPq(new ReadOnlySpan<byte>(profile->data, (int)profile->size)))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var image = new IccImage();
        try
        {
            image.errorState = GCHandle.Alloc(image);
            image.context = cmsCreateContext(IntPtr.Zero, GCHandle.ToIntPtr(image.errorState));
            image.Require(image.context, "create context");
            cmsSetLogErrorHandlerTHR(image.context, &OnError);
            image.sourceProfile = cmsOpenProfileFromMemTHR(image.context, profile->data, (uint)profile->size);
            image.Require(image.sourceProfile, "open image profile");
            uint sourceSpace = cmsGetColorSpace(image.sourceProfile);
            if (sourceSpace is not (RgbSignature or GraySignature))
                throw new NotSupportedException($"ICC color space 0x{sourceSpace:X8} is not compatible with the decoded RGB pixels.");

            image.targetProfile = wideGamut ? image.CreateLinearBt2020() : cmsCreate_sRGBProfileTHR(image.context);
            image.Require(image.targetProfile, "create output profile");
            image.transform = cmsCreateTransformTHR(image.context,
                image.sourceProfile, sourceSpace == GraySignature ? GrayAlpha16 : Rgba16,
                image.targetProfile, Rgba16, 1, CopyAlphaAndBlackPointCompensation);
            image.Require(image.transform, "create color transform");

            image.frame = DecodeRgb(input, parameters, width, height);
            bool associated = StillImage.IsAlphaPremultiplied(input, associatedAlpha);
            ushort[] rgbaRow = new ushort[checked(image.frame->width * 4)];
            ushort[] grayRow = sourceSpace == GraySignature ? new ushort[checked(image.frame->width * 2)] : null;
            fixed (ushort* pixels = rgbaRow)
            for (int row = 0; row < image.frame->height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var green = (ushort*)(image.frame->data[0] + (long)row * image.frame->linesize[0]);
                var blue = (ushort*)(image.frame->data[1] + (long)row * image.frame->linesize[1]);
                var red = (ushort*)(image.frame->data[2] + (long)row * image.frame->linesize[2]);
                var alphaPlane = (ushort*)(image.frame->data[3] + (long)row * image.frame->linesize[3]);
                for (int column = 0; column < image.frame->width; column++)
                {
                    pixels[column * 4] = red[column];
                    pixels[column * 4 + 1] = green[column];
                    pixels[column * 4 + 2] = blue[column];
                    pixels[column * 4 + 3] = alphaPlane[column];
                    if (associated)
                    {
                        uint alpha = alphaPlane[column];
                        for (int channel = 0; channel < 3; channel++)
                            pixels[column * 4 + channel] = alpha == 0 ? (ushort)0 :
                                (ushort)Math.Min(65535u, ((uint)pixels[column * 4 + channel] * 65535 + alpha / 2) / alpha);
                    }
                }

                image.error = null;
                if (grayRow != null)
                {
                    for (int column = 0; column < image.frame->width; column++)
                    {
                        grayRow[column * 2] = pixels[column * 4];
                        grayRow[column * 2 + 1] = pixels[column * 4 + 3];
                    }
                    fixed (ushort* gray = grayRow)
                        cmsDoTransform(image.transform, gray, pixels, (uint)image.frame->width);
                }
                else
                {
                    cmsDoTransform(image.transform, pixels, pixels, (uint)image.frame->width);
                }

                if (image.error is not null)
                    throw new InvalidDataException($"LittleCMS could not transform image row {row}: {image.error}.");

                for (int column = 0; column < image.frame->width; column++)
                {
                    ushort* pixel = pixels + column * 4;
                    red[column] = pixel[0];
                    green[column] = pixel[1];
                    blue[column] = pixel[2];
                    alphaPlane[column] = pixel[3];
                }
            }

            image.frame->color_primaries = wideGamut ? AVColorPrimaries.AVCOL_PRI_BT2020 : AVColorPrimaries.AVCOL_PRI_BT709;
            image.frame->color_trc = wideGamut ? AVColorTransferCharacteristic.AVCOL_TRC_LINEAR : AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
            image.frame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            image.frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal static AVFrame* DecodeRgb(AVFrame* input, AVCodecParameters* parameters, int width = 0, int height = 0)
    {
        AVFrame* frame = av_frame_alloc();
        if (frame == null)
            throw new OutOfMemoryException("Could not allocate an ICC image frame.");
        SwsContext* scaler = null;
        try
        {
            frame->width = width > 0 ? width : input->width;
            frame->height = height > 0 ? height : input->height;
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_GBRAP16LE;
            CheckFfmpeg(av_frame_get_buffer(frame, 0), "allocate RGB buffer");

            scaler = sws_getContext(input->width, input->height, (AVPixelFormat)input->format,
                frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_GBRAP16LE, 2, null, null, null);
            if (scaler == null)
                throw new InvalidDataException("FFmpeg cannot convert this image to RGB for ICC processing.");
            AVColorSpace space = input->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED ? parameters->color_space : input->colorspace;
            int coefficients = VideoFrameMetadata.MapColorSpace(space, input->height) switch
            {
                VideoColorSpace.Bt601 => 5,
                VideoColorSpace.Bt2020 => 9,
                _ => 1,
            };
            AVColorRange range = input->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? parameters->color_range : input->color_range;
            int fullRange = range == AVColorRange.AVCOL_RANGE_JPEG ||
                VideoFrameMetadata.IsFullRangeFormat((AVPixelFormat)input->format) ? 1 : 0;
            CheckFfmpeg(sws_setColorspaceDetails(scaler,
                sws_getCoefficients(coefficients), fullRange,
                sws_getCoefficients(1), 1, 0, 1 << 16, 1 << 16), "configure RGB conversion");

            byte** sourceData = stackalloc byte*[4] { input->data[0], input->data[1], input->data[2], input->data[3] };
            int* sourceStrides = stackalloc int[4] { input->linesize[0], input->linesize[1], input->linesize[2], input->linesize[3] };
            byte** targetData = stackalloc byte*[4] { frame->data[0], frame->data[1], frame->data[2], frame->data[3] };
            int* targetStrides = stackalloc int[4] { frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3] };
            int converted = sws_scale(scaler, sourceData, sourceStrides, 0, input->height, targetData, targetStrides);
            if (converted != frame->height)
                throw new InvalidDataException($"FFmpeg converted {converted} of {frame->height} ICC image rows.");
            return frame;
        }
        catch
        {
            av_frame_free(&frame);
            throw;
        }
        finally
        {
            sws_freeContext(scaler);
        }
    }

    private IntPtr CreateLinearBt2020()
    {
        IntPtr gamma = cmsBuildGamma(context, 1);
        Require(gamma, "create linear transfer curve");
        try
        {
            var white = new Chromaticity(0.3127, 0.3290);
            var primaries = new Primaries(new(0.708, 0.292), new(0.170, 0.797), new(0.131, 0.046));
            IntPtr* curves = stackalloc IntPtr[3] { gamma, gamma, gamma };
            return cmsCreateRGBProfileTHR(context, &white, &primaries, curves);
        }
        finally
        {
            cmsFreeToneCurve(gamma);
        }
    }

    private static void CheckFfmpeg(int error, string operation)
    {
        if (error < 0)
            throw new FfmpegException($"Could not {operation}: {FfmpegError.Describe(error)}", error);
    }

    private void Require(IntPtr value, string operation)
    {
        if (value == IntPtr.Zero)
            throw new InvalidDataException($"LittleCMS could not {operation}: {error ?? "no native error detail"}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(IntPtr context, uint code, IntPtr message)
    {
        var image = (IccImage)GCHandle.FromIntPtr(cmsGetContextUserData(context)).Target;
        image.error = $"{code}: {Marshal.PtrToStringUTF8(message)}";
    }

    public void Dispose()
    {
        AVFrame* outgoing = frame;
        frame = null;
        if (outgoing != null)
            av_frame_free(&outgoing);
        if (transform != IntPtr.Zero)
            cmsDeleteTransform(transform);
        if (targetProfile != IntPtr.Zero)
            cmsCloseProfile(targetProfile);
        if (sourceProfile != IntPtr.Zero)
            cmsCloseProfile(sourceProfile);
        if (context != IntPtr.Zero)
            cmsDeleteContext(context);
        if (errorState.IsAllocated)
            errorState.Free();
        transform = targetProfile = sourceProfile = context = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Chromaticity(double horizontal, double vertical)
    {
        private readonly double horizontal = horizontal;
        private readonly double vertical = vertical;
        private readonly double luminance = 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Primaries(Chromaticity red, Chromaticity green, Chromaticity blue)
    {
        private readonly Chromaticity red = red;
        private readonly Chromaticity green = green;
        private readonly Chromaticity blue = blue;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsCreateContext(IntPtr plugins, IntPtr userData);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsDeleteContext(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsGetContextUserData(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsSetLogErrorHandlerTHR(IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, void> handler);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsOpenProfileFromMemTHR(IntPtr context, byte* data, uint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint cmsGetColorSpace(IntPtr profile);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsCreate_sRGBProfileTHR(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsBuildGamma(IntPtr context, double gamma);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsFreeToneCurve(IntPtr curve);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsCreateRGBProfileTHR(IntPtr context, Chromaticity* white, Primaries* primaries, IntPtr* curves);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cmsCreateTransformTHR(IntPtr context, IntPtr source, uint inputFormat, IntPtr target, uint outputFormat, uint intent, uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsDoTransform(IntPtr transform, void* input, void* output, uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsDeleteTransform(IntPtr transform);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int cmsCloseProfile(IntPtr profile);
}