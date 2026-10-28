using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;

namespace LightStudio.LightPlayer.Services.PhotoExport;

internal enum PhotoExportPrimaries
{
    Bt709,
    DisplayP3,
    Bt2020,
}

/// <summary>Primaries, transfer functions and CICP code points of the export pipeline, all with a D65 white point.</summary>
internal static class PhotoExportColor
{
    /// <summary>Diffuse white of relative HDR content, following ITU-R BT.2408.</summary>
    public const float ReferenceWhiteNits = 203;

    public const int CicpTransferSrgb = 13;
    public const int CicpTransferPq = 16;
    public const int CicpMatrixIdentity = 0;
    public const int CicpMatrixBt709 = 1;
    public const int CicpMatrixBt601 = 6;
    public const int CicpMatrixBt2020 = 9;

    private static readonly double[][] Chromaticities =
    [
        [0.640, 0.330, 0.300, 0.600, 0.150, 0.060],
        [0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
        [0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
    ];

    private static readonly double[][] ToXyz = Chromaticities.Select(RgbToXyz).ToArray();
    private static readonly double[][] FromXyz = ToXyz.Select(Invert).ToArray();

    /// <summary>Row-major 3x3 matrix converting linear RGB between two sets of primaries.</summary>
    public static float[] Matrix(PhotoExportPrimaries from, PhotoExportPrimaries to) =>
        Multiply(FromXyz[(int)to], ToXyz[(int)from]).Select(value => (float)value).ToArray();

    /// <summary>Luminance weights: the Y row of the RGB-to-XYZ matrix.</summary>
    public static (float R, float G, float B) Luma(PhotoExportPrimaries primaries)
    {
        double[] m = ToXyz[(int)primaries];
        return ((float)m[3], (float)m[4], (float)m[5]);
    }

    /// <summary>ITU-T H.273 colour_primaries.</summary>
    public static int Cicp(PhotoExportPrimaries primaries) => primaries switch
    {
        PhotoExportPrimaries.DisplayP3 => 12,
        PhotoExportPrimaries.Bt2020 => 9,
        _ => 1,
    };

    /// <summary>Relative excess beyond a gamut boundary that still counts as inside it.</summary>
    public const float GamutTolerance = 1f / 256;

    /// <summary>
    /// Row-major matrices to BT.2020 from each linear RGB space a decoded photo holds colors in: the
    /// base image's, and a gain map's alternate space. Other white points are treated as D65.
    /// </summary>
    public static float[][] SourceGamuts(VideoFrame frame)
    {
        var gamuts = new List<float[]>(2);
        if (frame.PhotoColor is { } profile)
            gamuts.Add(profile.ToBt2020.ToArray());
        else if (frame.DolbyVision.IsPresent)
            gamuts.Add(Matrix(PhotoExportPrimaries.Bt2020, PhotoExportPrimaries.Bt2020));
        else
            gamuts.Add(ToBt2020(frame.ColorPrimaries switch
            {
                VideoColorPrimaries.Bt2020 => Chromaticities[(int)PhotoExportPrimaries.Bt2020],
                VideoColorPrimaries.DisplayP3 or VideoColorPrimaries.DciP3 => Chromaticities[(int)PhotoExportPrimaries.DisplayP3],
                VideoColorPrimaries.Smpte170M => [0.630, 0.340, 0.310, 0.595, 0.155, 0.070],
                VideoColorPrimaries.Bt470Bg => [0.640, 0.330, 0.290, 0.600, 0.150, 0.060],
                VideoColorPrimaries.Bt470M => [0.670, 0.330, 0.210, 0.710, 0.140, 0.080],
                _ => Chromaticities[(int)PhotoExportPrimaries.Bt709],
            }));
        if (frame.GainMap is { } gainMap)
            gamuts.Add(gainMap.ToBt2020.ToArray());
        return gamuts.ToArray();
    }

    /// <summary>Whether colors of the source gamuts can lie outside <paramref name="target"/>.</summary>
    public static bool ExceedsGamut(IReadOnlyList<float[]> sources, PhotoExportPrimaries target)
    {
        Vector3 limits = GamutLimits(sources, target);
        return MathF.Max(limits.X, MathF.Max(limits.Y, limits.Z)) > 1 + GamutTolerance;
    }

    /// <summary>
    /// The largest distance from the neutral axis, per output channel, that colors of the source gamuts
    /// reach on <paramref name="target"/> primaries, where 1 is the target's gamut boundary.
    /// </summary>
    public static Vector3 GamutLimits(IReadOnlyList<float[]> sources, PhotoExportPrimaries target)
    {
        const int steps = 16;
        float[] fromBt2020 = Matrix(PhotoExportPrimaries.Bt2020, target);
        Vector3 limits = Vector3.Zero;
        Span<float> rgb = stackalloc float[3];
        foreach (float[] source in sources)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                for (int step = 0; step < steps; step++)
                {
                    rgb.Clear();
                    rgb[edge] = 1 - step / (float)steps;
                    rgb[(edge + 1) % 3] = step / (float)steps;
                    Vector3 color = Transform(fromBt2020, Transform(source, new Vector3(rgb[0], rgb[1], rgb[2])));
                    float neutral = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
                    if (neutral > 0)
                        limits = Vector3.Max(limits, (new Vector3(neutral) - color) / neutral);
                }
            }
        }

        return limits;
    }

    public static Vector3 Transform(float[] m, Vector3 v) => new(
        m[0] * v.X + m[1] * v.Y + m[2] * v.Z,
        m[3] * v.X + m[4] * v.Y + m[5] * v.Z,
        m[6] * v.X + m[7] * v.Y + m[8] * v.Z);

    private static float[] ToBt2020(double[] chromaticities) =>
        Multiply(FromXyz[(int)PhotoExportPrimaries.Bt2020], RgbToXyz(chromaticities)).Select(value => (float)value).ToArray();

    public static float SrgbToLinear(float encoded) => encoded <= 0.04045f
        ? encoded / 12.92f
        : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float linear) => linear <= 0.0031308f
        ? 12.92f * MathF.Max(linear, 0)
        : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;

    /// <summary>SMPTE ST 2084 inverse EOTF for an absolute luminance in nits.</summary>
    public static float NitsToPq(float nits)
    {
        const float m1 = 2610f / 16384f, m2 = 2523f / 4096f * 128f;
        const float c1 = 3424f / 4096f, c2 = 2413f / 4096f * 32f, c3 = 2392f / 4096f * 32f;
        float p = MathF.Pow(Math.Clamp(nits / 10000f, 0f, 1f), m1);
        return MathF.Pow((c1 + c2 * p) / (1f + c3 * p), m2);
    }

    public static float PqToNits(float encoded)
    {
        const float m1 = 2610f / 16384f, m2 = 2523f / 4096f * 128f;
        const float c1 = 3424f / 4096f, c2 = 2413f / 4096f * 32f, c3 = 2392f / 4096f * 32f;
        float e = MathF.Pow(Math.Clamp(encoded, 0f, 1f), 1f / m2);
        return MathF.Pow(MathF.Max(e - c1, 0f) / (c2 - c3 * e), 1f / m1) * 10000f;
    }

    /// <summary>Triangular noise of up to one quantization step, the renderer's preview dither pattern.</summary>
    public static float Dither(int x, int y)
    {
        return Noise(x, y, 0) - Noise(y + 37, x + 17, 0.61803399f);

        static float Noise(float x, float y, float seed)
        {
            float value = x * 0.06711056f + y * 0.00583715f + seed;
            value = 52.9829189f * (value - MathF.Floor(value));
            return value - MathF.Floor(value);
        }
    }

    public static ushort Quantize16(float value) => (ushort)Math.Clamp((int)(value * 65535 + 0.5f), 0, 65535);

    /// <summary>Runs a callback over bands of rows, in parallel for large images outside the browser.</summary>
    public static void ForRows(int height, int width, CancellationToken cancellationToken, Action<int, int> band)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int workers = OperatingSystem.IsBrowser() ? 1
            : Math.Clamp((int)((long)width * height / 131072), 1, Math.Min(Environment.ProcessorCount, Math.Max(1, height)));
        if (workers <= 1)
        {
            band(0, height);
            return;
        }

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            worker => band((int)((long)height * worker / workers), (int)((long)height * (worker + 1) / workers)));
    }

    private static double[] RgbToXyz(double[] p)
    {
        const double wx = 0.3127, wy = 0.3290;
        double[] columns =
        [
            p[0] / p[1], p[2] / p[3], p[4] / p[5],
            1, 1, 1,
            (1 - p[0] - p[1]) / p[1], (1 - p[2] - p[3]) / p[3], (1 - p[4] - p[5]) / p[5],
        ];
        double[] inverse = Invert(columns);
        double[] white = [wx / wy, 1, (1 - wx - wy) / wy];
        double[] scale = new double[3];
        for (int r = 0; r < 3; r++)
            scale[r] = inverse[r * 3] * white[0] + inverse[r * 3 + 1] * white[1] + inverse[r * 3 + 2] * white[2];

        var result = new double[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            result[r * 3 + c] = columns[r * 3 + c] * scale[c];
        return result;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            result[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return result;
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }
}

/// <summary>
/// Fits linear colors on the output primaries into the output gamut. SDR fitting also brings
/// components above white down to one; HDR fitting keeps them.
/// </summary>
internal sealed class PhotoExportGamutMapper
{
    private const float Threshold = 0.8f;
    private const float Power = 1.2f;
    private readonly PhotoExportGamutMapping mapping;
    private readonly (float R, float G, float B) luma;
    private readonly Vector3 scales;
    private readonly bool compresses;

    /// <param name="sources">Matrices to BT.2020 from the linear spaces the source's colors are in.</param>
    public PhotoExportGamutMapper(PhotoExportGamutMapping mapping, PhotoExportPrimaries primaries, IReadOnlyList<float[]> sources)
    {
        this.mapping = mapping;
        luma = PhotoExportColor.Luma(primaries);
        Vector3 limits = PhotoExportColor.GamutLimits(sources, primaries);
        Vector3 channelScales = Vector3.Zero;
        for (int c = 0; c < 3; c++)
        {
            if (limits[c] <= 1 + PhotoExportColor.GamutTolerance)
                continue;
            double limit = limits[c];
            channelScales[c] = (float)((limit - Threshold) /
                Math.Pow(Math.Pow((1 - Threshold) / (limit - Threshold), -Power) - 1, 1 / Power));
            compresses = true;
        }

        scales = channelScales;
    }

    /// <summary>Fits a relative SDR color into the unit cube.</summary>
    public void MapSdr(ref float r, ref float g, ref float b)
    {
        switch (mapping)
        {
            case PhotoExportGamutMapping.Clip:
                r = Math.Clamp(r, 0, 1);
                g = Math.Clamp(g, 0, 1);
                b = Math.Clamp(b, 0, 1);
                return;
            case PhotoExportGamutMapping.PreserveHue:
                ClipNegative(ref r, ref g, ref b);
                DesaturateHighlights(ref r, ref g, ref b);
                return;
            default:
                CompressCone(ref r, ref g, ref b);
                RollOffHighlights(ref r, ref g, ref b);
                return;
        }
    }

    /// <summary>Removes negative components, leaving light above white in place.</summary>
    public void MapHdr(ref float r, ref float g, ref float b)
    {
        switch (mapping)
        {
            case PhotoExportGamutMapping.Clip:
                r = MathF.Max(r, 0);
                g = MathF.Max(g, 0);
                b = MathF.Max(b, 0);
                return;
            case PhotoExportGamutMapping.PreserveHue:
                ClipNegative(ref r, ref g, ref b);
                return;
            default:
                CompressCone(ref r, ref g, ref b);
                return;
        }
    }

    /// <summary>
    /// Compresses each component's distance from the neutral axis beyond a threshold so that the source
    /// gamut's boundary lands on the output's, using the ACES reference gamut compression curve. Colors
    /// beyond the source gamut still clip.
    /// </summary>
    private void CompressCone(ref float r, ref float g, ref float b)
    {
        float neutral = MathF.Max(r, MathF.Max(g, b));
        if (!(neutral > 0))
        {
            r = g = b = 0;
            return;
        }

        if (compresses)
        {
            r = neutral - Compress((neutral - r) / neutral, scales.X) * neutral;
            g = neutral - Compress((neutral - g) / neutral, scales.Y) * neutral;
            b = neutral - Compress((neutral - b) / neutral, scales.Z) * neutral;
        }

        r = MathF.Max(r, 0);
        g = MathF.Max(g, 0);
        b = MathF.Max(b, 0);
    }

    private static float Compress(float distance, float scale)
    {
        if (distance < Threshold || scale <= 0)
            return distance;
        float normalized = (distance - Threshold) / scale;
        return Threshold + scale * normalized / MathF.Pow(1 + MathF.Pow(normalized, Power), 1 / Power);
    }

    /// <summary>Desaturates toward the color's own luminance until no component is negative, keeping its hue.</summary>
    private void ClipNegative(ref float r, ref float g, ref float b)
    {
        float lowest = MathF.Min(r, MathF.Min(g, b));
        if (lowest >= 0)
            return;
        float y = luma.R * r + luma.G * g + luma.B * b;
        if (y <= 0)
        {
            r = g = b = 0;
            return;
        }

        float t = y / (y - lowest);
        r = MathF.Max(0, y + (r - y) * t);
        g = MathF.Max(0, y + (g - y) * t);
        b = MathF.Max(0, y + (b - y) * t);
    }

    /// <summary>Desaturates toward the color's luminance, limited to white, until no component exceeds one.</summary>
    private void DesaturateHighlights(ref float r, ref float g, ref float b)
    {
        float maximum = MathF.Max(r, MathF.Max(g, b));
        if (maximum <= 1)
            return;
        float y = MathF.Min(luma.R * r + luma.G * g + luma.B * b, 1);
        float t = maximum - y > 1e-6f ? (1 - y) / (maximum - y) : 0;
        r = Math.Clamp(y + (r - y) * t, 0, 1);
        g = Math.Clamp(y + (g - y) * t, 0, 1);
        b = Math.Clamp(y + (b - y) * t, 0, 1);
    }

    /// <summary>
    /// The renderer's SDR highlight fit: components above a knee roll off per channel so the largest
    /// lands on one, keeping saturated highlights bright.
    /// </summary>
    private static void RollOffHighlights(ref float r, ref float g, ref float b)
    {
        float maximum = MathF.Max(r, MathF.Max(g, b));
        if (maximum <= 1)
            return;
        const float knee = 0.7f;
        float strength = (maximum - 1) / ((1 - knee) * (maximum - knee));
        r = Compress(r);
        g = Compress(g);
        b = Compress(b);

        float Compress(float value)
        {
            float excess = MathF.Max(value - knee, 0);
            return MathF.Min(value, knee) + excess / (1 + strength * excess);
        }
    }
}

/// <summary>Counts visible source pixels whose BT.2020 light lies outside sRGB and Display P3.</summary>
internal sealed class PhotoExportGamutStatistics
{
    private static readonly float[] ToBt709 = PhotoExportColor.Matrix(PhotoExportPrimaries.Bt2020, PhotoExportPrimaries.Bt709);
    private static readonly float[] ToDisplayP3 = PhotoExportColor.Matrix(PhotoExportPrimaries.Bt2020, PhotoExportPrimaries.DisplayP3);
    private long total;
    private long outsideSrgb;
    private long outsideDisplayP3;

    public long Total => Interlocked.Read(ref total);

    public long OutsideSrgb => Interlocked.Read(ref outsideSrgb);

    public long OutsideDisplayP3 => Interlocked.Read(ref outsideDisplayP3);

    /// <summary>Whether enough visible pixels fall outside sRGB to need Display P3.</summary>
    public bool NeedsWideGamut => OutsideSrgb >= Math.Max(16, Total / 10000);

    /// <param name="first">The region's first pixel.</param>
    /// <param name="stride">Distance between rows, in floats.</param>
    public unsafe void Add(IntPtr first, int width, int rows, int stride, CancellationToken cancellationToken)
    {
        PhotoExportColor.ForRows(rows, width, cancellationToken, (firstRow, lastRow) =>
        {
            long visible = 0, srgb = 0, p3 = 0;
            for (int y = firstRow; y < lastRow; y++)
            {
                float* row = (float*)first + (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    float* pixel = row + x * 4;
                    if (pixel[3] <= 0)
                        continue;
                    visible++;
                    var color = new Vector3(pixel[0], pixel[1], pixel[2]);
                    if (!Outside(PhotoExportColor.Transform(ToBt709, color)))
                        continue;
                    srgb++;
                    if (Outside(PhotoExportColor.Transform(ToDisplayP3, color)))
                        p3++;
                }
            }

            Interlocked.Add(ref total, visible);
            Interlocked.Add(ref outsideSrgb, srgb);
            Interlocked.Add(ref outsideDisplayP3, p3);
        });
    }

    private static bool Outside(Vector3 color)
    {
        float largest = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        return MathF.Min(color.X, MathF.Min(color.Y, color.Z)) < -PhotoExportColor.GamutTolerance * MathF.Max(largest, 1e-3f);
    }
}

/// <summary>A tightly packed single-float RGBA image of linear light on BT.2020 primaries, with straight alpha.</summary>
internal sealed unsafe class LinearImage : IDisposable
{
    private void* pixels;

    /// <summary>Allocates a transparent black image.</summary>
    public LinearImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (!Environment.Is64BitProcess && (long)width * height > int.MaxValue / 16)
            throw new PhotoExportException(PhotoExportFailure.TooLarge);
        Width = width;
        Height = height;
        pixels = NativeMemory.AllocZeroed((nuint)((long)width * height * 16));
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Float components per row.</summary>
    public int Stride => Width * 4;

    public IntPtr Pointer => (IntPtr)Row(0);

    public float* Row(int y)
    {
        ObjectDisposedException.ThrowIf(pixels == null, this);
        return (float*)pixels + (long)y * Stride;
    }

    public void Dispose()
    {
        void* released = pixels;
        pixels = null;
        if (released != null)
            NativeMemory.Free(released);
    }
}
