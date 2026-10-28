using System;
using System.Collections.Concurrent;
using System.Numerics;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Rendering;

/// <summary>
/// The chromaticities each set of primaries names, and the linear-light matrices between them.
/// </summary>
/// <remarks>
/// <para>
/// A frame's primaries say what its R, G and B actually are. Nothing downstream can put a color
/// where it belongs without them: the same code point is a different color in every gamut, and
/// showing one gamut's signal on another's display without this matrix is what makes P3 content
/// look oversaturated on an sRGB screen and BT.709 content look flat on a wide one.
/// </para>
/// <para>
/// The matrices are derived from the published chromaticities rather than tabulated, because the
/// derivation is short, exact and the same for every pair, whereas a table has to be extended by
/// hand every time a gamut is added and is wrong in a way nothing can see.
/// </para>
/// </remarks>
internal static class VideoColorGamut
{
    /// <summary>The chromaticities of one set of primaries, as CIE 1931 xy.</summary>
    public readonly record struct Chromaticities(
        double RedX,
        double RedY,
        double GreenX,
        double GreenY,
        double BlueX,
        double BlueY,
        double WhiteX,
        double WhiteY);

    /// <summary>Everything a draw has to know about a source and target gamut, worked out once.</summary>
    /// <param name="Matrix">Linear source RGB to linear target RGB, row major.</param>
    /// <param name="Luma">Weights giving luminance from linear RGB still in the source's primaries.</param>
    /// <param name="MaximumComponentGain">The most a single component can grow crossing to the target.</param>
    public sealed record GamutConversion(
        double[] Matrix,
        (double Red, double Green, double Blue) Luma,
        double MaximumComponentGain);

    private static readonly ConcurrentDictionary<(VideoColorPrimaries Source, VideoColorPrimaries Target), GamutConversion> Conversions = new();
    private static readonly ConcurrentDictionary<VideoColorPrimaries, (float[] Matrix, float[] Luma)> SingleConversions = new();

    private const double D65X = 0.3127;
    private const double D65Y = 0.3290;

    // Bradford cone response, which is what every color management stack adapts white points with.
    private static readonly double[] BradfordForward =
    [
        0.8951, 0.2664, -0.1614,
        -0.7502, 1.7135, 0.0367,
        0.0389, -0.0685, 1.0296,
    ];

    private static readonly double[] BradfordInverse =
    [
        0.9869929, -0.1470543, 0.1599627,
        0.4323053, 0.5183603, 0.0492912,
        -0.0085287, 0.0400428, 0.9684867,
    ];

    public static Chromaticities For(VideoColorPrimaries primaries) => primaries switch
    {
        VideoColorPrimaries.Bt2020 =>
            new(0.708, 0.292, 0.170, 0.797, 0.131, 0.046, D65X, D65Y),
        VideoColorPrimaries.Smpte170M =>
            new(0.630, 0.340, 0.310, 0.595, 0.155, 0.070, D65X, D65Y),
        VideoColorPrimaries.Bt470Bg =>
            new(0.640, 0.330, 0.290, 0.600, 0.150, 0.060, D65X, D65Y),

        // System M was specified on illuminant C, not D65.
        VideoColorPrimaries.Bt470M =>
            new(0.670, 0.330, 0.210, 0.710, 0.140, 0.080, 0.31006, 0.31616),
        VideoColorPrimaries.DisplayP3 =>
            new(0.680, 0.320, 0.265, 0.690, 0.150, 0.060, D65X, D65Y),
        VideoColorPrimaries.DciP3 =>
            new(0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.314, 0.351),
        _ =>
            new(0.640, 0.330, 0.300, 0.600, 0.150, 0.060, D65X, D65Y),
    };

    /// <summary>
    /// What it takes to show these primaries on the target. Derived on first use and kept,
    /// because a draw asks for it every frame and the answer only depends on the two gamuts.
    /// </summary>
    public static GamutConversion ToTarget(VideoColorPrimaries source, VideoColorPrimaries target = VideoColorPrimaries.Bt709) =>
        Conversions.GetOrAdd((source, target), static value =>
        {
            double[] matrix = Matrix(value.Source, value.Target);
            double[] toXyz = RgbToXyz<double>(For(value.Source));
            double gain = 0;
            for (int row = 0; row < 3; row++)
            {
                double sum = 0;
                for (int column = 0; column < 3; column++)
                    sum += Math.Max(matrix[(row * 3) + column], 0);
                gain = Math.Max(gain, sum);
            }

            return new GamutConversion(matrix, (toXyz[3], toXyz[4], toXyz[5]), gain);
        });

    public static (float[] Matrix, float[] Luma) ToBt709Single(VideoColorPrimaries primaries) =>
        SingleConversions.GetOrAdd(primaries, static value =>
        {
            float[] toXyz = RgbToXyz<float>(For(value));
            return (Matrix<float>(value, VideoColorPrimaries.Bt709), [toXyz[3], toXyz[4], toXyz[5]]);
        });

    /// <summary>
    /// The weights that turn linear RGB in these primaries into luminance, which is the Y row of
    /// their conversion to XYZ. Used wherever a stage has to know how bright a color is before it
    /// has been moved to the display's primaries, such as the HLG OOTF and the tone curve.
    /// </summary>
    public static (double Red, double Green, double Blue) LumaCoefficients(VideoColorPrimaries primaries) =>
        ToTarget(primaries).Luma;

    /// <summary>
    /// The 3x3 matrix taking linear light in <paramref name="source"/> to linear light in
    /// <paramref name="target"/>, in row-major order. White points that differ are reconciled with
    /// a Bradford adaptation, so a color that was white in the source stays white in the target.
    /// </summary>
    public static double[] Matrix(VideoColorPrimaries source, VideoColorPrimaries target) =>
        Matrix<double>(source, target);

    private static T[] Matrix<T>(VideoColorPrimaries source, VideoColorPrimaries target)
        where T : IFloatingPointIeee754<T>
    {
        Chromaticities from = For(source);
        Chromaticities to = For(target);
        T[] sourceToXyz = RgbToXyz<T>(from);
        if (T.Abs(T.CreateChecked(from.WhiteX) - T.CreateChecked(to.WhiteX)) > T.CreateChecked(1e-9) ||
            T.Abs(T.CreateChecked(from.WhiteY) - T.CreateChecked(to.WhiteY)) > T.CreateChecked(1e-9))
            sourceToXyz = Multiply(Adaptation<T>(from, to), sourceToXyz);

        return Multiply(Invert(RgbToXyz<T>(to)), sourceToXyz);
    }

    /// <summary>
    /// Whether a frame's primaries differ from the output primaries.
    /// </summary>
    public static bool NeedsConversion(VideoColorPrimaries primaries, VideoColorPrimaries target = VideoColorPrimaries.Bt709) =>
        primaries != target;

    /// <summary>
    /// The largest a single component can grow crossing from <paramref name="source"/> into
    /// <paramref name="target"/>, which is the row of the matrix with the largest positive sum. A display's headroom
    /// has to cover that, not just the source's own peak, or a saturated highlight clips.
    /// </summary>
    public static double MaximumComponentGain(VideoColorPrimaries source, VideoColorPrimaries target = VideoColorPrimaries.Bt709) =>
        ToTarget(source, target).MaximumComponentGain;

    /// <summary>
    /// The matrix taking linear RGB in these primaries to CIE XYZ. Each primary contributes its own
    /// chromaticity scaled so that RGB (1, 1, 1) lands exactly on the declared white point.
    /// </summary>
    private static T[] RgbToXyz<T>(Chromaticities chromaticities) where T : IFloatingPointIeee754<T>
    {
        T rx = T.CreateChecked(chromaticities.RedX), ry = T.CreateChecked(chromaticities.RedY);
        T gx = T.CreateChecked(chromaticities.GreenX), gy = T.CreateChecked(chromaticities.GreenY);
        T bx = T.CreateChecked(chromaticities.BlueX), by = T.CreateChecked(chromaticities.BlueY);
        T[] xyz =
        [
            rx / ry, gx / gy, bx / by,
            T.One, T.One, T.One,
            (T.One - rx - ry) / ry,
            (T.One - gx - gy) / gy,
            (T.One - bx - by) / by,
        ];

        T[] scale = Transform(Invert(xyz), WhiteXyz<T>(chromaticities));
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                xyz[(row * 3) + column] *= scale[column];
        }

        return xyz;
    }

    private static T[] Adaptation<T>(Chromaticities from, Chromaticities to) where T : IFloatingPointIeee754<T>
    {
        T[] forward = Convert<T>(BradfordForward), inverse = Convert<T>(BradfordInverse);
        T[] sourceCone = Transform(forward, WhiteXyz<T>(from));
        T[] targetCone = Transform(forward, WhiteXyz<T>(to));
        T[] ratio =
        [
            targetCone[0] / sourceCone[0], T.Zero, T.Zero,
            T.Zero, targetCone[1] / sourceCone[1], T.Zero,
            T.Zero, T.Zero, targetCone[2] / sourceCone[2],
        ];
        return Multiply(inverse, Multiply(ratio, forward));
    }

    private static T[] WhiteXyz<T>(Chromaticities chromaticities) where T : IFloatingPointIeee754<T>
    {
        T x = T.CreateChecked(chromaticities.WhiteX), y = T.CreateChecked(chromaticities.WhiteY);
        return [x / y, T.One, (T.One - x - y) / y];
    }

    private static T[] Convert<T>(double[] values) where T : IFloatingPointIeee754<T>
    {
        var result = new T[values.Length];
        for (int i = 0; i < values.Length; i++)
            result[i] = T.CreateChecked(values[i]);
        return result;
    }

    private static T[] Multiply<T>(T[] left, T[] right) where T : IFloatingPointIeee754<T>
    {
        var result = new T[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                T sum = T.Zero;
                for (int inner = 0; inner < 3; inner++)
                    sum += left[(row * 3) + inner] * right[(inner * 3) + column];
                result[(row * 3) + column] = sum;
            }
        }

        return result;
    }

    private static T[] Transform<T>(T[] matrix, T[] vector) where T : IFloatingPointIeee754<T> =>
    [
        (matrix[0] * vector[0]) + (matrix[1] * vector[1]) + (matrix[2] * vector[2]),
        (matrix[3] * vector[0]) + (matrix[4] * vector[1]) + (matrix[5] * vector[2]),
        (matrix[6] * vector[0]) + (matrix[7] * vector[1]) + (matrix[8] * vector[2]),
    ];

    private static T[] Invert<T>(T[] matrix) where T : IFloatingPointIeee754<T>
    {
        T cofactor0 = (matrix[4] * matrix[8]) - (matrix[5] * matrix[7]);
        T cofactor1 = (matrix[5] * matrix[6]) - (matrix[3] * matrix[8]);
        T cofactor2 = (matrix[3] * matrix[7]) - (matrix[4] * matrix[6]);
        T determinant = (matrix[0] * cofactor0) + (matrix[1] * cofactor1) + (matrix[2] * cofactor2);
        if (T.Abs(determinant) < T.CreateChecked(1e-12))
            throw new InvalidOperationException("The primaries are degenerate and have no inverse.");

        T reciprocal = T.One / determinant;
        return
        [
            cofactor0 * reciprocal,
            ((matrix[2] * matrix[7]) - (matrix[1] * matrix[8])) * reciprocal,
            ((matrix[1] * matrix[5]) - (matrix[2] * matrix[4])) * reciprocal,
            cofactor1 * reciprocal,
            ((matrix[0] * matrix[8]) - (matrix[2] * matrix[6])) * reciprocal,
            ((matrix[2] * matrix[3]) - (matrix[0] * matrix[5])) * reciprocal,
            cofactor2 * reciprocal,
            ((matrix[1] * matrix[6]) - (matrix[0] * matrix[7])) * reciprocal,
            ((matrix[0] * matrix[4]) - (matrix[1] * matrix[3])) * reciprocal,
        ];
    }
}
