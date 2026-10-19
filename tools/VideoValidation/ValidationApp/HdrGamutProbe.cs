using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LightMediaRenderer.Avalonia.Controls;

namespace LightStudio.VideoValidation;

/// <summary>One flat patch of the gamut probe pattern, in BT.2020 absolute luminance.</summary>
public sealed record HdrProbePatch(
    int Column,
    int Row,
    string Label,
    double RedNits,
    double GreenNits,
    double BlueNits);

public sealed record HdrProbeColor(double Red, double Green, double Blue);

public sealed record HdrProbePatchResult
{
    public string Label { get; init; } = string.Empty;

    /// <summary>Mean of the CPU reference over the patch interior, in output linear units.</summary>
    public HdrProbeColor Expected { get; init; } = new(0, 0, 0);

    /// <summary>Mean of the native float capture over the same interior.</summary>
    public HdrProbeColor Actual { get; init; } = new(0, 0, 0);

    public double MaximumAbsoluteError { get; init; }

    public double Tolerance { get; init; }

    /// <summary>Whether a component the reference puts outside the sRGB gamut survived as negative.</summary>
    public bool NegativesPreserved { get; init; }

    public bool Matches { get; init; }
}

public sealed record HdrGamutProbeReport
{
    /// <summary>False when the surface is bounded, where out-of-gamut output cannot be measured.</summary>
    public bool Applicable { get; init; }

    public bool Matches { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<HdrProbePatchResult> Patches { get; init; } = [];

    public static HdrGamutProbeReport NotApplicable(string message) => new() { Message = message };
}

/// <summary>
/// Measures flat patches of known luminance and saturation instead of comparing whole frames.
/// </summary>
/// <remarks>
/// <para>
/// A whole-frame metric cannot decide a resized picture: the reference is not resized, so the
/// filter's own ringing lands in the same error budget as a genuine defect, and averaging over
/// every pixel hides a handful of badly wrong ones. Reading only the interior of a flat patch
/// removes both problems, because every resampling filter reproduces the interior of a flat region
/// exactly. What remains in the difference is the color pipeline.
/// </para>
/// <para>
/// The patches are chosen for the places the extended-range path can quietly fail. Saturated
/// BT.2020 primaries become negative BT.709 components, which are valid scRGB and must reach the
/// compositor intact; peak-luminance patches sit at the top of the range an intermediate buffer has
/// to hold; and the darkest patches sit far enough below the fp16 normal minimum to expose a
/// scratch surface that flushes denormals to zero.
/// </para>
/// </remarks>
public static class HdrGamutProbe
{
    public const int Columns = 4;
    public const int Rows = 3;

    /// <summary>Fraction of each patch read, centered, leaving the rest as a margin for ringing.</summary>
    public const double InteriorFraction = 0.5;

    private const double RelativeTolerance = 0.02;
    private const double AbsoluteTolerance = 1e-4;

    /// <summary>Magnitude below which a negative component is too small to conclude anything from.</summary>
    private const double NegativeFloor = 0.01;

    private const double ReferenceWhiteNits = 203;

    public static IReadOnlyList<HdrProbePatch> Patches { get; } =
    [
        new(0, 0, "BT.2020 green at reference white", 0, ReferenceWhiteNits, 0),
        new(1, 0, "BT.2020 red at reference white", ReferenceWhiteNits, 0, 0),
        new(2, 0, "BT.2020 blue at reference white", 0, 0, ReferenceWhiteNits),
        new(3, 0, "BT.2020 cyan at reference white", 0, ReferenceWhiteNits, ReferenceWhiteNits),
        new(0, 1, "Neutral 1000 nits", 1000, 1000, 1000),
        new(1, 1, "Neutral 4000 nits", 4000, 4000, 4000),
        new(2, 1, "BT.2020 green at 4000 nits", 0, 4000, 0),
        new(3, 1, "BT.2020 red at the PQ maximum", 10000, 0, 0),
        new(0, 2, "Neutral reference white", ReferenceWhiteNits, ReferenceWhiteNits, ReferenceWhiteNits),
        new(1, 2, "Neutral 10 nits", 10, 10, 10),
        new(2, 2, "Neutral 1 nit", 1, 1, 1),
        new(3, 2, "Neutral 0.1 nits", 0.1, 0.1, 0.1),
    ];

    public static HdrGamutProbeReport Evaluate(VideoRenderCapture capture, HdrExpectedFrame expected)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(expected);
        if (capture.NativeRgbaF32 is not { } actual)
            return HdrGamutProbeReport.NotApplicable("The capture has no native float pixels to probe.");

        var results = new List<HdrProbePatchResult>(Patches.Count);
        foreach (HdrProbePatch patch in Patches)
        {
            (int left, int right) = Interior(patch.Column, Columns, expected.Width);
            (int top, int bottom) = Interior(patch.Row, Rows, expected.Height);
            double expectedRed = 0;
            double expectedGreen = 0;
            double expectedBlue = 0;
            double actualRed = 0;
            double actualGreen = 0;
            double actualBlue = 0;
            int samples = 0;

            for (int y = top; y < bottom; y++)
            {
                int captureY = Math.Min((int)((y + 0.5) * capture.Height / expected.Height), capture.Height - 1);
                for (int x = left; x < right; x++)
                {
                    int captureX = Math.Min((int)((x + 0.5) * capture.Width / expected.Width), capture.Width - 1);
                    int expectedOffset = checked((y * expected.Width + x) * 4);
                    int actualOffset = checked((captureY * capture.Width + captureX) * 4);
                    expectedRed += expected.Rgba[expectedOffset];
                    expectedGreen += expected.Rgba[expectedOffset + 1];
                    expectedBlue += expected.Rgba[expectedOffset + 2];
                    actualRed += actual[actualOffset];
                    actualGreen += actual[actualOffset + 1];
                    actualBlue += actual[actualOffset + 2];
                    samples++;
                }
            }

            if (samples == 0)
                return HdrGamutProbeReport.NotApplicable("The probe pattern has no interior to read.");

            var expectedColor = new HdrProbeColor(expectedRed / samples, expectedGreen / samples, expectedBlue / samples);
            var actualColor = new HdrProbeColor(actualRed / samples, actualGreen / samples, actualBlue / samples);
            results.Add(Compare(patch.Label, expectedColor, actualColor));
        }

        HdrProbePatchResult[] clipped = results.Where(result => !result.NegativesPreserved).ToArray();
        HdrProbePatchResult[] failed = results.Where(result => !result.Matches).ToArray();
        bool matches = failed.Length == 0;
        string message = matches
            ? $"All {results.Count} probe patches match the linear reference."
            : clipped.Length > 0
                ? $"{clipped.Length} of {results.Count} patches lost their out-of-gamut components " +
                    $"({Describe(clipped[0])}); {failed.Length} patches diverged in total."
                : $"{failed.Length} of {results.Count} patches diverged ({Describe(failed[0])}).";

        return new HdrGamutProbeReport
        {
            Applicable = true,
            Matches = matches,
            Message = message,
            Patches = results,
        };
    }

    private static HdrProbePatchResult Compare(string label, HdrProbeColor expected, HdrProbeColor actual)
    {
        double redError = Math.Abs(actual.Red - expected.Red);
        double greenError = Math.Abs(actual.Green - expected.Green);
        double blueError = Math.Abs(actual.Blue - expected.Blue);
        double tolerance = Math.Max(
            AbsoluteTolerance,
            RelativeTolerance * Math.Max(
                Math.Abs(expected.Red),
                Math.Max(Math.Abs(expected.Green), Math.Abs(expected.Blue))));
        double maximumError = Math.Max(redError, Math.Max(greenError, blueError));
        bool finite =
            double.IsFinite(actual.Red) && double.IsFinite(actual.Green) && double.IsFinite(actual.Blue);

        return new HdrProbePatchResult
        {
            Label = label,
            Expected = expected,
            Actual = actual,
            MaximumAbsoluteError = maximumError,
            Tolerance = tolerance,
            NegativesPreserved =
                Preserved(expected.Red, actual.Red) &&
                Preserved(expected.Green, actual.Green) &&
                Preserved(expected.Blue, actual.Blue),
            Matches = finite && maximumError <= tolerance,
        };
    }

    /// <summary>
    /// Whether a component the reference puts well outside the gamut is still outside it. A value
    /// that arrives at or above zero has been clipped rather than merely rounded.
    /// </summary>
    private static bool Preserved(double expected, double actual) =>
        expected > -NegativeFloor || actual < -NegativeFloor / 4;

    private static (int Start, int End) Interior(int index, int count, int extent)
    {
        int start = index * extent / count;
        int end = (index + 1) * extent / count;
        int margin = (int)((end - start) * (1 - InteriorFraction) / 2);
        return (start + margin, Math.Max(end - margin, start + margin + 1));
    }

    private static string Describe(HdrProbePatchResult result) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Label}: expected {Format(result.Expected)}, measured {Format(result.Actual)}");

    private static string Format(HdrProbeColor color) =>
        string.Create(CultureInfo.InvariantCulture, $"({color.Red:0.####}, {color.Green:0.####}, {color.Blue:0.####})");
}
