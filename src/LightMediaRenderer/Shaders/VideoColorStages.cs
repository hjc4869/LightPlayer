using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// What one component's Dolby Vision reshaping curve asks of a program, so the emitted code and the
/// uniforms it reads are decided from one description rather than from the format's worst case.
/// </summary>
internal readonly record struct DolbyVisionCurveShape
{
    public int PieceCount { get; init; }

    /// <summary>Whether any piece is a polynomial, and so reads the coefficients.</summary>
    public bool Polynomial { get; init; }

    /// <summary>The highest MMR order any piece uses, or zero where no piece is MMR.</summary>
    public int MmrOrder { get; init; }

    /// <summary>Whether the curve maps every value to itself and can be left out of the program.</summary>
    public bool Identity { get; init; }

    /// <summary>Whether the pivots are read, for a branch or for the clamp that ends a piece.</summary>
    public bool Pivots { get; init; }
}

/// <summary>
/// The SkSL each stage contributes. Stages talk to each other through the functions below and
/// nothing else, so one can be replaced without the rest knowing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>float3 sampleSource(float2 coord)</c> -- the decoder's samples, in the layout it wrote.</item>
/// <item><c>float3 sampleFrame(float2 coord)</c> -- the same, with the frame's two fields separated
/// again where they were sampled at different instants.</item>
/// <item><c>float3 decodeSignal(float2 coord)</c> -- non-linear R'G'B' in the source's primaries.</item>
/// <item><c>float3 decodeTransfer(float3 signal)</c> -- linear light, absolute nits where the source
/// carries them, still in the source's primaries.</item>
/// <item><c>float3 toneMap(float3 nits)</c> -- the same, brought within the display's volume.</item>
/// <item><c>float3 toScene(float3 nits)</c> -- scene light: linear, non-negative, source primaries,
/// one unit being what the surface stores as 1.0.</item>
/// <item><c>float3 scaleScene(float2 coord)</c> -- scene light resampled to the destination.</item>
/// <item><c>float3 convertGamut(float3 scene)</c> -- the display's primaries, where a color outside
/// them becomes a negative component.</item>
/// <item><c>float3 fitRange(float3 rgb)</c> -- the same, within what the surface can carry.</item>
/// <item><c>float3 encodeOutput(float3 rgb, float2 coord)</c> -- what the surface stores.</item>
/// </list>
/// <para>
/// Each stage is emitted in exactly one shape per draw rather than branching on a mode uniform, so
/// a program contains only the arithmetic that frame needs.
/// </para>
/// </remarks>
internal static class VideoColorStages
{
    // Keep color equations in sync with ../Software/VideoCpuColorPipeline.cs and ../Software/VideoCpuPreview.cs.
    /// <summary>Transfer functions, needed by any stage that works in light rather than in signal.</summary>
    public const string LinearHelpers = """
        float pqToNits(float signal) {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;
            float power = pow(max(signal, 0.0), 1.0 / m2);
            float level = max(power - c1, 0.0) / max(c2 - c3 * power, 1e-6);
            return 10000.0 * pow(level, 1.0 / m1);
        }

        float3 pqToNits3(float3 signal) {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;
            float3 power = pow(max(signal, float3(0.0)), float3(1.0 / m2));
            float3 level = max(power - float3(c1), float3(0.0))
                / max(float3(c2) - c3 * power, float3(1e-6));
            return 10000.0 * pow(level, float3(1.0 / m1));
        }

        float nitsToPq(float nits) {
            if (nits <= 0.0) {
                return 0.0;
            }

            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;
            float level = pow(clamp(nits / 10000.0, 0.0, 1.0), m1);
            return pow((c1 + c2 * level) / (1.0 + c3 * level), m2);
        }

        float hlgToScene(float signal) {
            const float a = 0.17883277;
            const float b = 0.28466892;
            const float c = 0.55991073;
            return signal <= 0.5
                ? signal * signal / 3.0
                : (exp((signal - c) / a) + b) / 12.0;
        }

        float srgbToLinear(float signal) {
            return signal <= 0.04045
                ? signal / 12.92
                : pow((signal + 0.055) / 1.055, 2.4);
        }
        """;

    /// <summary>Everything the picture passes through once it is in the display's primaries.</summary>
    public const string OutputHelpers = """
        float3 compressGamut(float3 rgb) {
            // Clipping negative components carries a primary outside the display gamut onto the
            // display's matching primary.
            rgb = max(rgb, float3(0.0));

            // Components above the knee roll off per channel so the largest lands on 1, keeping
            // saturated highlights bright. Values already in gamut remain untouched.
            float maximum = max(rgb.r, max(rgb.g, rgb.b));
            if (maximum > 1.0) {
                const float knee = 0.7;
                float strength = (maximum - 1.0) / ((1.0 - knee) * (maximum - knee));
                float3 excess = max(rgb - float3(knee), float3(0.0));
                rgb = min(rgb, float3(knee)) + excess / (1.0 + strength * excess);
            }

            return rgb;
        }

        float3 fitExtendedRange(float3 rgb) {
            // Negative components represent valid colors outside sRGB, so retain them for the
            // platform color manager. Only a positive component beyond the mapped display peak has
            // to come in; scaling every channel together preserves its chromaticity.
            float maximum = max(rgb.r, max(rgb.g, rgb.b));
            return maximum > 1.0 ? rgb / maximum : rgb;
        }
        """;

    public const string DitherHelpers = """
        float gradientNoise(float2 pixel, float seed) {
            return fract(52.9829189 * fract(
                dot(pixel, float2(0.06711056, 0.00583715)) + seed));
        }

        float dither(float2 coord) {
            float3 position = float3(coord, 1.0);
            float reciprocalW = 1.0 / max(dot(ditherW, position), 1e-6);
            float2 pixel = floor(float2(
                dot(ditherX, position),
                dot(ditherY, position)) * reciprocalW);
            float first = gradientNoise(pixel, ditherParams.y);
            float second = gradientNoise(pixel.yx + float2(37.0, 17.0), ditherParams.y + 0.61803399);
            return (first - second) * ditherParams.x;
        }

        float3 quantize(float3 encoded, float2 coord) {
            float3 level = floor((encoded + dither(coord)) * ditherParams.z + 0.5) / ditherParams.z;
            return clamp(floor(level * ditherParams.w + 0.5) / ditherParams.w, 0.0, 1.0);
        }
        """;

    // How wide one unit of the quantized signal is in linear light at this level, taken from the
    // slope of the encoding whatever quantizes an extended-range surface will apply. A step of a
    // fixed size in light would be inaudible in the highlights and deafening in the shadows; this
    // keeps the noise one code point wherever it lands.
    private const string EncodedStepSrgb = """
        float encodedStep(float light) {
            float slope = light <= 0.0031308
                ? 12.92
                : (1.055 / 2.4) * pow(max(light, 1e-4), 1.0 / 2.4 - 1.0);
            return 1.0 / slope;
        }
        """;

    // A pure power law has no slope at all at zero, so the floor stands in for the toe the sRGB
    // curve has.
    private const string EncodedStepPower = """
        float encodedStep(float light) {
            return surfaceParams.z * pow(max(light, 1e-4), 1.0 - surfaceParams.w);
        }
        """;

    // A single-component plane is read through .a whatever its depth, and an interleaved chroma
    // plane through .rg, so subsampling, depth, alignment and chroma order all fold into the plane
    // images and the matrix rather than into the program.
    public const string SamplePlanar = """
        float3 sampleSource(float2 coord) {
            return float3(
                float(luma.eval(coord).a),
                float(cb.eval(coord).a),
                float(cr.eval(coord).a));
        }
        """;

    public const string SampleInterleaved = """
        float3 sampleSource(float2 coord) {
            return float3(float(luma.eval(coord).a), float2(chroma.eval(coord).rg));
        }
        """;

    private const string ClampPlaneCoordinates = """
        float2 planeCoord(float2 coord, float4 bounds) {
            return clamp(coord, bounds.xy, bounds.zw);
        }
        """;

    public const string SamplePlanarClamped = ClampPlaneCoordinates + """
        float3 sampleSource(float2 coord) {
            return float3(
                float(luma.eval(planeCoord(coord, lumaBounds)).a),
                float(cb.eval(planeCoord(coord, chromaBounds)).a),
                float(cr.eval(planeCoord(coord, chromaBounds)).a));
        }
        """;

    public const string SampleInterleavedClamped = ClampPlaneCoordinates + """
        float3 sampleSource(float2 coord) {
            return float3(
                float(luma.eval(planeCoord(coord, lumaBounds)).a),
                float2(chroma.eval(planeCoord(coord, chromaBounds)).rg));
        }
        """;

    // Backends without a 16-bit texture format take those planes as byte pairs instead. Rebuilding
    // the sample is a linear function of the two bytes, and bilinear filtering is itself linear, so
    // filtering the bytes separately and combining them afterwards lands on the same value the
    // hardware would have reached filtering whole samples: the loss is precision, not correctness.
    public const string SampleWidePlanar = """
        float wide(float2 pair) {
            return dot(pair, float2(255.0, 65280.0) / 65535.0);
        }

        float3 sampleSource(float2 coord) {
            return float3(
                wide(float2(luma.eval(coord).rg)),
                wide(float2(cb.eval(coord).rg)),
                wide(float2(cr.eval(coord).rg)));
        }
        """;

    public const string SampleWideInterleaved = """
        float wide(float2 pair) {
            return dot(pair, float2(255.0, 65280.0) / 65535.0);
        }

        float3 sampleSource(float2 coord) {
            float4 pairs = float4(chroma.eval(coord));
            return float3(
                wide(float2(luma.eval(coord).rg)),
                wide(pairs.rg),
                wide(pairs.ba));
        }
        """;

    public const string SampleWidePlanarClamped = ClampPlaneCoordinates + """
        float wide(float2 pair) {
            return dot(pair, float2(255.0, 65280.0) / 65535.0);
        }

        float3 sampleSource(float2 coord) {
            return float3(
                wide(float2(luma.eval(planeCoord(coord, lumaBounds)).rg)),
                wide(float2(cb.eval(planeCoord(coord, chromaBounds)).rg)),
                wide(float2(cr.eval(planeCoord(coord, chromaBounds)).rg)));
        }
        """;

    public const string SampleWideInterleavedClamped = ClampPlaneCoordinates + """
        float wide(float2 pair) {
            return dot(pair, float2(255.0, 65280.0) / 65535.0);
        }

        float3 sampleSource(float2 coord) {
            float4 pairs = float4(chroma.eval(planeCoord(coord, chromaBounds)));
            return float3(
                wide(float2(luma.eval(planeCoord(coord, lumaBounds)).rg)),
                wide(pairs.rg),
                wide(pairs.ba));
        }
        """;

    public const string SampleInterlacedChroma = """

        float2 sampleFieldChroma(float2 pixel) {
            float2 coord = (pixel - fieldChromaScale.zw) * fieldChromaScale.xy
                - fieldLumaScale.xy * fieldLumaScale.zw;
            return samplePlanes(coord).yz;
        }

        float3 sampleSource(float2 coord) {
            float2 native = coord / fieldLumaScale.xy + fieldLumaScale.zw;
            float field = mod(floor(native.y), 2.0);
            float2 fieldCoord = float2(native.x, (native.y - field + 0.5) * 0.5);
            float2 chroma = fieldCoord * fieldLumaScale.xy / fieldChromaScale.xy
                + fieldChromaScale.zw;
            float2 lower = floor(chroma - 0.5);
            float2 blend = chroma - 0.5 - lower;
            if (fieldChromaSize.z == 0.0) {
                lower = floor(chroma);
                blend = float2(0.0);
            }
            float2 maximum = float2(fieldChromaSize.x - 1.0,
                max(floor((fieldChromaSize.y - 1.0 - field) * 0.5), 0.0));
            float2 first = clamp(lower, float2(0.0), maximum);
            float2 last = clamp(lower + 1.0, float2(0.0), maximum);
            first = float2(first.x + 0.5, min(first.y * 2.0 + field, fieldChromaSize.y - 1.0) + 0.5);
            last = float2(last.x + 0.5, min(last.y * 2.0 + field, fieldChromaSize.y - 1.0) + 0.5);
            float2 upperChroma = mix(sampleFieldChroma(first),
                sampleFieldChroma(float2(last.x, first.y)), blend.x);
            float2 lowerChroma = mix(sampleFieldChroma(float2(first.x, last.y)),
                sampleFieldChroma(last), blend.x);
            return float3(samplePlanes(coord).x, mix(upperChroma, lowerChroma, blend.y));
        }
        """;

    public const string SamplePacked = """
        float3 sampleSource(float2 coord) {
            return float4(image.eval(coord)).rgb;
        }
        """;

    public const string DeinterlaceNone = """
        float3 sampleFrame(float2 coord) {
            return sampleSource(coord);
        }
        """;

    public const string SamplePreviousFrame = """
        float3 samplePrevious(float2 coord) {
            return float4(previousSamples.eval(coord)).rgb;
        }
        """;

    public const string SampleCurrentFrame = """
        float3 samplePrevious(float2 coord) {
            return sampleSource(coord);
        }
        """;

    /// <summary>Weaves unchanged regions and reconstructs moving regions from the first field.</summary>
    public const string DeinterlaceMotionAdaptive = """
        float3 sampleFrame(float2 coord) {
            coord = clamp(floor(coord) + 0.5, float2(0.5), deinterlaceSize.xy - 0.5);
            float3 here = sampleSource(coord);
            if (mod(floor(coord.y), 2.0) == deinterlaceParams.x || deinterlaceSize.y < 2.0) {
                return here;
            }
            float first = deinterlaceParams.x + 0.5;
            float last = first + 2.0 * floor((deinterlaceSize.y - 1.0 - deinterlaceParams.x) * 0.5);
            float2 aboveCoord = float2(coord.x, clamp(coord.y - 1.0, first, last));
            float2 belowCoord = float2(coord.x, clamp(coord.y + 1.0, first, last));
            float3 above = sampleSource(aboveCoord);
            float3 below = sampleSource(belowCoord);
            float3 motion = max(abs(here - samplePrevious(coord)),
                max(abs(above - samplePrevious(aboveCoord)), abs(below - samplePrevious(belowCoord))));
            motion *= deinterlaceSize.z;
            float weight = smoothstep(
                deinterlaceParams.y,
                deinterlaceParams.z,
                max(motion.x, max(motion.y, motion.z)));
            if (weight == 0.0) {
                return here;
            }

            float left = max(coord.x - 1.0, 0.5);
            float right = min(coord.x + 1.0, deinterlaceSize.x - 0.5);
            float3 upLeft = sampleSource(float2(left, aboveCoord.y));
            float3 upRight = sampleSource(float2(right, aboveCoord.y));
            float3 downLeft = sampleSource(float2(left, belowCoord.y));
            float3 downRight = sampleSource(float2(right, belowCoord.y));
            float leftCost = dot(abs(upLeft - downRight), float3(1.0)) * deinterlaceSize.z + deinterlaceParams.w;
            float rightCost = dot(abs(upRight - downLeft), float3(1.0)) * deinterlaceSize.z + deinterlaceParams.w;
            float best = min(dot(abs(above - below), float3(1.0)) * deinterlaceSize.z, min(leftCost, rightCost));

            float3 spatial = 0.5 * (above + below);
            float takeLeft = step(leftCost, best);
            spatial = mix(spatial, 0.5 * (upLeft + downRight), takeLeft);
            spatial = mix(spatial, 0.5 * (upRight + downLeft), step(rightCost, best) * (1.0 - takeLeft));

            return mix(here, spatial, weight);
        }
        """;

    /// <summary>
    /// The color matrix, with the sample offset folded into <c>.w</c> so each channel is one dot
    /// product. Reading the planes through <c>sampleSource</c> rather than being handed a triple
    /// leaves room for a reconstruction that wants more than one tap per output pixel.
    /// </summary>
    public const string DecodeMatrix = """
        float3 decodeSignal(float2 coord) {
            float4 sample = float4(sampleFrame(coord), 1.0);
            return clamp(
                float3(dot(matrixR, sample), dot(matrixG, sample), dot(matrixB, sample)),
                0.0,
                1.0);
        }
        """;

    public const string DecodeIdentity = """
        float3 decodeSignal(float2 coord) {
            return clamp(sampleFrame(coord), 0.0, 1.0);
        }
        """;

    private const string DecodeDolbyVisionTail = """
            float4 affine = float4(reshaped, 1.0);
            float3 nonlinear = float3(
                dot(doviNonlinearMatrix[0], affine),
                dot(doviNonlinearMatrix[1], affine),
                dot(doviNonlinearMatrix[2], affine));
            float3 linearNits = pqToNits3(nonlinear);

            float3 rgbNits = float3(
                dot(doviLinearMatrix[0], linearNits),
                dot(doviLinearMatrix[1], linearNits),
                dot(doviLinearMatrix[2], linearNits));
            return clamp(rgbNits, 0.0, 10000.0);
        }

        float3 decodeSignal(float2 coord) {
            float3 nits = decodeDoviNits(coord);
            return float3(
                nitsToPq(nits.r),
                nitsToPq(nits.g),
                nitsToPq(nits.b));
        }
        """;

    public static string CreateDolbyVision(
        DolbyVisionMetadata metadata,
        bool swappedChroma,
        bool composeEnhancementLayer,
        bool enhancementSwappedChroma)
    {
        DolbyVisionCurveShape[] shapes = DescribeDolbyVision(metadata);
        int mmrOrder = 0;
        foreach (DolbyVisionCurveShape shape in shapes)
            mmrOrder = System.Math.Max(mmrOrder, shape.MmrOrder);

        var source = new System.Text.StringBuilder();
        for (int component = 0; component < shapes.Length; component++)
        {
            if (!shapes[component].Identity)
                source.Append(CreateDolbyVisionComponent(component, metadata.Curves[component], shapes[component]));
        }

        AppendDolbyVisionDecode(
            source,
            shapes,
            swappedChroma,
            mmrOrder,
            composeEnhancementLayer,
            enhancementSwappedChroma);
        return source.Append(DecodeDolbyVisionTail).ToString();
    }

    /// <summary>
    /// What each component's curve needs, so the emitted code and the uniforms it reads are decided
    /// once and cannot disagree.
    /// </summary>
    internal static DolbyVisionCurveShape[] DescribeDolbyVision(DolbyVisionMetadata metadata)
    {
        var shapes = new DolbyVisionCurveShape[DolbyVisionMetadata.ComponentCount];
        for (int component = 0; component < shapes.Length; component++)
            shapes[component] = DescribeDolbyVisionCurve(metadata.Curves[component]);
        return shapes;
    }

    public const string DecodeSignalSource = """
        float3 decodeSignal(float2 coord) {
            return clamp(float4(source.eval(coord)).rgb, 0.0, 1.0);
        }
        """;

    public const string DebandNone = """
        float3 processSignal(float2 coord) {
            return decodeSignal(coord);
        }
        """;

    public const string ProcessDolbyVisionNits = """
        float3 processSignal(float2 coord) {
            return decodeDoviNits(coord);
        }
        """;

    private const string DebandHelpers = """
        float debandRandom(float2 position, float seed) {
            return fract(52.9829189 * fract(
                dot(floor(position), float2(0.06711056, 0.00583715)) + seed));
        }

        float3 debandIteration(float2 coord, float3 value, float iteration) {
            float first = debandRandom(coord + float2(17.0 * iteration, 37.0), debandParams.z);
            float second = debandRandom(coord.yx + float2(53.0, 29.0 * iteration), debandParams.z + 0.61803399);
            float distance = first * iteration * debandParams.y;
            float angle = second * 6.28318531;
            float2 offset = distance * float2(cos(angle), sin(angle));
            float3 average = (
                decodeSignal(coord + float2(+offset.x, +offset.y)) +
                decodeSignal(coord + float2(-offset.x, +offset.y)) +
                decodeSignal(coord + float2(-offset.x, -offset.y)) +
                decodeSignal(coord + float2(+offset.x, -offset.y))) * 0.25;
            float3 difference = abs(value - average);
            float threshold = debandParams.x / iteration;
            return float3(
                difference.r > threshold ? value.r : average.r,
                difference.g > threshold ? value.g : average.g,
                difference.b > threshold ? value.b : average.b);
        }
        """;

    public const string DebandLow = DebandHelpers + """
        float3 processSignal(float2 coord) {
            return debandIteration(coord, decodeSignal(coord), 1.0);
        }
        """;

    public const string DebandMedium = DebandHelpers + """
        float3 processSignal(float2 coord) {
            float3 value = debandIteration(coord, decodeSignal(coord), 1.0);
            value = debandIteration(coord, value, 2.0);
            float grain = debandRandom(coord.yx + float2(11.0, 71.0), debandParams.z + 0.38196601) - 0.5;
            return clamp(value + grain * debandParams.w, 0.0, 1.0);
        }
        """;

    public const string DebandHigh = DebandHelpers + """
        float3 processSignal(float2 coord) {
            float3 value = debandIteration(coord, decodeSignal(coord), 1.0);
            value = debandIteration(coord, value, 2.0);
            value = debandIteration(coord, value, 3.0);
            value = debandIteration(coord, value, 4.0);
            float grain = debandRandom(coord.yx + float2(11.0, 71.0), debandParams.z + 0.38196601) - 0.5;
            return clamp(value + grain * debandParams.w, 0.0, 1.0);
        }
        """;

    public const string TransferPq = """
        float3 decodeTransfer(float3 signal) {
            return pqToNits3(signal);
        }
        """;

    public const string TransferHlg = """
        float3 decodeTransfer(float3 signal) {
            float3 scene = float3(
                hlgToScene(signal.r),
                hlgToScene(signal.g),
                hlgToScene(signal.b));
            float luminance = max(dot(sourceLuma.xyz, scene), 0.0);
            return scene * pow(max(luminance, 1e-6), sourceLuma.w - 1.0) * transferParams.x;
        }
        """;

    public const string TransferSrgb = """
        float3 decodeTransfer(float3 signal) {
            return float3(
                srgbToLinear(signal.r),
                srgbToLinear(signal.g),
                srgbToLinear(signal.b));
        }
        """;

    public const string TransferBt1886 = """
        float3 decodeTransfer(float3 signal) {
            return transferParams.x * pow(
                max(signal + float3(transferParams.y), float3(0.0)),
                float3(transferParams.z));
        }
        """;

    public const string TransferPower = """
        float3 decodeTransfer(float3 signal) {
            return pow(max(signal, float3(0.0)), float3(surfaceParams.x));
        }
        """;

    public const string TransferNone = """
        float3 decodeTransfer(float3 signal) {
            return signal;
        }
        """;

    public const string ToneBt2390 = """
        float3 toneMap(float3 nits) {
            float sourceMax = toneParams.x;
            float targetMax = toneParams.y;
            float maxLum = toneParams.z;
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float x = clamp(nitsToPq(luminance) / sourceMax, 0.0, 1.0);
            float knee = (1.0 + toneParams.w) * maxLum - toneParams.w;

            if (knee < 1.0 && x > knee) {
                float t = (x - knee) / (1.0 - knee);
                float t2 = t * t;
                float t3 = t2 * t;
                x = (2.0 * t3 - 3.0 * t2 + 1.0) * knee
                    + (t3 - 2.0 * t2 + t) * (1.0 - knee)
                    + (-2.0 * t3 + 3.0 * t2) * maxLum;
            }

            float mapped = pqToNits(min(x * sourceMax, targetMax));
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    public const string ToneBt2446A = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float level = pow(clamp(luminance / toneParams.x, 0.0, 1.0), 1.0 / 2.4);
            level = log(1.0 + (toneParams.z - 1.0) * level) / log(toneParams.z);
            if (level <= 0.7399) {
                level *= 1.0770;
            } else if (level < 0.9909) {
                level = (-1.1510 * level + 2.7811) * level - 0.6302;
            } else {
                level = 0.5 * level + 0.5;
            }
            level = (pow(toneParams.w, level) - 1.0) / (toneParams.w - 1.0);
            float mapped = pow(clamp(level, 0.0, 1.0), 2.4) * toneParams.y;
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    // The curves below are shader-only; toneParams.w holds the target peak in nits.

    public const string ToneClip = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float mapped = min(luminance, toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    /// <summary>Unchanged up to the knee in .x, then m * e / (m + e) over the excess e, with m in .y.</summary>
    public const string ToneMobius = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float excess = max(luminance - toneParams.x, 0.0);
            float mapped = min(luminance, toneParams.x) + toneParams.y * excess / (toneParams.y + excess);
            mapped = min(mapped, toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    /// <summary>L / (a * L + b), with a and b in .xy.</summary>
    public const string ToneReinhard = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float mapped = min(luminance / (toneParams.x * luminance + toneParams.y), toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    /// <summary>Input relative to the target peak, scaled by .x, with the source peak at .y.</summary>
    public const string ToneHable = """
        float hableFilmic(float x) {
            // Shoulder strength, linear strength, linear angle, toe strength, toe numerator and denominator.
            const float a = 0.15;
            const float b = 0.50;
            const float c = 0.10;
            const float d = 0.20;
            const float e = 0.02;
            const float f = 0.30;
            return (x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f) - e / f;
        }

        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float mapped = hableFilmic(luminance * toneParams.x) / hableFilmic(toneParams.y) * toneParams.w;
            mapped = min(mapped, toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    /// <summary>Unchanged up to the knee in .x, then a power law with the exponent in .y.</summary>
    public const string ToneGamma = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float mapped = luminance > toneParams.x
                ? toneParams.x * pow(luminance / toneParams.x, toneParams.y)
                : luminance;
            mapped = min(mapped, toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    /// <summary>
    /// PQ signal scaled by .x and limited to the target's PQ signal in .y. The clip is applied in
    /// nits as well, since the PQ round trip is not exact on every backend.
    /// </summary>
    public const string ToneLinear = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float mapped = pqToNits(min(nitsToPq(luminance) * toneParams.x, toneParams.y));
            mapped = min(mapped, toneParams.w);
            return nits * (luminance > 1e-6 ? mapped / luminance : 0.0);
        }
        """;

    public static readonly string ToneHdr10Plus = CreateHdr10PlusToneMap();

    public const string ToneSceneAdaptive = """
        float3 toneMap(float3 nits) {
            float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
            float level = clamp(nitsToPq(luminance), toneParams.x, toneParams.y);
            float x = level - toneCurve0.x;
            float mapped = x > 0.0
                ? ((toneCurve1.x * x + toneCurve1.y) * x + toneCurve1.z) * x
                : (toneCurve0.z * x + toneCurve0.w) * x;
            mapped = clamp(mapped + toneCurve0.y, toneParams.z, toneParams.w);
            float mappedNits = pqToNits(mapped);
            return nits * (luminance > 1e-6 ? mappedNits / luminance : 0.0);
        }
        """;

    public const string ToneNone = """
        float3 toneMap(float3 nits) {
            return nits;
        }
        """;

    public const string SceneScaled = """
        float3 toScene(float3 nits) {
            return nits * sceneParams.x;
        }
        """;

    public const string SceneNone = """
        float3 toScene(float3 nits) {
            return nits;
        }
        """;

    /// <summary>Reads one sample, leaving the filtering to whatever Skia was asked to do.</summary>
    public const string ScaleDirect = """
        float3 scaleScene(float2 coord) {
            return float4(source.eval(coord)).rgb;
        }
        """;

    public const string GamutMatrix = """
        float3 convertGamut(float3 scene) {
            float4 value = float4(scene, 1.0);
            return float3(dot(gamutR, value), dot(gamutG, value), dot(gamutB, value));
        }
        """;

    public const string GamutNone = """
        float3 convertGamut(float3 scene) {
            return scene;
        }
        """;

    public const string FitCompress = """
        float3 fitRange(float3 rgb) {
            return compressGamut(rgb / outputParams.x) * outputParams.x;
        }
        """;

    public const string FitExtended = """
        float3 fitRange(float3 rgb) {
            return fitExtendedRange(rgb / outputParams.x) * outputParams.x;
        }
        """;

    public const string FitNone = """
        float3 fitRange(float3 rgb) {
            return rgb;
        }
        """;

    /// <summary>An extended-range surface takes the light as it is; nothing is clipped or quantized.</summary>
    public const string EncodeLinear = """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return rgb;
        }
        """;

    /// <summary>
    /// The same, for a surface whose float values are quantized further down the chain: by a
    /// compositor handing an 8-bit signal on, or by a panel that resolves fewer bits than it takes.
    /// Nothing here can see that happen, so the noise is shaped for the encoding it will happen in
    /// rather than for the linear values being written.
    /// </summary>
    private const string EncodeLinearDitheredBody = """
        float3 encodeOutput(float3 rgb, float2 coord) {
            float amount = dither(coord);
            return rgb + amount * float3(
                encodedStep(rgb.r),
                encodedStep(rgb.g),
                encodedStep(rgb.b));
        }
        """;

    public const string EncodeLinearDitheredSrgb = EncodedStepSrgb + EncodeLinearDitheredBody;

    public const string EncodeLinearDitheredPower = EncodedStepPower + EncodeLinearDitheredBody;

    private const string LinearToSrgb = """
        float linearToSrgb(float signal) {
            return signal <= 0.0031308
                ? 12.92 * signal
                : 1.055 * pow(max(signal, 0.0), 1.0 / 2.4) - 0.055;
        }
        """;

    private const string LinearToPower = """
        float3 linearToPower(float3 signal) {
            return pow(max(signal, float3(0.0)), float3(surfaceParams.w));
        }
        """;

    // A bounded surface is quantized here rather than left to its own conversion, so a panel depth
    // below the surface's lands on the panel's grid, and then on the surface's own codes so that
    // nothing is left for a backend's half-precision store to round differently.
    public const string EncodeSrgbDithered = LinearToSrgb + """
        float3 encodeOutput(float3 rgb, float2 coord) {
            float3 encoded = float3(
                linearToSrgb(rgb.r),
                linearToSrgb(rgb.g),
                linearToSrgb(rgb.b));
            return quantize(encoded, coord);
        }
        """;

    public const string EncodeSrgb = LinearToSrgb + """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return clamp(
                float3(linearToSrgb(rgb.r), linearToSrgb(rgb.g), linearToSrgb(rgb.b)),
                0.0,
                1.0);
        }
        """;

    public const string EncodePowerDithered = LinearToPower + """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return quantize(linearToPower(rgb), coord);
        }
        """;

    public const string EncodePower = LinearToPower + """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return clamp(linearToPower(rgb), 0.0, 1.0);
        }
        """;

    /// <summary>The signal already carries the surface's encoding, so only quantization is left.</summary>
    public const string EncodeDirectDithered = """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return quantize(rgb, coord);
        }
        """;

    public const string EncodeDirect = """
        float3 encodeOutput(float3 rgb, float2 coord) {
            return clamp(rgb, 0.0, 1.0);
        }
        """;

    public const string MainSamples = """
        float4 main(float2 coord) {
            return float4(sampleSource(coord), 1.0);
        }
        """;

    public const string MainSignal = """
        float4 main(float2 coord) {
            return float4(processSignal(coord), 1.0);
        }
        """;

    public const string MainScene = """
        float4 main(float2 coord) {
            return float4(toScene(toneMap(decodeTransfer(processSignal(coord)))), 1.0);
        }
        """;

    public const string MainOutput = """
        float4 main(float2 coord) {
            return float4(encodeOutput(fitRange(convertGamut(scaleScene(coord))), coord), 1.0);
        }
        """;

    public const string MainFused = """
        float4 main(float2 coord) {
            float3 scene = toScene(toneMap(decodeTransfer(processSignal(coord))));
            return float4(encodeOutput(fitRange(convertGamut(scene)), coord), 1.0);
        }
        """;

    private static DolbyVisionCurveShape DescribeDolbyVisionCurve(DolbyVisionReshapingCurve curve)
    {
        int pieceCount = System.Math.Clamp(
            curve.PivotCount - 1,
            1,
            DolbyVisionReshapingCurve.MaximumPieces);
        bool polynomial = false;
        int mmrOrder = 0;
        for (int piece = 0; piece < pieceCount; piece++)
        {
            if (curve.Methods[piece] < 0.5f)
            {
                polynomial = true;
                continue;
            }

            mmrOrder = System.Math.Max(
                mmrOrder,
                System.Math.Clamp(
                    (int)curve.MmrOrders[piece],
                    1,
                    DolbyVisionReshapingCurve.MaximumMmrOrder));
        }

        // A profile whose base layer already carries the picture sends a curve per component that
        // maps every value to itself, and evaluating one of those costs what a real curve costs.
        bool identity = pieceCount == 1 && polynomial &&
            curve.PolynomialCoefficients[0] == 0 &&
            curve.PolynomialCoefficients[1] == 1 &&
            curve.PolynomialCoefficients[2] == 0;
        bool clips = curve.Pivots[0] > 0 || curve.Pivots[pieceCount] < 1;
        return new DolbyVisionCurveShape
        {
            PieceCount = pieceCount,
            Polynomial = polynomial && !identity,
            MmrOrder = mmrOrder,
            Identity = identity,
            Pivots = !identity || clips,
        };
    }

    private static void AppendDolbyVisionDecode(
        System.Text.StringBuilder source,
        DolbyVisionCurveShape[] shapes,
        bool swappedChroma,
        int mmrOrder,
        bool composeEnhancementLayer,
        bool enhancementSwappedChroma)
    {
        source.AppendLine("float3 decodeDoviNits(float2 coord) {")
            .Append("    float3 signal = clamp(sampleFrame(coord)")
            .Append(swappedChroma ? ".xzy" : string.Empty)
            .AppendLine(" * doviParams.x, 0.0, 1.0);");

        // The MMR basis is a function of the sample alone, so the three curves share one copy of it
        // instead of every piece of every curve building its own.
        if (mmrOrder >= 1)
        {
            source.AppendLine("    float xy = signal.x * signal.y;")
                .AppendLine("    float4 cross = float4(xy, signal.x * signal.z, signal.y * signal.z, xy * signal.z);");
        }

        if (mmrOrder >= 2)
        {
            source.AppendLine("    float3 signal2 = signal * signal;")
                .AppendLine("    float4 cross2 = cross * cross;");
        }

        if (mmrOrder >= 3)
        {
            source.AppendLine("    float3 signal3 = signal2 * signal;")
                .AppendLine("    float4 cross3 = cross2 * cross;");
        }

        source.AppendLine("    float3 reshaped = float3(");
        for (int component = 0; component < shapes.Length; component++)
        {
            source.Append("        ").Append(DolbyVisionComponentValue(component, shapes[component]))
                .AppendLine(component < shapes.Length - 1 ? "," : ");");
        }

            if (composeEnhancementLayer)
            {
                source.Append("    float3 enhancement = sampleEnhancement(coord)")
                .Append(enhancementSwappedChroma ? ".xzy" : string.Empty)
                .AppendLine(" * doviParams.y - doviNlqOffset;")
                .AppendLine("    reshaped += sign(enhancement) * (abs(enhancement) * doviNlqSlope + doviNlqThreshold);");
            }
    }

    private static string DolbyVisionComponentValue(int component, DolbyVisionCurveShape shape)
    {
        if (!shape.Identity)
            return $"reshapeDovi{component}({DolbyVisionBasisArguments(shape.MmrOrder)})";

        string channel = $"signal[{component}]";
        return shape.Pivots
            ? $"clamp({channel}, doviPivots{component}[0], doviPivots{component}[1])"
            : channel;
    }

    private static string DolbyVisionBasisParameters(int mmrOrder) => mmrOrder switch
    {
        0 => "float3 signal",
        1 => "float3 signal, float4 cross",
        2 => "float3 signal, float4 cross, float3 signal2, float4 cross2",
        _ => "float3 signal, float4 cross, float3 signal2, float4 cross2, float3 signal3, float4 cross3",
    };

    private static string DolbyVisionBasisArguments(int mmrOrder) => mmrOrder switch
    {
        0 => "signal",
        1 => "signal, cross",
        2 => "signal, cross, signal2, cross2",
        _ => "signal, cross, signal2, cross2, signal3, cross3",
    };

    private static string CreateDolbyVisionComponent(
        int component,
        DolbyVisionReshapingCurve curve,
        DolbyVisionCurveShape shape)
    {
        int pieceCount = shape.PieceCount;
        var source = new System.Text.StringBuilder($$"""
            float reshapeDovi{{component}}({{DolbyVisionBasisParameters(shape.MmrOrder)}}) {
                float s = signal[{{component}}];

            """);
        for (int piece = 0; piece < pieceCount; piece++)
        {
            if (piece < pieceCount - 1)
                source.Append("    if (s < doviPivots").Append(component).Append('[')
                    .Append(piece + 1).AppendLine("]) {");
            else
                source.AppendLine("    {");

            AppendDolbyVisionPiece(source, component, curve, piece, shape);
            source.AppendLine("    }");
        }

        return source.AppendLine("}").ToString();
    }

    private static void AppendDolbyVisionPiece(
        System.Text.StringBuilder source,
        int component,
        DolbyVisionReshapingCurve curve,
        int piece,
        DolbyVisionCurveShape shape)
    {
        if (curve.Methods[piece] < 0.5f)
        {
            source.Append($$"""
                    float3 polynomial = doviPolynomial{{component}}[{{piece}}];
                    float value = (polynomial.z * s + polynomial.y) * s + polynomial.x;

                """);
        }
        else
        {
            int coefficient = piece * shape.MmrOrder * 2;
            int order = System.Math.Clamp(
                (int)curve.MmrOrders[piece],
                1,
                DolbyVisionReshapingCurve.MaximumMmrOrder);
            source.Append($$"""
                    float value = doviMmrConstants{{component}}[{{piece}}]
                        + dot(doviMmrCoefficients{{component}}[{{coefficient}}].xyz, signal)
                        + dot(doviMmrCoefficients{{component}}[{{coefficient + 1}}], cross);

                """);
            if (order >= 2)
            {
                source.Append($$"""
                        value += dot(doviMmrCoefficients{{component}}[{{coefficient + 2}}].xyz, signal2)
                            + dot(doviMmrCoefficients{{component}}[{{coefficient + 3}}], cross2);

                    """);
            }

            if (order >= 3)
            {
                source.Append($$"""
                        value += dot(doviMmrCoefficients{{component}}[{{coefficient + 4}}].xyz, signal3)
                            + dot(doviMmrCoefficients{{component}}[{{coefficient + 5}}], cross3);

                    """);
            }
        }

        source.Append("        return clamp(value, doviPivots").Append(component)
            .Append("[0], doviPivots").Append(component).Append('[').Append(shape.PieceCount)
            .AppendLine("]);");
    }

    private static string CreateHdr10PlusToneMap()
    {
        var source = new System.Text.StringBuilder("""
            float hdr10PlusBezier(float t) {
                if (t <= 0.5) {
                    float ratio = t / max(1.0 - t, 1e-6);
                    float basis = pow(1.0 - t, 16.0);
                    float value = hdr10PlusCurve[0] * basis;

            """);
        AppendHdr10PlusBernsteinTerms(source, reverse: false);
        source.Append("""
                    return value;
                }

                float ratio = (1.0 - t) / max(t, 1e-6);
                float basis = pow(t, 16.0);
                float value = hdr10PlusCurve[16] * basis;

            """);
        AppendHdr10PlusBernsteinTerms(source, reverse: true);
        source.Append("""
                return value;
            }

            float3 toneMap(float3 nits) {
                float luminance = max(dot(sourceLuma.xyz, nits), 0.0);
                float inputMaximum = hdr10PlusParams.x;
                float outputMaximum = hdr10PlusParams.y;
                float kneeX = hdr10PlusParams.z;
                float kneeY = hdr10PlusParams.w;
                float normalized = clamp(luminance / max(inputMaximum, 1e-6), 0.0, 1.0);
                float mapped;

                if (normalized <= kneeX && kneeX > 1e-6) {
                    mapped = normalized * kneeY / kneeX;
                } else {
                    float t = clamp((normalized - kneeX) / max(1.0 - kneeX, 1e-6), 0.0, 1.0);
                    mapped = kneeY + (1.0 - kneeY) * clamp(hdr10PlusBezier(t), 0.0, 1.0);
                }

                float mappedNits = clamp(mapped, 0.0, 1.0) * outputMaximum;
                return nits * (luminance > 1e-6 ? mappedNits / luminance : 0.0);
            }
            """);
        return source.ToString();
    }

    private static void AppendHdr10PlusBernsteinTerms(System.Text.StringBuilder source, bool reverse)
    {
        for (int point = 1; point <= 16; point++)
        {
            int index = reverse ? 16 - point : point;
            source.Append("        basis *= ratio * (")
                .Append(17 - point).Append(".0 / ").Append(point).AppendLine(".0);");
            source.Append("        value += hdr10PlusCurve[").Append(index).AppendLine("] * basis;");
        }
    }

}
