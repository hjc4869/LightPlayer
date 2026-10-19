using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.VideoValidation;

public sealed record HdrExpectedFrame(int Width, int Height, float[] Rgba);

/// <summary>
/// CPU implementation of the documented HDR output equations. FFmpeg independently supplies the
/// range-expanded, matrix-converted encoded RGB signal; this class owns transfer, OOTF, gamut,
/// PQ or relative spline mapping, BT.2390, and output normalization independently of rendered pixels.
/// </summary>
public static class HdrReferenceProcessor
{
    private const double SourcePeakNits = 1000;

    public static async Task<HdrExpectedFrame> CreateAsync(
        string signalPath,
        ValidationAsset asset,
        bool hdrPassthrough,
        bool extendedOutput,
        double linearNitsPerUnit,
        double toneMapPeakNits,
        bool relativeLuminance,
        double referenceWhiteScale,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(linearNitsPerUnit) || linearNitsPerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(linearNitsPerUnit));
        if (!double.IsFinite(toneMapPeakNits) || toneMapPeakNits <= 0)
            throw new ArgumentOutOfRangeException(nameof(toneMapPeakNits));

        byte[] bytes = await File.ReadAllBytesAsync(signalPath, cancellationToken).ConfigureAwait(false);
        int planeSamples = checked(asset.Width * asset.Height);
        int expectedBytes = checked(planeSamples * 3 * sizeof(float));
        if (bytes.Length != expectedBytes)
            throw new InvalidDataException($"HDR signal reference is {bytes.Length} bytes; expected {expectedBytes}.");

        var rgba = new float[checked(planeSamples * 4)];
        for (int pixel = 0; pixel < planeSamples; pixel++)
        {
            double green = ReadFloat(bytes, pixel);
            double blue = ReadFloat(bytes, planeSamples + pixel);
            double red = ReadFloat(bytes, planeSamples * 2 + pixel);
            (double outputRed, double outputGreen, double outputBlue) = Process(
                red,
                green,
                blue,
                asset,
                hdrPassthrough,
                extendedOutput,
                linearNitsPerUnit,
                toneMapPeakNits,
                relativeLuminance,
                referenceWhiteScale);
            int output = pixel * 4;
            rgba[output] = (float)outputRed;
            rgba[output + 1] = (float)outputGreen;
            rgba[output + 2] = (float)outputBlue;
            rgba[output + 3] = 1;
        }

        return new HdrExpectedFrame(asset.Width, asset.Height, rgba);
    }

    private static (double Red, double Green, double Blue) Process(
        double red,
        double green,
        double blue,
        ValidationAsset asset,
        bool hdrPassthrough,
        bool extendedOutput,
        double linearNitsPerUnit,
        double toneMapPeakNits,
        bool relativeLuminance,
        double referenceWhiteScale)
    {
        red = Math.Clamp(red, 0, 1);
        green = Math.Clamp(green, 0, 1);
        blue = Math.Clamp(blue, 0, 1);

        double redNits;
        double greenNits;
        double blueNits;
        if (asset.ColorTransfer == VideoColorTransfer.Pq)
        {
            redNits = PqToNits(red);
            greenNits = PqToNits(green);
            blueNits = PqToNits(blue);
        }
        else if (asset.ColorTransfer == VideoColorTransfer.Hlg)
        {
            double redScene = HlgToScene(red);
            double greenScene = HlgToScene(green);
            double blueScene = HlgToScene(blue);
            double sceneLuma = Math.Max(0.2627 * redScene + 0.6780 * greenScene + 0.0593 * blueScene, 0);
            double systemGamma = 1.2 + 0.42 * Math.Log10(SourcePeakNits / 1000.0);
            double ootf = Math.Pow(Math.Max(sceneLuma, 1e-6), systemGamma - 1.0) * SourcePeakNits;
            redNits = redScene * ootf;
            greenNits = greenScene * ootf;
            blueNits = blueScene * ootf;
        }
        else
        {
            throw new InvalidOperationException($"{asset.ColorTransfer} is not an HDR transfer.");
        }

        if (!hdrPassthrough)
        {
            double luminance = Math.Max(0.2627 * redNits + 0.6780 * greenNits + 0.0593 * blueNits, 0);
            double mapped = toneMapPeakNits <= linearNitsPerUnit
                ? SourcePeakNits <= toneMapPeakNits ? luminance : Spline(luminance, toneMapPeakNits)
                : relativeLuminance ? Spline(luminance, toneMapPeakNits, relative: true) : Bt2390(luminance, toneMapPeakNits);
            double scale = luminance > 1e-6 ? mapped / luminance : 0;
            redNits *= scale;
            greenNits *= scale;
            blueNits *= scale;
        }

        (double outputRed, double outputGreen, double outputBlue) = TransformBt2020ToBt709(redNits, greenNits, blueNits);
        if (hdrPassthrough)
        {
            return (
                outputRed / linearNitsPerUnit * referenceWhiteScale,
                outputGreen / linearNitsPerUnit * referenceWhiteScale,
                outputBlue / linearNitsPerUnit * referenceWhiteScale);
        }

        (outputRed, outputGreen, outputBlue) = extendedOutput
            ? FitExtendedRange(
                outputRed / toneMapPeakNits,
                outputGreen / toneMapPeakNits,
                outputBlue / toneMapPeakNits)
            : CompressGamut(
                outputRed / toneMapPeakNits,
                outputGreen / toneMapPeakNits,
                outputBlue / toneMapPeakNits);
        double outputScale = toneMapPeakNits / linearNitsPerUnit * referenceWhiteScale;
        return (outputRed * outputScale, outputGreen * outputScale, outputBlue * outputScale);
    }

    private static double PqToNits(double signal)
    {
        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;
        double power = Math.Pow(Math.Max(signal, 0), 1.0 / m2);
        double level = Math.Max(power - c1, 0) / Math.Max(c2 - c3 * power, 1e-6);
        return 10000 * Math.Pow(level, 1.0 / m1);
    }

    private static double NitsToPq(double nits)
    {
        if (nits <= 0)
            return 0;
        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;
        double level = Math.Pow(Math.Clamp(nits / 10000, 0, 1), m1);
        return Math.Pow((c1 + c2 * level) / (1 + c3 * level), m2);
    }

    private static double HlgToScene(double signal)
    {
        const double a = 0.17883277;
        const double b = 0.28466892;
        const double c = 0.55991073;
        return signal <= 0.5
            ? signal * signal / 3.0
            : (Math.Exp((signal - c) / a) + b) / 12.0;
    }

    private static double Spline(double nits, double targetPeak, bool relative = false)
    {
        double sourceMax = Coordinate(Math.Max(SourcePeakNits, targetPeak));
        double targetMax = Coordinate(targetPeak);
        double sourcePivot = 0.4 * sourceMax;
        double targetPivot = Math.Clamp(
            sourcePivot + (relative ? 0.4 : 0) * (0.4 * targetMax - sourcePivot),
            0.1 * targetMax, 0.8 * targetMax);
        double slope = Math.Pow(targetPivot / sourcePivot,
            0.5 * Math.Clamp(1.5 * (sourceMax / targetMax - 1), 0.2, 1.2));
        slope = Math.Min(slope, Math.Min(2 * targetPivot / sourcePivot,
            3 * (targetMax - targetPivot) / (sourceMax - sourcePivot)));
        double below = -sourcePivot;
        double above = sourceMax - sourcePivot;
        double lowerCoefficient = (-targetPivot - slope * below) / (below * below);
        double upperDifference = slope * above - (targetMax - targetPivot);
        double cubic = upperDifference / (2 * above * above * above);
        double square = -3 * upperDifference / (2 * above * above);
        double distance = Math.Clamp(Coordinate(nits), 0, sourceMax) - sourcePivot;
        double mapped = distance > 0
            ? ((cubic * distance + square) * distance + slope) * distance
            : (lowerCoefficient * distance + slope) * distance;
        mapped = Math.Clamp(mapped + targetPivot, 0, targetMax);
        return relative ? (Math.Pow(2, mapped) - 1) * 203 : PqToNits(mapped);

        double Coordinate(double luminance) =>
            relative ? Math.Log2(1 + luminance / 203) : NitsToPq(luminance);
    }

    private static double Bt2390(double nits, double toneMapPeakNits)
    {
        double sourceMax = NitsToPq(SourcePeakNits);
        double targetMax = NitsToPq(toneMapPeakNits);
        double maxLuminance = targetMax / sourceMax;
        double value = Math.Clamp(NitsToPq(nits) / sourceMax, 0, 1);
        double knee = 1.5 * maxLuminance - 0.5;
        if (knee < 1 && value > knee)
        {
            double t = (value - knee) / (1 - knee);
            double t2 = t * t;
            double t3 = t2 * t;
            value = (2 * t3 - 3 * t2 + 1) * knee
                + (t3 - 2 * t2 + t) * (1 - knee)
                + (-2 * t3 + 3 * t2) * maxLuminance;
        }

        return PqToNits(Math.Min(value * sourceMax, targetMax));
    }

    private static (double Red, double Green, double Blue) TransformBt2020ToBt709(
        double red,
        double green,
        double blue) =>
        (
            1.660491 * red - 0.587641 * green - 0.072850 * blue,
            -0.124550 * red + 1.132900 * green - 0.008349 * blue,
            -0.018151 * red - 0.100579 * green + 1.118730 * blue
        );

    private static (double Red, double Green, double Blue) CompressGamut(
        double red,
        double green,
        double blue)
    {
        red = Math.Max(red, 0);
        green = Math.Max(green, 0);
        blue = Math.Max(blue, 0);
        double maximum = Math.Max(red, Math.Max(green, blue));
        if (maximum <= 1)
            return (red, green, blue);

        const double knee = 0.7;
        double strength = (maximum - 1) / ((1 - knee) * (maximum - knee));
        return (SoftClip(red), SoftClip(green), SoftClip(blue));

        double SoftClip(double value) =>
            value <= knee ? value : knee + (value - knee) / (1 + strength * (value - knee));
    }

    private static (double Red, double Green, double Blue) FitExtendedRange(
        double red,
        double green,
        double blue)
    {
        double maximum = Math.Max(red, Math.Max(green, blue));
        if (maximum <= 1)
            return (red, green, blue);

        double scale = 1 / maximum;
        return (red * scale, green * scale, blue * scale);
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int sample) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(sample * sizeof(float))..]));
}