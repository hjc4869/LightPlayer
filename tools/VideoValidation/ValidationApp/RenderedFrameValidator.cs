using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Controls;
using SkiaSharp;

namespace LightStudio.VideoValidation;

public enum RenderedFrameComparisonDomain
{
    Srgb8,
    ScRgbFloat,
}

public sealed record RenderedFrameMetrics
{
    public RenderedFrameComparisonDomain ComparisonDomain { get; init; }

    public double MeanAbsoluteError { get; init; }

    public double RootMeanSquareError { get; init; }

    public double LumaCorrelation { get; init; }

    public double LumaStandardDeviation { get; init; }

    public int LumaRange { get; init; }

    public int QuantizedColorCount { get; init; }

    public bool NativeFloatAvailable { get; init; }

    public double LinearMeanAbsoluteError { get; init; }

    public double LinearRootMeanSquareError { get; init; }

    public double LinearNormalizedRootMeanSquareError { get; init; }

    public double LinearMaximumAbsoluteError { get; init; }

    public double LinearLumaCorrelation { get; init; }

    public int NonFiniteLinearSamples { get; init; }

    /// <summary>Mean error over the flat interiors, in output code points. SDR only.</summary>
    public double GamutMeanAbsoluteError { get; init; }

    public double GamutMaximumAbsoluteError { get; init; }

    public int GamutComparedPixels { get; init; }

    public bool IsPatterned { get; init; }

    public bool ReferenceMatches { get; init; }

    /// <summary>Per-patch measurement, for assets that carry the gamut probe grid.</summary>
    public HdrGamutProbeReport? GamutProbe { get; init; }

    public string Message { get; init; } = string.Empty;
}

public static class RenderedFrameValidator
{
    /// <summary>Smallest linear difference worth reporting, roughly a code point near black.</summary>
    private const double LinearTolerance = 0.002;

    private const double RelativeTolerance = 0.02;

    /// <summary>Allowed error on a bounded surface, in output code points.</summary>
    private const double CodeTolerance = 6;

    /// <summary>
    /// How far a reference pixel has to be constant around it before its color can be attributed
    /// to the color chain. Wide enough for the three lobes the largest production kernel reaches.
    /// </summary>
    private const int FlatRadius = 4;

    public static async Task<RenderedFrameMetrics> SaveAndValidateAsync(
        VideoRenderCapture capture,
        string capturePath,
        string nativeCapturePath,
        string referencePath,
        string signalReferencePath,
        ValidationAsset asset,
        bool hdrPassthrough,
        double linearNitsPerUnit,
        double toneMapPeakNits,
        LightMediaRenderer.Avalonia.Rendering.VideoOutputStatus output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
        SavePng(capture, capturePath);
        bool extendedOutput =
            capture.NativeColorSpace == LightMediaRenderer.Contracts.VideoOutputColorSpace.ScRgbLinear;
        bool nativeFloatAvailable = extendedOutput && capture.NativeRgbaF32 is not null;
        bool hdr = asset.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        if (hdr && nativeFloatAvailable)
            SavePfm(capture, nativeCapturePath);

        using SKBitmap reference = SKBitmap.Decode(referencePath)
            ?? throw new InvalidDataException($"Could not decode reference image '{referencePath}'.");
        if (reference.Width <= 0 || reference.Height <= 0)
            throw new InvalidDataException($"Reference image '{referencePath}' has no pixels.");

        long absoluteError = 0;
        double squareError = 0;
        double captureLumaSum = 0;
        double referenceLumaSum = 0;
        double captureLumaSquareSum = 0;
        double referenceLumaSquareSum = 0;
        double lumaProductSum = 0;
        int lumaMinimum = 255;
        int lumaMaximum = 0;
        var colors = new HashSet<int>();
        int pixelCount = checked(reference.Width * reference.Height);

        for (int y = 0; y < reference.Height; y++)
        {
            int captureY = Math.Min((int)((y + 0.5) * capture.Height / reference.Height), capture.Height - 1);
            for (int x = 0; x < reference.Width; x++)
            {
                int captureX = Math.Min((int)((x + 0.5) * capture.Width / reference.Width), capture.Width - 1);
                int offset = checked(captureY * capture.RowBytes + captureX * 4);
                byte blue = capture.Pixels[offset];
                byte green = capture.Pixels[offset + 1];
                byte red = capture.Pixels[offset + 2];
                SKColor expected = reference.GetPixel(x, y);

                AccumulateError(blue, expected.Blue);
                AccumulateError(green, expected.Green);
                AccumulateError(red, expected.Red);

                int luma = Luma(red, green, blue);
                int expectedLuma = Luma(expected.Red, expected.Green, expected.Blue);
                captureLumaSum += luma;
                referenceLumaSum += expectedLuma;
                captureLumaSquareSum += luma * luma;
                referenceLumaSquareSum += expectedLuma * expectedLuma;
                lumaProductSum += luma * expectedLuma;
                lumaMinimum = Math.Min(lumaMinimum, luma);
                lumaMaximum = Math.Max(lumaMaximum, luma);
                colors.Add((red >> 4) << 8 | (green >> 4) << 4 | (blue >> 4));
            }
        }

        double sampleCount = pixelCount * 3.0;
        double meanAbsoluteError = absoluteError / sampleCount;
        double rootMeanSquareError = Math.Sqrt(squareError / sampleCount);
        double captureLumaMean = captureLumaSum / pixelCount;
        double referenceLumaMean = referenceLumaSum / pixelCount;
        double captureVariance = Math.Max(0, captureLumaSquareSum / pixelCount - captureLumaMean * captureLumaMean);
        double referenceVariance = Math.Max(0, referenceLumaSquareSum / pixelCount - referenceLumaMean * referenceLumaMean);
        double covariance = lumaProductSum / pixelCount - captureLumaMean * referenceLumaMean;
        double correlation = captureVariance > 0.01 && referenceVariance > 0.01
            ? covariance / Math.Sqrt(captureVariance * referenceVariance)
            : 0;
        int range = lumaMaximum - lumaMinimum;

        // Flat probe patches carry far fewer distinct colors than a bar pattern, by design.
        int minimumColors = asset.ResolvedPattern == ValidationPattern.HdrGamutProbe ? 6 : 24;
        bool patterned = range >= 40 && Math.Sqrt(captureVariance) >= 12 && colors.Count >= minimumColors;

        HdrExpectedFrame? hdrExpected = null;
        LinearComparison? linear = null;
        if (hdr && !string.IsNullOrWhiteSpace(signalReferencePath))
        {
            hdrExpected = await HdrReferenceProcessor.CreateAsync(
                signalReferencePath,
                asset,
                hdrPassthrough,
                extendedOutput,
                linearNitsPerUnit,
                toneMapPeakNits,
                output.RelativeLuminance,
                extendedOutput ? output.ReferenceWhiteScale ?? 1 : 1,
                cancellationToken).ConfigureAwait(false);
        }
        if (hdrExpected is not null && nativeFloatAvailable)
        {
            linear = CompareLinear(
                capture,
                hdrExpected.Rgba,
                asset.Width,
                asset.Height);
        }

        HdrGamutProbeReport? probe = null;
        if (hdr && asset.ResolvedPattern == ValidationPattern.HdrGamutProbe)
        {
            probe = hdrExpected is not null && nativeFloatAvailable
                ? HdrGamutProbe.Evaluate(capture, hdrExpected)
                : HdrGamutProbeReport.NotApplicable(
                    "Out-of-gamut output is not measurable on a bounded surface.");
        }

        // Only flat interiors are compared, because the reference is not resized and a resampler's
        // ringing at a bar edge is not a color defect. On an extended-range surface the native
        // capture also makes the SDR transfer visible before the compositor can obscure it.
        GamutComparison? sdr = null;
        if (!hdr && !string.IsNullOrWhiteSpace(signalReferencePath))
        {
            SdrExpectedFrame expected = await SdrReferenceProcessor.CreateAsync(
                signalReferencePath,
                asset,
                output,
                cancellationToken).ConfigureAwait(false);
            sdr = CompareFlatInteriors(capture, expected);
        }

        RenderedFrameComparisonDomain domain = hdr && nativeFloatAvailable
            ? RenderedFrameComparisonDomain.ScRgbFloat
            : RenderedFrameComparisonDomain.Srgb8;
        double maximumMeanError = asset.Codec == "rawvideo" ? 12 : 18;
        const double minimumCorrelation = 0.97;
        bool previewMatches = hdrExpected is not null && !nativeFloatAvailable
            ? CompareHdrPreview(capture, hdrExpected, out meanAbsoluteError, out rootMeanSquareError, out correlation)
            : meanAbsoluteError <= maximumMeanError && correlation >= minimumCorrelation;
        bool linearMatches = linear is not null &&
            linear.NonFiniteSamples == 0 &&
            linear.MeanAbsoluteError <= 0.025 &&
            linear.NormalizedRootMeanSquareError <= 0.08 &&
            linear.LumaCorrelation >= 0.997;

        // A whole-frame comparison against an unresized reference cannot separate the resampler's
        // own ringing from a defect, so where patch interiors are available they decide instead.
        bool referenceMatches = probe is { Applicable: true }
            ? probe.Matches
            : sdr is not null
                ? sdr.Matches
                : hdr && nativeFloatAvailable
                    ? linearMatches
                    : previewMatches;
        string message = !patterned
            ? $"Frame lacks the expected pattern (range {range}, sigma {Math.Sqrt(captureVariance):0.0}, colors {colors.Count})."
            : probe is { Applicable: true }
            ? probe.Message
            : sdr is not null
            ? (sdr.Matches ? "SDR matches the converted reference" : "SDR diverged from the converted reference") +
                $" ({sdr.Domain}: mean {sdr.MeanAbsoluteError:0.00000}, max {sdr.MaximumAbsoluteError:0.00000}, " +
                $"{sdr.FailedPixels} of {sdr.ComparedPixels} flat pixels outside tolerance)."
            : !referenceMatches
                ? hdr && nativeFloatAvailable
                    ? linear is null
                        ? "HDR output could not be compared because native scRGB float capture or its signal reference is unavailable."
                        : $"Native scRGB diverged from the linear reference (MAE {linear.MeanAbsoluteError:0.0000}, " +
                            $"NRMSE {linear.NormalizedRootMeanSquareError:0.0000}, luma correlation {linear.LumaCorrelation:0.0000})."
                    : $"Rendered sRGB diverged from the processed HDR reference (MAE {meanAbsoluteError:0.00}, " +
                        $"luma correlation {correlation:0.0000})."
                : hdr && nativeFloatAvailable
                    ? $"Native scRGB matches the linear reference (MAE {linear!.MeanAbsoluteError:0.0000}, " +
                        $"NRMSE {linear.NormalizedRootMeanSquareError:0.0000})."
                    : "Rendered sRGB matches the generated reference.";

        return new RenderedFrameMetrics
        {
            ComparisonDomain = domain,
            MeanAbsoluteError = meanAbsoluteError,
            RootMeanSquareError = rootMeanSquareError,
            LumaCorrelation = correlation,
            LumaStandardDeviation = Math.Sqrt(captureVariance),
            LumaRange = range,
            QuantizedColorCount = colors.Count,
            NativeFloatAvailable = nativeFloatAvailable,
            LinearMeanAbsoluteError = linear?.MeanAbsoluteError ?? 0,
            LinearRootMeanSquareError = linear?.RootMeanSquareError ?? 0,
            LinearNormalizedRootMeanSquareError = linear?.NormalizedRootMeanSquareError ?? 0,
            LinearMaximumAbsoluteError = linear?.MaximumAbsoluteError ?? 0,
            LinearLumaCorrelation = linear?.LumaCorrelation ?? 0,
            NonFiniteLinearSamples = linear?.NonFiniteSamples ?? 0,
            GamutMeanAbsoluteError = sdr?.MeanAbsoluteError ?? 0,
            GamutMaximumAbsoluteError = sdr?.MaximumAbsoluteError ?? 0,
            GamutComparedPixels = sdr?.ComparedPixels ?? 0,
            IsPatterned = patterned,
            ReferenceMatches = referenceMatches,
            GamutProbe = probe,
            Message = message,
        };

        void AccumulateError(byte actual, byte expected)
        {
            int difference = actual - expected;
            absoluteError += Math.Abs(difference);
            squareError += difference * difference;
        }
    }

    private static LinearComparison CompareLinear(
        VideoRenderCapture capture,
        float[] expected,
        int referenceWidth,
        int referenceHeight)
    {
        float[] actual = capture.NativeRgbaF32
            ?? throw new InvalidOperationException("The capture has no native float pixels.");
        int planeSamples = checked(referenceWidth * referenceHeight);
        if (expected.Length != checked(planeSamples * 4))
            throw new InvalidDataException("The processed HDR reference has the wrong pixel count.");

        double absoluteError = 0;
        double squareError = 0;
        double referenceSquare = 0;
        double maximumAbsoluteError = 0;
        double actualLumaSum = 0;
        double referenceLumaSum = 0;
        double actualLumaSquareSum = 0;
        double referenceLumaSquareSum = 0;
        double lumaProductSum = 0;
        int nonFinite = 0;
        int sampleCount = checked(planeSamples * 3);

        for (int y = 0; y < referenceHeight; y++)
        {
            int captureY = Math.Min((int)((y + 0.5) * capture.Height / referenceHeight), capture.Height - 1);
            for (int x = 0; x < referenceWidth; x++)
            {
                int captureX = Math.Min((int)((x + 0.5) * capture.Width / referenceWidth), capture.Width - 1);
                int actualOffset = checked((captureY * capture.Width + captureX) * 4);
                int referenceOffset = checked((y * referenceWidth + x) * 4);
                float expectedRed = expected[referenceOffset];
                float expectedGreen = expected[referenceOffset + 1];
                float expectedBlue = expected[referenceOffset + 2];
                float actualRed = actual[actualOffset];
                float actualGreen = actual[actualOffset + 1];
                float actualBlue = actual[actualOffset + 2];

                Accumulate(actualRed, expectedRed);
                Accumulate(actualGreen, expectedGreen);
                Accumulate(actualBlue, expectedBlue);

                double actualLuma = 0.2126 * actualRed + 0.7152 * actualGreen + 0.0722 * actualBlue;
                double expectedLuma = 0.2126 * expectedRed + 0.7152 * expectedGreen + 0.0722 * expectedBlue;
                if (!double.IsFinite(actualLuma) || !double.IsFinite(expectedLuma))
                {
                    nonFinite++;
                    continue;
                }

                actualLumaSum += actualLuma;
                referenceLumaSum += expectedLuma;
                actualLumaSquareSum += actualLuma * actualLuma;
                referenceLumaSquareSum += expectedLuma * expectedLuma;
                lumaProductSum += actualLuma * expectedLuma;
            }
        }

        int pixelCount = planeSamples;
        double actualLumaMean = actualLumaSum / pixelCount;
        double referenceLumaMean = referenceLumaSum / pixelCount;
        double actualVariance = Math.Max(0, actualLumaSquareSum / pixelCount - actualLumaMean * actualLumaMean);
        double referenceVariance = Math.Max(0, referenceLumaSquareSum / pixelCount - referenceLumaMean * referenceLumaMean);
        double covariance = lumaProductSum / pixelCount - actualLumaMean * referenceLumaMean;
        double lumaCorrelation = actualVariance > 1e-12 && referenceVariance > 1e-12
            ? covariance / Math.Sqrt(actualVariance * referenceVariance)
            : 0;
        double rootMeanSquareError = Math.Sqrt(squareError / sampleCount);
        double referenceRms = Math.Sqrt(referenceSquare / sampleCount);
        return new LinearComparison(
            absoluteError / sampleCount,
            rootMeanSquareError,
            rootMeanSquareError / Math.Max(referenceRms, 1e-6),
            maximumAbsoluteError,
            lumaCorrelation,
            nonFinite);

        void Accumulate(float value, float expected)
        {
            if (!float.IsFinite(value) || !float.IsFinite(expected))
            {
                nonFinite++;
                return;
            }

            double difference = value - expected;
            double magnitude = Math.Abs(difference);
            absoluteError += magnitude;
            squareError += difference * difference;
            referenceSquare += expected * expected;
            maximumAbsoluteError = Math.Max(maximumAbsoluteError, magnitude);
        }
    }

    private static bool CompareHdrPreview(
        VideoRenderCapture capture,
        HdrExpectedFrame expected,
        out double meanAbsoluteError,
        out double rootMeanSquareError,
        out double lumaCorrelation)
    {
        double absoluteError = 0;
        double squareError = 0;
        double actualLumaSum = 0;
        double expectedLumaSum = 0;
        double actualLumaSquareSum = 0;
        double expectedLumaSquareSum = 0;
        double lumaProductSum = 0;
        int pixels = expected.Width * expected.Height;

        for (int y = 0; y < expected.Height; y++)
        {
            int captureY = Math.Min((int)((y + 0.5) * capture.Height / expected.Height), capture.Height - 1);
            for (int x = 0; x < expected.Width; x++)
            {
                int captureX = Math.Min((int)((x + 0.5) * capture.Width / expected.Width), capture.Width - 1);
                int captureOffset = captureY * capture.RowBytes + captureX * 4;
                int expectedOffset = (y * expected.Width + x) * 4;
                byte actualBlue = capture.Pixels[captureOffset];
                byte actualGreen = capture.Pixels[captureOffset + 1];
                byte actualRed = capture.Pixels[captureOffset + 2];
                byte expectedRed = ToSrgbByte(expected.Rgba[expectedOffset]);
                byte expectedGreen = ToSrgbByte(expected.Rgba[expectedOffset + 1]);
                byte expectedBlue = ToSrgbByte(expected.Rgba[expectedOffset + 2]);

                Accumulate(actualRed, expectedRed);
                Accumulate(actualGreen, expectedGreen);
                Accumulate(actualBlue, expectedBlue);
                int actualLuma = Luma(actualRed, actualGreen, actualBlue);
                int referenceLuma = Luma(expectedRed, expectedGreen, expectedBlue);
                actualLumaSum += actualLuma;
                expectedLumaSum += referenceLuma;
                actualLumaSquareSum += actualLuma * actualLuma;
                expectedLumaSquareSum += referenceLuma * referenceLuma;
                lumaProductSum += actualLuma * referenceLuma;
            }
        }

        meanAbsoluteError = absoluteError / (pixels * 3.0);
        rootMeanSquareError = Math.Sqrt(squareError / (pixels * 3.0));
        double actualMean = actualLumaSum / pixels;
        double expectedMean = expectedLumaSum / pixels;
        double actualVariance = Math.Max(0, actualLumaSquareSum / pixels - actualMean * actualMean);
        double expectedVariance = Math.Max(0, expectedLumaSquareSum / pixels - expectedMean * expectedMean);
        double covariance = lumaProductSum / pixels - actualMean * expectedMean;
        lumaCorrelation = actualVariance > 0.01 && expectedVariance > 0.01
            ? covariance / Math.Sqrt(actualVariance * expectedVariance)
            : 0;
        return meanAbsoluteError <= 3 && lumaCorrelation >= 0.995;

        void Accumulate(byte value, byte reference)
        {
            int difference = value - reference;
            absoluteError += Math.Abs(difference);
            squareError += difference * difference;
        }
    }

    /// <summary>
    /// Compares only the pixels whose reference neighborhood is flat. Every resampling filter
    /// reproduces the interior of a flat region exactly, so what is left is the color chain; the
    /// edges the filter does change are left out rather than tolerated. An extended-range surface
    /// is read as the linear light it holds, so a color outside BT.709 is compared where it still
    /// exists rather than after something has clipped it away.
    /// </summary>
    private static GamutComparison CompareFlatInteriors(VideoRenderCapture capture, SdrExpectedFrame expected)
    {
        float[]? native = expected.IsLinear ? capture.NativeRgbaF32 : null;
        bool linear = native is not null;
        double absoluteError = 0;
        double maximumError = 0;
        int compared = 0;
        int failed = 0;

        for (int y = 1; y < expected.Height - 1; y++)
        {
            int captureY = Math.Min((int)((y + 0.5) * capture.Height / expected.Height), capture.Height - 1);
            for (int x = 1; x < expected.Width - 1; x++)
            {
                if (!IsFlat(expected, x, y))
                    continue;
                int captureX = Math.Min((int)((x + 0.5) * capture.Width / expected.Width), capture.Width - 1);
                int expectedOffset = ((y * expected.Width) + x) * 4;
                compared++;

                double pixelError;
                double tolerance;
                if (native is not null)
                {
                    int offset = ((captureY * capture.Width) + captureX) * 4;
                    pixelError = Math.Max(
                        Math.Abs(native[offset] - expected.Rgba[expectedOffset]),
                        Math.Max(
                            Math.Abs(native[offset + 1] - expected.Rgba[expectedOffset + 1]),
                            Math.Abs(native[offset + 2] - expected.Rgba[expectedOffset + 2])));
                    double magnitude = Math.Max(
                        Math.Abs(expected.Rgba[expectedOffset]),
                        Math.Max(
                            Math.Abs(expected.Rgba[expectedOffset + 1]),
                            Math.Abs(expected.Rgba[expectedOffset + 2])));
                    tolerance = Math.Max(LinearTolerance, RelativeTolerance * magnitude);
                }
                else
                {
                    int offset = (captureY * capture.RowBytes) + (captureX * 4);
                    pixelError = Math.Max(
                        Difference(capture.Pixels[offset + 2], expected.Rgba[expectedOffset]),
                        Math.Max(
                            Difference(capture.Pixels[offset + 1], expected.Rgba[expectedOffset + 1]),
                            Difference(capture.Pixels[offset], expected.Rgba[expectedOffset + 2])));
                    tolerance = CodeTolerance;
                }

                absoluteError += pixelError;
                maximumError = Math.Max(maximumError, pixelError);
                if (!(pixelError <= tolerance))
                    failed++;
            }
        }

        return new GamutComparison(
            compared > 0 ? absoluteError / compared : double.PositiveInfinity,
            maximumError,
            compared,
            failed,
            linear ? "scRGB linear" : "output code points",
            compared > 0 && failed == 0);

        static double Difference(byte actual, float reference) =>
            Math.Abs(actual - (int)Math.Round(Math.Clamp(reference, 0, 1) * 255));
    }

    /// <summary>Whether the reference is constant over the pixel and everything within reach of it.</summary>
    private static bool IsFlat(SdrExpectedFrame expected, int x, int y)
    {
        if (x < FlatRadius || y < FlatRadius ||
            x >= expected.Width - FlatRadius || y >= expected.Height - FlatRadius)
        {
            return false;
        }

        int center = ((y * expected.Width) + x) * 4;
        for (int dy = -FlatRadius; dy <= FlatRadius; dy++)
        {
            for (int dx = -FlatRadius; dx <= FlatRadius; dx++)
            {
                int offset = (((y + dy) * expected.Width) + x + dx) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    if (Math.Abs(expected.Rgba[offset + channel] - expected.Rgba[center + channel]) > 1.0f / 255)
                        return false;
                }
            }
        }

        return true;
    }

    private static byte ToSrgbByte(float linear)
    {
        double encoded = linear <= 0.0031308
            ? 12.92 * linear
            : 1.055 * Math.Pow(Math.Max(linear, 0), 1.0 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(encoded, 0, 1) * 255);
    }

    private static void SavePng(VideoRenderCapture capture, string path)
    {
        var info = new SKImageInfo(
            capture.Width,
            capture.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque,
            SKColorSpace.CreateSrgb());
        GCHandle pinned = GCHandle.Alloc(capture.Pixels, GCHandleType.Pinned);
        try
        {
            using SKImage? image = SKImage.FromPixels(info, pinned.AddrOfPinnedObject(), capture.RowBytes);
            using SKData? data = image?.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
                throw new InvalidOperationException("Skia could not encode the rendered frame as PNG.");
            using FileStream stream = File.Create(path);
            data.SaveTo(stream);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void SavePfm(VideoRenderCapture capture, string path)
    {
        float[] pixels = capture.NativeRgbaF32
            ?? throw new InvalidOperationException("The capture has no native float pixels.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: false);
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"PF\n{capture.Width} {capture.Height}\n-1.0\n"));

        // PFM stores scanlines bottom-up. A negative scale declares little-endian floats.
        for (int y = capture.Height - 1; y >= 0; y--)
        {
            int row = y * capture.Width * 4;
            for (int x = 0; x < capture.Width; x++)
            {
                int offset = row + x * 4;
                writer.Write(pixels[offset]);
                writer.Write(pixels[offset + 1]);
                writer.Write(pixels[offset + 2]);
            }
        }
    }

    private static int Luma(byte red, byte green, byte blue) =>
        (54 * red + 183 * green + 19 * blue + 128) >> 8;

    private sealed record LinearComparison(
        double MeanAbsoluteError,
        double RootMeanSquareError,
        double NormalizedRootMeanSquareError,
        double MaximumAbsoluteError,
        double LumaCorrelation,
        int NonFiniteSamples);

    private sealed record GamutComparison(
        double MeanAbsoluteError,
        double MaximumAbsoluteError,
        int ComparedPixels,
        int FailedPixels,
        string Domain,
        bool Matches);
}