#nullable enable
using System;
using System.Numerics;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;

namespace LightMediaRenderer.Software;

// Keep the equations in sync with Shaders/VideoColorStages.cs (transfer, Dolby Vision, tone, gamut, encoding).
internal sealed class VideoCpuColorPipeline
{
    internal const float ReferenceWhiteNits = 200;
    private readonly VideoFrame frame;
    private readonly VideoToneCurve tone;
    private readonly float[] gamut;
    private readonly float[] matrix = new float[12];
    private readonly float[] doviLinear = new float[9];
    private readonly float[] luma;
    private readonly bool hdr;
    private readonly bool sceneLight;
    private readonly bool managed;
    private readonly bool convertGamut;
    private readonly float sourcePeak;
    private readonly float sourceExponent;

    /// <param name="sceneLight">
    /// Decodes to untouched linear light: absolute sources in nits and relative ones with SDR white at 1.
    /// </param>
    public VideoCpuColorPipeline(VideoFrame frame, VideoSdrToneMappingOptions? options = null, bool sceneLight = false)
    {
        this.frame = frame;
        this.sceneLight = sceneLight;
        bool dovi = frame.DolbyVision.IsApplicable;
        hdr = dovi || frame.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        VideoColorPrimaries primaries = dovi ? VideoColorPrimaries.Bt2020 : frame.ColorPrimaries;
        (gamut, luma) = VideoColorGamut.ToBt709Single(primaries);
        convertGamut = VideoColorGamut.NeedsConversion(primaries);
        managed = hdr || frame.PhotoColor is not null || frame.Hdr10Plus.IsPresent ||
            frame.ColorTransfer != VideoColorTransfer.Bt709 || convertGamut;
        VideoSurfaceTransfer? sourceTransfer = VideoSurfaceTransfers.ForBt709();
        sourceExponent = frame.ColorTransfer == VideoColorTransfer.Bt709 && sourceTransfer is
            { Kind: VideoSurfaceTransferKind.Power } power ? power.Exponent : 0;
        managed |= sourceTransfer is not null;
        float peak = dovi && frame.DolbyVision.EffectivePeakLuminance > 0 ? (float)frame.DolbyVision.EffectivePeakLuminance :
            !dovi && frame.Hdr10Plus is { IsPresent: true, EffectivePeakLuminance: > 0 }
                ? frame.Hdr10Plus.EffectivePeakLuminance : (float)frame.HdrPeakLuminance;
        sourcePeak = frame.ColorTransfer == VideoColorTransfer.Hlg && !dovi ? 1000 :
            float.IsFinite(peak) && peak > 0 ? Math.Clamp(peak, 100, 10000) : 1000;
        options ??= new VideoSdrToneMappingOptions
        {
            Algorithm = dovi || frame.Hdr10Plus.IsPresent
                ? VideoSdrToneMappingAlgorithm.Auto : VideoSdrToneMappingAlgorithm.Bt2390,
        };
        options.Validate();
        if (options.Algorithm is not (VideoSdrToneMappingAlgorithm.Auto or VideoSdrToneMappingAlgorithm.Spline or
            VideoSdrToneMappingAlgorithm.Bt2390 or VideoSdrToneMappingAlgorithm.Bt2446A))
            throw new ArgumentException($"Software previews do not implement the {options.Algorithm} tone curve.",
                nameof(options));
        if (options.ReferenceWhiteNits is not (null or ReferenceWhiteNits) ||
            options.TargetHeadroom is not (null or 1) || options.TargetMaximumNits is not (null or 200))
            throw new ArgumentException("Software previews require a 200-nit white and 1x headroom.", nameof(options));
        tone = sceneLight || !hdr || sourcePeak <= ReferenceWhiteNits ? new VideoToneCurve() :
            !dovi && frame.Hdr10Plus.IsPresent && options.Algorithm == VideoSdrToneMappingAlgorithm.Auto &&
                frame.Hdr10Plus.Windows[0] is { ToneMapping: true, AnchorCount: > 0 } &&
                frame.Hdr10Plus.TargetedSystemDisplayMaximumLuminance > 0
                ? VideoToneCurve.Hdr10Plus(frame.Hdr10Plus, sourcePeak, ReferenceWhiteNits)
                : options.Algorithm switch
                {
                    VideoSdrToneMappingAlgorithm.Bt2390 =>
                        VideoToneCurve.Bt2390(sourcePeak, ReferenceWhiteNits, options.Bt2390KneeOffset),
                    VideoSdrToneMappingAlgorithm.Bt2446A => VideoToneCurve.Bt2446A(sourcePeak, ReferenceWhiteNits),
                    _ => VideoToneCurve.Spline(
                        dovi ? VideoToneCurve.NitsToPq(VideoToneCurve.PqToNits(frame.DolbyVision.SourceMinimumPq)) : 0,
                        dovi ? VideoToneCurve.NitsToPq(VideoToneCurve.PqToNits(frame.DolbyVision.SceneAveragePq)) :
                            VideoToneCurve.NitsToPq(frame.Hdr10Plus.SceneAverageLuminance),
                        VideoToneCurve.NitsToPq(sourcePeak), VideoToneCurve.NitsToPq(ReferenceWhiteNits), options),
                };
        if (dovi)
        {
            DolbyVisionMetadata metadata = frame.DolbyVision;
            for (int row = 0; row < 3; row++)
            {
                Array.Copy(metadata.NonlinearMatrix, row * 3, matrix, row * 4, 3);
                matrix[row * 4 + 3] = -(metadata.NonlinearMatrix[row * 3] * metadata.NonlinearOffset[0] +
                    metadata.NonlinearMatrix[row * 3 + 1] * metadata.NonlinearOffset[1] +
                    metadata.NonlinearMatrix[row * 3 + 2] * metadata.NonlinearOffset[2]);
            }
            ReadOnlySpan<float> lmsToRgb =
            [
                3.06441879f, -2.16597676f, 0.10155818f,
                -0.65612108f, 1.78554118f, -0.12943749f,
                0.01736321f, -0.04725154f, 1.03004253f,
            ];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    doviLinear[row * 3 + column] =
                        lmsToRgb[row * 3] * frame.DolbyVision.LinearMatrix[column] +
                        lmsToRgb[row * 3 + 1] * frame.DolbyVision.LinearMatrix[3 + column] +
                        lmsToRgb[row * 3 + 2] * frame.DolbyVision.LinearMatrix[6 + column];
        }
        else if (VideoPlanarLayout.IsPlanar(frame.Format))
        {
            (float kr, float kb) = frame.ColorSpace switch
            {
                VideoColorSpace.Bt601 => (0.299f, 0.114f),
                VideoColorSpace.Bt2020 => (0.2627f, 0.0593f),
                _ => (0.2126f, 0.0722f),
            };
            float kg = 1 - kr - kb;
            float maximum = (1 << frame.Layout.BitDepth) - 1;
            float codeScale = 1 << (frame.Layout.BitDepth - 8);
            bool full = frame.ColorRange == VideoColorRange.Full;
            float yGain = full ? 1 : maximum / (219 * codeScale);
            float cGain = full ? 1 : maximum / (224 * codeScale);
            float yOffset = full ? 0 : 16 * codeScale / maximum;
            float cOffset = 128 * codeScale / maximum;
            SetRow(0, yGain, 0, 2 * (1 - kr) * cGain, yOffset, cOffset);
            SetRow(1, yGain, -2 * kb * (1 - kb) / kg * cGain,
                -2 * kr * (1 - kr) / kg * cGain, yOffset, cOffset);
            SetRow(2, yGain, 2 * (1 - kb) * cGain, 0, yOffset, cOffset);
        }
    }

    public bool Dither => hdr || frame.Layout.BitDepth > 8 ||
        frame.Format is VideoPixelFormat.Rgba64 or VideoPixelFormat.RgbaF16;

    public void Decode(Span<float> r, Span<float> g, Span<float> b,
        ReadOnlySpan<float> er = default, ReadOnlySpan<float> eg = default, ReadOnlySpan<float> eb = default,
        bool useSimd = true)
    {
        ValidateChannels(r, g, b);
        bool enhancement = frame.DolbyVision is { IsApplicable: true, EnhancementLayerResidual: true } &&
            frame.EnhancementLayer is not null;
        if (enhancement && (er.Length < r.Length || eg.Length < r.Length || eb.Length < r.Length))
            throw new ArgumentException("Enhancement samples must cover the base-layer row.");
        int x = 0;
        if (useSimd && Vector.IsHardwareAccelerated)
        {
            for (; x <= r.Length - Vector<float>.Count; x += Vector<float>.Count)
            {
                var red = new SimdColor(new Vector<float>(r[x..]));
                var green = new SimdColor(new Vector<float>(g[x..]));
                var blue = new SimdColor(new Vector<float>(b[x..]));
                DecodePixel(ref red, ref green, ref blue,
                    enhancement ? new SimdColor(new Vector<float>(er[x..])) : default,
                    enhancement ? new SimdColor(new Vector<float>(eg[x..])) : default,
                    enhancement ? new SimdColor(new Vector<float>(eb[x..])) : default, enhancement);
                red.Value.CopyTo(r[x..]);
                green.Value.CopyTo(g[x..]);
                blue.Value.CopyTo(b[x..]);
            }
        }
        for (; x < r.Length; x++)
        {
            ScalarColor red = r[x], green = g[x], blue = b[x];
            DecodePixel(ref red, ref green, ref blue, enhancement ? er[x] : 0,
                enhancement ? eg[x] : 0, enhancement ? eb[x] : 0, enhancement);
            r[x] = red.Value;
            g[x] = green.Value;
            b[x] = blue.Value;
        }
    }

    public void Encode(Span<float> r, Span<float> g, Span<float> b, bool useSimd = true)
    {
        ValidateChannels(r, g, b);
        int x = 0;
        if (useSimd && Vector.IsHardwareAccelerated)
        {
            for (; x <= r.Length - Vector<float>.Count; x += Vector<float>.Count)
            {
                var red = new SimdColor(new Vector<float>(r[x..]));
                var green = new SimdColor(new Vector<float>(g[x..]));
                var blue = new SimdColor(new Vector<float>(b[x..]));
                EncodePixel(ref red, ref green, ref blue);
                red.Value.CopyTo(r[x..]);
                green.Value.CopyTo(g[x..]);
                blue.Value.CopyTo(b[x..]);
            }
        }
        for (; x < r.Length; x++)
        {
            ScalarColor red = r[x], green = g[x], blue = b[x];
            EncodePixel(ref red, ref green, ref blue);
            r[x] = red.Value;
            g[x] = green.Value;
            b[x] = blue.Value;
        }
    }

    /// <summary>
    /// Converts samples to the nonlinear RGB signal the source was coded in, keeping its transfer and
    /// primaries. Dolby Vision has no such signal and is reconstructed through <see cref="Decode"/>.
    /// </summary>
    public void DecodeSignal(Span<float> r, Span<float> g, Span<float> b)
    {
        ValidateChannels(r, g, b);
        if (frame.DolbyVision.IsApplicable)
            throw new InvalidOperationException("Dolby Vision pictures are only defined once reshaped into light.");
        bool planar = VideoPlanarLayout.IsPlanar(frame.Format);
        for (int x = 0; x < r.Length; x++)
        {
            float y = r[x], cb = g[x], cr = b[x];
            if (planar)
            {
                r[x] = matrix[0] * y + matrix[1] * cb + matrix[2] * cr + matrix[3];
                g[x] = matrix[4] * y + matrix[5] * cb + matrix[6] * cr + matrix[7];
                b[x] = matrix[8] * y + matrix[9] * cb + matrix[10] * cr + matrix[11];
            }
            r[x] = Math.Clamp(r[x], 0, 1);
            g[x] = Math.Clamp(g[x], 0, 1);
            b[x] = Math.Clamp(b[x], 0, 1);
        }
    }

    private void DecodePixel<T>(ref T r, ref T g, ref T b, T er, T eg, T eb, bool enhancement)
        where T : struct, IColorMath<T>
    {
        if (frame.DolbyVision.IsApplicable)
        {
            DolbyVisionMetadata metadata = frame.DolbyVision;
            T x = Clamp(r, 0, 1), y = Clamp(g, 0, 1), z = Clamp(b, 0, 1);
            r = Reshape(metadata.Curves[0], x, x, y, z);
            g = Reshape(metadata.Curves[1], y, x, y, z);
            b = Reshape(metadata.Curves[2], z, x, y, z);
            if (enhancement)
            {
                r = r + Residual(er - (T)metadata.NlqOffset[0], metadata, 0);
                g = g + Residual(eg - (T)metadata.NlqOffset[1], metadata, 1);
                b = b + Residual(eb - (T)metadata.NlqOffset[2], metadata, 2);
            }
            x = r; y = g; z = b;
            r = PqToNits(Dot(matrix, 0, x, y, z) + (T)matrix[3]);
            g = PqToNits(Dot(matrix, 4, x, y, z) + (T)matrix[7]);
            b = PqToNits(Dot(matrix, 8, x, y, z) + (T)matrix[11]);
            x = r; y = g; z = b;
            r = Clamp(Dot(doviLinear, 0, x, y, z), 0, 10000);
            g = Clamp(Dot(doviLinear, 3, x, y, z), 0, 10000);
            b = Clamp(Dot(doviLinear, 6, x, y, z), 0, 10000);
        }
        else
        {
            if (VideoPlanarLayout.IsPlanar(frame.Format))
            {
                T x = r, y = g, z = b;
                r = Dot(matrix, 0, x, y, z) + (T)matrix[3];
                g = Dot(matrix, 4, x, y, z) + (T)matrix[7];
                b = Dot(matrix, 8, x, y, z) + (T)matrix[11];
            }
            r = Clamp(r, 0, 1); g = Clamp(g, 0, 1); b = Clamp(b, 0, 1);
            if (frame.PhotoColor is { } profile)
            {
                r = PhotoToLinear(r, profile); g = PhotoToLinear(g, profile); b = PhotoToLinear(b, profile);
                T x = r, y = g, z = b;
                ReadOnlySpan<float> m = profile.ToBt2020.Span;
                r = (T)m[0] * x + (T)m[1] * y + (T)m[2] * z;
                g = (T)m[3] * x + (T)m[4] * y + (T)m[5] * z;
                b = (T)m[6] * x + (T)m[7] * y + (T)m[8] * z;
                if (profile.TransferCode == 18)
                {
                    T luminance = (T)0.2627f * r + (T)0.6780f * g + (T)0.0593f * b;
                    T scale = T.Pow(T.Max(luminance, 1e-6f), 0.2f) * (T)(1000 * profile.LinearScale);
                    r = r * scale; g = g * scale; b = b * scale;
                }
            }
            else if (frame.ColorTransfer == VideoColorTransfer.Pq)
            {
                r = PqToNits(r); g = PqToNits(g); b = PqToNits(b);
            }
            else if (frame.ColorTransfer == VideoColorTransfer.Hlg)
            {
                r = HlgToScene(r); g = HlgToScene(g); b = HlgToScene(b);
                T luminance = T.Max((T)luma[0] * r + (T)luma[1] * g + (T)luma[2] * b, 1e-6f);
                T scale = T.Pow(luminance, 0.2f) * (T)sourcePeak;
                r = r * scale; g = g * scale; b = b * scale;
            }
            else if (frame.ColorTransfer != VideoColorTransfer.Linear)
            {
                if (sourceExponent > 0)
                {
                    r = T.Pow(r, sourceExponent); g = T.Pow(g, sourceExponent); b = T.Pow(b, sourceExponent);
                }
                else
                {
                    r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b);
                }
            }
        }
        if (tone.Kind != SoftwareToneCurve.None)
        {
            T luminance = T.Max((T)luma[0] * r + (T)luma[1] * g + (T)luma[2] * b, 0);
            T mapped = MapLuminance(tone, luminance);
            T scale = T.SelectGreater(luminance, 1e-6f, mapped / T.Max(luminance, 1e-6f), 0);
            r = r * scale; g = g * scale; b = b * scale;
        }
        if (hdr && !sceneLight)
        {
            r = r * (T)(1 / ReferenceWhiteNits);
            g = g * (T)(1 / ReferenceWhiteNits);
            b = b * (T)(1 / ReferenceWhiteNits);
        }
    }

    internal static T MapLuminance<T>(VideoToneCurve tone, T luminance) where T : struct, IColorMath<T>
    {
        float[] p = tone.Parameters;
        if (tone.Kind == SoftwareToneCurve.Hdr10Plus)
        {
            T normalized = Clamp(luminance / (T)MathF.Max(p[0], 1e-6f), 0, 1);
            T t = Clamp((normalized - (T)p[2]) / (T)MathF.Max(1 - p[2], 1e-6f), 0, 1);
            T forward = Bezier(tone, t, false), reverse = Bezier(tone, t, true);
            T value = T.SelectGreater(t, 0.5f, reverse, forward);
            T mapped = (T)p[3] + (T)(1 - p[3]) * Clamp(value, 0, 1);
            if (p[2] > 1e-6f)
                mapped = T.SelectGreater(normalized, p[2], mapped, normalized * (T)(p[3] / p[2]));
            return Clamp(mapped, 0, 1) * (T)p[1];
        }
        if (tone.Kind == SoftwareToneCurve.Bt2446A)
        {
            T level = T.Pow(Clamp(luminance / (T)p[0], 0, 1), 1 / 2.4f);
            level = T.Log((T)1 + (T)(p[2] - 1) * level) / (T)MathF.Log(p[2]);
            level = T.SelectGreater(level, 0.7399f,
                T.SelectGreater(0.9909f, level, ((T)(-1.1510f) * level + (T)2.7811f) * level - (T)0.6302f,
                    (T)0.5f * level + (T)0.5f), level * (T)1.0770f);
            level = (T.Exp(level * (T)MathF.Log(p[3])) - (T)1) / (T)(p[3] - 1);
            return T.Pow(Clamp(level, 0, 1), 2.4f) * (T)p[1];
        }
        if (tone.Kind == SoftwareToneCurve.Spline)
        {
            float[] a = tone.Curve0, c = tone.Curve1;
            T x = Clamp(NitsToPq(luminance), p[0], p[1]) - (T)a[0];
            T mapped = T.SelectGreater(x, 0,
                (((T)c[0] * x + (T)c[1]) * x + (T)c[2]) * x,
                ((T)a[2] * x + (T)a[3]) * x);
            return PqToNits(Clamp(mapped + (T)a[1], p[2], p[3]));
        }
        T encoded = Clamp(NitsToPq(luminance) / (T)p[0], 0, 1);
        float knee = (1 + p[3]) * p[2] - p[3];
        if (knee < 1)
        {
            T t = (encoded - (T)knee) / (T)(1 - knee);
            T t2 = t * t, t3 = t2 * t;
            T shoulder = ((T)2 * t3 - (T)3 * t2 + (T)1) * (T)knee +
                (t3 - (T)2 * t2 + t) * (T)(1 - knee) + ((T)(-2) * t3 + (T)3 * t2) * (T)p[2];
            encoded = T.SelectGreater(encoded, knee, shoulder, encoded);
        }
        return PqToNits(T.Min(encoded * (T)p[0], p[1]));
    }

    private static T Bezier<T>(VideoToneCurve tone, T t, bool reverse) where T : struct, IColorMath<T>
    {
        T ratio = reverse ? ((T)1 - t) / T.Max(t, 1e-6f) : t / T.Max((T)1 - t, 1e-6f);
        T basis = T.Pow(reverse ? t : (T)1 - t, 16);
        T value = (T)tone.Controls[reverse ? 16 : 0] * basis;
        for (int point = 1; point <= 16; point++)
        {
            basis = basis * ratio * (T)((float)(17 - point) / point);
            value = value + (T)tone.Controls[reverse ? 16 - point : point] * basis;
        }
        return value;
    }

    private void EncodePixel<T>(ref T r, ref T g, ref T b) where T : struct, IColorMath<T>
    {
        if (managed)
        {
            if (convertGamut)
            {
                T x = r, y = g, z = b;
                r = Dot(gamut, 0, x, y, z); g = Dot(gamut, 3, x, y, z); b = Dot(gamut, 6, x, y, z);
            }
            r = T.Max(r, 0); g = T.Max(g, 0); b = T.Max(b, 0);
            T maximum = T.Max(r, T.Max(g, b));
            T strength = (maximum - (T)1) / ((T)0.3f * T.Max(maximum - (T)0.7f, 1e-6f));
            strength = T.SelectGreater(maximum, 1, strength, 0);
            r = Compress(r, strength); g = Compress(g, strength); b = Compress(b, strength);
        }
        r = Clamp(LinearToSrgb(r), 0, 1);
        g = Clamp(LinearToSrgb(g), 0, 1);
        b = Clamp(LinearToSrgb(b), 0, 1);
    }

    private static T Compress<T>(T value, T strength) where T : struct, IColorMath<T>
    {
        T excess = T.Max(value - (T)0.7f, 0);
        return T.Min(value, 0.7f) + excess / ((T)1 + strength * excess);
    }

    private static T Reshape<T>(DolbyVisionReshapingCurve curve, T s, T x, T y, T z)
        where T : struct, IColorMath<T>
    {
        int count = Math.Clamp(curve.PivotCount - 1, 1, 8);
        T result = Piece(count - 1);
        for (int piece = count - 2; piece >= 0; piece--)
            result = T.SelectGreater(curve.Pivots[piece + 1], s, Piece(piece), result);
        return Clamp(result, curve.Pivots[0], curve.Pivots[count]);

        T Piece(int piece)
        {
            if (curve.Methods[piece] < 0.5f)
            {
                int offset = piece * 3;
                return ((T)curve.PolynomialCoefficients[offset + 2] * s +
                    (T)curve.PolynomialCoefficients[offset + 1]) * s + (T)curve.PolynomialCoefficients[offset];
            }
            T value = (T)curve.MmrConstants[piece];
            T xy = x * y, xz = x * z, yz = y * z, xyz = xy * z;
            T px = x, py = y, pz = z, pxy = xy, pxz = xz, pyz = yz, pxyz = xyz;
            int order = Math.Clamp((int)curve.MmrOrders[piece], 1, 3);
            for (int i = 0; i < order; i++)
            {
                int offset = (piece * 3 + i) * 7;
                float[] c = curve.MmrCoefficients;
                value = value + ((T)c[offset] * px + (T)c[offset + 1] * py + (T)c[offset + 2] * pz) +
                    ((T)c[offset + 3] * pxy + (T)c[offset + 4] * pxz + (T)c[offset + 5] * pyz + (T)c[offset + 6] * pxyz);
                px = px * x; py = py * y; pz = pz * z;
                pxy = pxy * xy; pxz = pxz * xz; pyz = pyz * yz; pxyz = pxyz * xyz;
            }
            return value;
        }
    }

    private static T Residual<T>(T value, DolbyVisionMetadata metadata, int component)
        where T : struct, IColorMath<T> =>
        T.SelectGreater(value, 0, value * (T)metadata.NlqDeadzoneSlope[component] + (T)metadata.NlqDeadzoneThreshold[component],
            T.SelectGreater(0, value, value * (T)metadata.NlqDeadzoneSlope[component] - (T)metadata.NlqDeadzoneThreshold[component], 0));

    private static T PqToNits<T>(T signal) where T : struct, IColorMath<T>
    {
        T power = T.Pow(T.Max(signal, 0), 1 / 78.84375f);
        T level = T.Max(power - (T)0.8359375f, 0) / T.Max((T)18.8515625f - (T)18.6875f * power, 1e-6f);
        return (T)10000 * T.Pow(level, 1 / 0.1593017578125f);
    }

    private static T NitsToPq<T>(T nits) where T : struct, IColorMath<T>
    {
        T level = T.Pow(Clamp(nits / (T)10000, 0, 1), 0.1593017578125f);
        return T.SelectGreater(nits, 0,
            T.Pow(((T)0.8359375f + (T)18.8515625f * level) / ((T)1 + (T)18.6875f * level), 78.84375f), 0);
    }

    private static T HlgToScene<T>(T signal) where T : struct, IColorMath<T> =>
        T.SelectGreater(signal, 0.5f, (T.Exp((signal - (T)0.55991073f) / (T)0.17883277f) + (T)0.28466892f) / (T)12,
            signal * signal / (T)3);

    private static T PhotoToLinear<T>(T signal, PhotoColorProfile profile) where T : struct, IColorMath<T>
    {
        if (profile.TransferCode == 16)
            return PqToNits(signal) * (T)profile.LinearScale;
        if (profile.TransferCode == 18)
            return HlgToScene(signal);
        ReadOnlySpan<float> c = profile.Transfer.Span;
        T magnitude = T.Max(signal, (T)0 - signal);
        T linear = T.SelectGreater(c[4], magnitude, (T)c[3] * magnitude + (T)c[6],
            T.Pow(T.Max((T)c[1] * magnitude + (T)c[2], 0), c[0]) + (T)c[5]);
        return T.SelectGreater(0, signal, (T)0 - linear, linear) * (T)profile.LinearScale;
    }

    private static T SrgbToLinear<T>(T signal) where T : struct, IColorMath<T> =>
        T.SelectGreater(signal, 0.04045f, T.Pow((signal + (T)0.055f) / (T)1.055f, 2.4f), signal / (T)12.92f);

    private static T LinearToSrgb<T>(T signal) where T : struct, IColorMath<T> =>
        T.SelectGreater(signal, 0.0031308f, (T)1.055f * T.Pow(T.Max(signal, 0), 1 / 2.4f) - (T)0.055f, (T)12.92f * signal);

    private static T Clamp<T>(T value, float minimum, float maximum) where T : struct, IColorMath<T> =>
        T.Min(T.Max(value, minimum), maximum);

    private static T Dot<T>(float[] values, int offset, T x, T y, T z) where T : struct, IColorMath<T> =>
        (T)values[offset] * x + (T)values[offset + 1] * y + (T)values[offset + 2] * z;

    private void SetRow(int row, float y, float cb, float cr, float yOffset, float cOffset)
    {
        int offset = row * 4;
        matrix[offset] = y; matrix[offset + 1] = cb; matrix[offset + 2] = cr;
        matrix[offset + 3] = -(y * yOffset + (cb + cr) * cOffset);
    }

    private static void ValidateChannels(Span<float> r, Span<float> g, Span<float> b)
    {
        if (r.Length != g.Length || r.Length != b.Length)
            throw new ArgumentException("Color channels must have equal lengths.");
    }
}
