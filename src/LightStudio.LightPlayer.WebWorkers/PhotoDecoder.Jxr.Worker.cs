using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

// JPEG XR pictures in the media worker. They are stored as the desktop stores them, with LittleCMS in place of
// Skia for the color conversion.
internal static unsafe partial class PhotoDecoder
{
    private const string LittleCms = "liblcms2";

    // BT.2020 primaries in the D50 connection space of ICC colorant tags, row-major.
    private static readonly float[] Bt2020ToXyz =
    [
        0.673459f, 0.165661f, 0.125100f,
        0.279033f, 0.675338f, 0.0456288f,
        -0.00193139f, 0.0299794f, 0.797162f,
    ];

    internal static bool IsJxr(Stream stream)
    {
        long origin = stream.Position;
        Span<byte> signature = stackalloc byte[4];
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = origin;
        return read == signature.Length && JxrDecoder.IsJxr(signature);
    }

    /// <summary>Decodes a JPEG XR picture into full-range BT.2020, PQ for HDR and linear otherwise.</summary>
    internal static VideoFrame DecodeJxrPicture(Stream stream, CancellationToken cancellationToken, int reduction = 1,
        bool preserveAlpha = true)
    {
        using JxrImage image = JxrDecoder.DecodeNative(stream, reduction, cancellationToken);
        bool pq = IsJxrPqProfile(image.Info.IccProfile);
        bool hdr = pq || image.PixelFormat.IsLinear;
        float[]? pqMatrix = pq ? Multiply(Invert(Bt2020ToXyz), ReadJxrIccMatrix(image.Info.IccProfile) ?? Bt2020ToXyz) : null;
        using JxrColorTransform? transform = pq ? null : new JxrColorTransform(image);
        VideoFrame frame = CreateJxrTarget(image, hdr, preserveAlpha);
        try
        {
            var source = new Vector4[image.Width];
            var converted = new Vector4[image.Width];
            for (int row = 0; row < image.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.CopyRgbaRow(row, source);
                transform?.Convert(source, converted);
                float peak = StoreJxrRow(frame, row, pq ? source : converted, hdr, pqMatrix);
                frame.HdrPeakLuminance = Math.Max(frame.HdrPeakLuminance, peak);
            }
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }

    /// <summary>Decodes an upright sRGB thumbnail of a JPEG XR picture, with HDR light brought into SDR.</summary>
    internal static NativeImageThumbnail DecodeJxrThumbnail(Stream stream, int maxDimension, CancellationToken cancellationToken)
    {
        VideoFrame frame = DecodeJxrPicture(stream, cancellationToken, preserveAlpha: false);
        try
        {
            return StillImage.RenderThumbnail(frame, maxDimension, cancellationToken);
        }
        finally
        {
            frame.Release();
        }
    }

    private static float[] Multiply(float[] left, float[] right)
    {
        float[] product = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                product[row * 3 + column] = left[row * 3] * right[column] + left[row * 3 + 1] * right[3 + column] +
                    left[row * 3 + 2] * right[6 + column];
            }
        }
        return product;
    }

    private static float[] Invert(float[] matrix)
    {
        float a = matrix[0], b = matrix[1], c = matrix[2], d = matrix[3], e = matrix[4], f = matrix[5];
        float g = matrix[6], h = matrix[7], i = matrix[8];
        float determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / determinant, (c * h - b * i) / determinant, (b * f - c * e) / determinant,
            (f * g - d * i) / determinant, (a * i - c * g) / determinant, (c * d - a * f) / determinant,
            (d * h - e * g) / determinant, (b * g - a * h) / determinant, (a * e - b * d) / determinant,
        ];
    }

    // Converts JPEG XR pixels from their color space to linear BT.2020.
    private sealed class JxrColorTransform : IDisposable
    {
        private const uint RgbaFloat = (1u << 22) | (4u << 16) | (1u << 7) | (3u << 3) | 4u;
        // Gray read from the first of the four floats CopyRgbaRow writes per pixel.
        private const uint GrayFloat = (1u << 22) | (3u << 16) | (3u << 7) | (1u << 3) | 4u;
        private const uint RgbSpace = 0x52474220;
        private const uint GraySpace = 0x47524159;
        private const uint RelativeColorimetric = 1;
        private const uint NoOptimize = 0x0100;

        private readonly IntPtr context;
        private IntPtr source;
        private IntPtr target;
        private IntPtr transform;

        public JxrColorTransform(JxrImage image)
        {
            context = cmsCreateContext(IntPtr.Zero, IntPtr.Zero);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException("LittleCMS could not create a context.");
            try
            {
                byte[] profile = image.Info.IccProfile;
                if (profile.Length != 0)
                {
                    fixed (byte* data = profile)
                        source = cmsOpenProfileFromMemTHR(context, data, (uint)profile.Length);
                }
                else
                {
                    source = image.PixelFormat.IsLinear ? LinearProfile(0.64, 0.33, 0.30, 0.60, 0.15, 0.06)
                        : cmsCreate_sRGBProfileTHR(context);
                }
                uint space = source == IntPtr.Zero ? 0 : cmsGetColorSpace(source);
                if (space is not (RgbSpace or GraySpace))
                    throw new InvalidDataException("Unsupported JPEG XR ICC color profile.");
                target = LinearProfile(0.708, 0.292, 0.170, 0.797, 0.131, 0.046);
                transform = target == IntPtr.Zero ? IntPtr.Zero : cmsCreateTransformTHR(context, source,
                    space == GraySpace ? GrayFloat : RgbaFloat, target, RgbaFloat, RelativeColorimetric, NoOptimize);
                if (transform == IntPtr.Zero)
                    throw new InvalidDataException("Unsupported JPEG XR ICC color profile.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Convert(Vector4[] pixels, Vector4[] converted)
        {
            fixed (Vector4* input = pixels)
            fixed (Vector4* output = converted)
                cmsDoTransform(transform, input, output, (uint)pixels.Length);
            for (int column = 0; column < pixels.Length; column++)
                converted[column].W = pixels[column].W;
        }

        public void Dispose()
        {
            if (transform != IntPtr.Zero)
                cmsDeleteTransform(transform);
            if (target != IntPtr.Zero)
                cmsCloseProfile(target);
            if (source != IntPtr.Zero)
                cmsCloseProfile(source);
            transform = target = source = IntPtr.Zero;
            cmsDeleteContext(context);
        }

        private IntPtr LinearProfile(double redX, double redY, double greenX, double greenY, double blueX, double blueY)
        {
            IntPtr gamma = cmsBuildGamma(context, 1);
            if (gamma == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                var white = new Chromaticity(0.3127, 0.3290);
                var primaries = new Primaries(new Chromaticity(redX, redY), new Chromaticity(greenX, greenY),
                    new Chromaticity(blueX, blueY));
                IntPtr* curves = stackalloc IntPtr[] { gamma, gamma, gamma };
                return cmsCreateRGBProfileTHR(context, &white, &primaries, curves);
            }
            finally
            {
                cmsFreeToneCurve(gamma);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Chromaticity(double x, double y)
        {
            private readonly double x = x;
            private readonly double y = y;
            private readonly double luminance = 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Primaries(Chromaticity red, Chromaticity green, Chromaticity blue)
        {
            private readonly Chromaticity red = red;
            private readonly Chromaticity green = green;
            private readonly Chromaticity blue = blue;
        }

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsCreateContext(IntPtr plugins, IntPtr userData);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern void cmsDeleteContext(IntPtr context);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsOpenProfileFromMemTHR(IntPtr context, byte* data, uint length);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint cmsGetColorSpace(IntPtr profile);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsCreate_sRGBProfileTHR(IntPtr context);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsBuildGamma(IntPtr context, double gamma);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern void cmsFreeToneCurve(IntPtr curve);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsCreateRGBProfileTHR(IntPtr context, Chromaticity* white, Primaries* primaries, IntPtr* curves);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cmsCreateTransformTHR(IntPtr context, IntPtr source, uint inputFormat, IntPtr target,
            uint outputFormat, uint intent, uint flags);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern void cmsDoTransform(IntPtr transform, void* input, void* output, uint count);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern void cmsDeleteTransform(IntPtr transform);

        [DllImport(LittleCms, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cmsCloseProfile(IntPtr profile);
    }
}
