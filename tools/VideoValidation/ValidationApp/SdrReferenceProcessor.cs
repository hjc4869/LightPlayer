using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;

namespace LightStudio.VideoValidation;

/// <summary>
/// What the surface should hold, in RGBA order: linear light where the target is extended range,
/// otherwise the 0-1 values a bounded surface stores.
/// </summary>
public sealed record SdrExpectedFrame(int Width, int Height, float[] Rgba, bool IsLinear);

/// <summary>
/// CPU implementation of the SDR output equations. FFmpeg independently supplies the
/// range-expanded, matrix-converted encoded RGB signal; this class owns the transfer, the gamut
/// matrix, the gamut fit and the surface encoding, with matrices taken from the published
/// chromaticities rather than from the renderer, so validation is not comparing the renderer to
/// its own arithmetic.
/// </summary>
public static class SdrReferenceProcessor
{
    private static readonly double[] Bt709ToBt709 =
    [
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
    ];

    // Derived from the SMPTE and ITU chromaticities, with Bradford adaptation where the source
    // white point is not D65. Rows sum to one, which is white staying white.
    private static readonly double[] DisplayP3ToBt709 =
    [
        1.224940, -0.224940, 0.000000,
        -0.042057, 1.042057, 0.000000,
        -0.019638, -0.078636, 1.098274,
    ];

    private static readonly double[] DciP3ToBt709 =
    [
        1.157516, -0.154962, -0.002554,
        -0.041500, 1.045568, -0.004068,
        -0.018050, -0.078578, 1.096628,
    ];

    private static readonly double[] Bt470BgToBt709 =
    [
        1.044043, -0.044043, 0.000000,
        0.000000, 1.000000, 0.000000,
        0.000000, 0.011793, 0.988207,
    ];

    private static readonly double[] Bt2020ToBt709 =
    [
        1.660491, -0.587641, -0.072850,
        -0.124550, 1.132900, -0.008349,
        -0.018151, -0.100579, 1.118730,
    ];

    public static async Task<SdrExpectedFrame> CreateAsync(
        string signalPath,
        ValidationAsset asset,
        VideoOutputStatus output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        byte[] bytes = await File.ReadAllBytesAsync(signalPath, cancellationToken).ConfigureAwait(false);
        int planeSamples = checked(asset.Width * asset.Height);
        int expectedBytes = checked(planeSamples * 3 * sizeof(float));
        if (bytes.Length != expectedBytes)
            throw new InvalidDataException($"SDR signal reference is {bytes.Length} bytes; expected {expectedBytes}.");

        double[] gamut = GamutMatrix(asset.ColorPrimaries);
        bool extended = output.Target == VideoOutputColorSpace.ScRgbLinear;

        // An extended-range surface takes light rather than a signal, and 1.0 on it is not always
        // diffuse white: where the platform pins it elsewhere, the whole scene moves to land on it.
        double unitScale = extended ? output.ReferenceWhiteScale ?? 1 : 1;

        var rgba = new float[checked(planeSamples * 4)];
        for (int pixel = 0; pixel < planeSamples; pixel++)
        {
            double green = DecodeSourceTransfer(ReadFloat(bytes, pixel), output);
            double blue = DecodeSourceTransfer(ReadFloat(bytes, planeSamples + pixel), output);
            double red = DecodeSourceTransfer(ReadFloat(bytes, (planeSamples * 2) + pixel), output);

            double outputRed = (gamut[0] * red) + (gamut[1] * green) + (gamut[2] * blue);
            double outputGreen = (gamut[3] * red) + (gamut[4] * green) + (gamut[5] * blue);
            double outputBlue = (gamut[6] * red) + (gamut[7] * green) + (gamut[8] * blue);

            if (extended)
            {
                // Nothing is fitted: a color outside BT.709 stays a negative component, which is
                // what the platform color manager is there to receive.
                outputRed *= unitScale;
                outputGreen *= unitScale;
                outputBlue *= unitScale;
            }
            else
            {
                (outputRed, outputGreen, outputBlue) = CompressGamut(outputRed, outputGreen, outputBlue);
                outputRed = Encode(outputRed, output.SurfaceTransfer);
                outputGreen = Encode(outputGreen, output.SurfaceTransfer);
                outputBlue = Encode(outputBlue, output.SurfaceTransfer);
            }

            int index = pixel * 4;
            rgba[index] = (float)outputRed;
            rgba[index + 1] = (float)outputGreen;
            rgba[index + 2] = (float)outputBlue;
            rgba[index + 3] = 1;
        }

        return new SdrExpectedFrame(asset.Width, asset.Height, rgba, extended);
    }

    private static double[] GamutMatrix(VideoColorPrimaries primaries) => primaries switch
    {
        VideoColorPrimaries.Bt709 => Bt709ToBt709,
        VideoColorPrimaries.Bt2020 => Bt2020ToBt709,
        VideoColorPrimaries.DisplayP3 => DisplayP3ToBt709,
        VideoColorPrimaries.DciP3 => DciP3ToBt709,
        VideoColorPrimaries.Bt470Bg => Bt470BgToBt709,
        _ => throw new NotSupportedException($"{primaries} has no reference matrix in the SDR model."),
    };

    private static double DecodeTransfer(double signal, VideoSurfaceTransfer transfer)
    {
        signal = Math.Clamp(signal, 0, 1);
        if (transfer.Kind == VideoSurfaceTransferKind.Power)
            return Math.Pow(signal, transfer.Exponent);

        return signal <= 0.04045
            ? signal / 12.92
            : Math.Pow((signal + 0.055) / 1.055, 2.4);
    }

    private static double DecodeSourceTransfer(double signal, VideoOutputStatus output)
    {
        signal = Math.Clamp(signal, 0, 1);
        if (output.SourceTransfer == VideoColorTransfer.Linear)
            return signal;
        if (output.SourceTransfer == VideoColorTransfer.Srgb)
            return DecodeTransfer(signal, VideoSurfaceTransfer.Srgb);
        if (!output.SdrReadAsMastered)
            return DecodeTransfer(signal, output.SurfaceTransfer);

        // Match the BT.1886 equation independently, using the target black that mpv/libplacebo
        // also adopts for an SDR source on an HDR display.
        double black = output.LuminanceBasis is VideoLuminanceBasis.DisplayReported or
            VideoLuminanceBasis.Nominal &&
            output.OutputNitsPerUnit is > 0 and var white && double.IsFinite(white) &&
            output.TargetMinimumNits is >= 0 and var minimum && double.IsFinite(minimum)
            ? Math.Clamp(minimum / white, 0, 1 - 1e-6)
            : 0;
        double blackRoot = Math.Pow(black, 1 / 2.4);
        double range = 1 - blackRoot;
        double scale = Math.Pow(range, 2.4);
        double offset = blackRoot / range;
        return scale * Math.Pow(signal + offset, 2.4);
    }

    private static (double Red, double Green, double Blue) CompressGamut(double red, double green, double blue)
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
            value <= knee ? value : knee + ((value - knee) / (1 + (strength * (value - knee))));
    }

    private static double Encode(double light, VideoSurfaceTransfer surfaceTransfer)
    {
        double encoded = surfaceTransfer.Kind == VideoSurfaceTransferKind.Power
            ? Math.Pow(Math.Max(light, 0), 1.0 / surfaceTransfer.Exponent)
            : light <= 0.0031308
                ? 12.92 * light
                : (1.055 * Math.Pow(Math.Max(light, 0), 1.0 / 2.4)) - 0.055;
        return Math.Clamp(encoded, 0, 1);
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int sample) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(sample * sizeof(float))..]));
}
