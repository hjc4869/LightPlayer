using System;
using System.Numerics;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>An encoded 8-bit gain map and its ISO 21496-1 metadata.</summary>
internal sealed class PhotoExportGainImage : IDisposable
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Interleaved RGB samples.</summary>
    public required NativeBuffer Pixels { get; init; }

    public required GainMapMetadata Metadata { get; init; }

    /// <summary>Peak of the HDR rendition relative to SDR white.</summary>
    public required float Peak { get; init; }

    public void Dispose() => Pixels.Dispose();
}

/// <summary>The rendered picture as 16-bit nonlinear RGBA samples, ready for the encoders.</summary>
internal sealed class PhotoExportPicture : IDisposable
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Interleaved R, G, B, A with straight alpha, encoded with <see cref="IsPq"/>'s transfer function.</summary>
    public required NativeBuffer Samples { get; init; }

    public required bool HasAlpha { get; init; }

    public required PhotoExportPrimaries Primaries { get; init; }

    /// <summary>Whether the samples carry SMPTE ST 2084 nits rather than the sRGB curve.</summary>
    public required bool IsPq { get; init; }

    public float MaxContentLightLevel { get; init; }

    public float MaxFrameAverageLightLevel { get; init; }

    public PhotoExportGainImage? GainMap { get; init; }

    public unsafe ushort* Pointer => (ushort*)Samples.Address;

    public void Dispose()
    {
        Samples.Dispose();
        GainMap?.Dispose();
    }
}

/// <summary>A tone curve evaluated through the renderer, applied to luminance with hue preserved.</summary>
internal sealed class PhotoExportToneCurve
{
    private const int Size = 4096;
    private readonly float[] mapped = new float[Size];

    /// <param name="targetPeakNits">An HDR target's peak, or null to map into SDR.</param>
    public PhotoExportToneCurve(VideoSdrToneMappingOptions? options, float sourcePeak, double? targetPeakNits)
    {
        VideoSdrToneMappingOptions? effective = options;
        double headroom = 1;
        if (targetPeakNits is { } peak)
        {
            double white = options is null || options.Algorithm == VideoSdrToneMappingAlgorithm.Bt2446A
                ? VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits
                : options.ResolveReferenceWhiteNits(null);
            headroom = Math.Clamp(peak / white, 1, 100);
            if (options?.Algorithm == VideoSdrToneMappingAlgorithm.Bt2446A)
                effective = options with { TargetMaximumNits = Math.Clamp(peak, 1, 10000) };
            else if (options is not null)
                effective = options with { TargetHeadroom = headroom };
        }

        var volume = new VideoOutputColorVolume(VideoOutputTransferFunction.Unknown, 0, null, null, null)
        {
            ReferenceWhiteScale = 1,
            HeadroomRatio = headroom,
            ToneMapping = VideoToneMappingMode.Client,
        };
        var nits = new float[Size];
        for (int index = 0; index < Size; index++)
            nits[index] = PhotoExportColor.PqToNits(index / (float)(Size - 1));
        VideoHdrOutputMapping mapping = VideoFramePipeline.EvaluateToneCurve(VideoOutputColorSpace.ScRgbLinear, true, volume,
            effective, sourcePeak, nits, mapped);
        WhiteNits = (float)mapping.LinearNitsPerUnit;
    }

    /// <summary>The luminance that output 1.0 stands for.</summary>
    public float WhiteNits { get; }

    /// <summary>Maps BT.2020 nits to output relative to <see cref="WhiteNits"/>.</summary>
    public Vector3 Map(Vector3 nits)
    {
        float luminance = 0.2627f * nits.X + 0.6780f * nits.Y + 0.0593f * nits.Z;
        if (!(luminance > 1e-6f))
            return Vector3.Zero;
        float position = PhotoExportColor.NitsToPq(luminance) * (Size - 1);
        int index = Math.Min((int)position, Size - 2);
        float fraction = position - index;
        float value = mapped[index] + (mapped[index + 1] - mapped[index]) * fraction;
        return nits * (value / luminance);
    }
}

/// <summary>Everything the renderer needs besides the placed pictures.</summary>
/// <param name="Primaries">The output primaries; BT.2020 for PQ.</param>
internal sealed record PhotoExportRenderSettings(
    PhotoExportSourceKind Source,
    PhotoExportHdrEncoding Hdr,
    bool KeepAlpha,
    Vector3 Background,
    double DeclaredPeakNits,
    VideoSdrToneMappingOptions? ToneMapping,
    bool ToneMapHdr,
    double TargetPeakNits,
    double SdrWhiteNits,
    PhotoExportPrimaries Primaries,
    PhotoExportGamutMapper Gamut);

/// <summary>
/// Turns placed linear light into the renditions a format stores: an SDR picture, an absolute PQ
/// picture, or an SDR base with an ISO 21496-1 gain map.
/// </summary>
internal static unsafe class PhotoExportRenderer
{
    private const float GainOffset = 1f / 64;

    /// <param name="canvas">BT.2020 linear light: nits for absolute sources, relative otherwise.</param>
    /// <param name="gain">Encoded gain samples placed like <paramref name="canvas"/>, for gain-map sources.</param>
    /// <param name="gainScale">How many canvas pixels each gain-map pixel covers along each axis.</param>
    public static PhotoExportPicture Render(LinearImage canvas, LinearImage? gain, PhotoExportGainParameters? gainParameters,
        PhotoExportRenderSettings settings, int gainScale, CancellationToken cancellationToken)
    {
        bool usesGain = settings.Source == PhotoExportSourceKind.GainMap && gain is not null && gainParameters is not null;
        float measuredPeak = settings.Source == PhotoExportSourceKind.Absolute ? MeasurePeak(canvas, cancellationToken) : 0;
        bool pq = settings.Hdr == PhotoExportHdrEncoding.Pq;

        float sourcePeak = (float)VideoOutputColorSpaceExtensions.EffectiveHdrSourcePeak(
            settings.DeclaredPeakNits > 0 ? settings.DeclaredPeakNits : measuredPeak);
        bool absolute = settings.Source == PhotoExportSourceKind.Absolute;
        PhotoExportToneCurve? sdrCurve = absolute && !pq ? new PhotoExportToneCurve(settings.ToneMapping, sourcePeak, null) : null;
        PhotoExportToneCurve? hdrCurve = absolute && pq && settings.ToneMapHdr
            ? new PhotoExportToneCurve(settings.ToneMapping, sourcePeak, settings.TargetPeakNits)
            : null;

        var context = new RenderContext(canvas, gain, gainParameters, settings, usesGain, sdrCurve, hdrCurve);
        var samples = new NativeBuffer(checked(canvas.Width * canvas.Height * 8));
        try
        {
            (float maxCll, float maxFall) = pq ? RenderPq(context, samples, cancellationToken) : RenderSdr(context, samples, cancellationToken);
            PhotoExportGainImage? gainImage = settings.Hdr == PhotoExportHdrEncoding.GainMap
                ? BuildGainMap(context, samples, usesGain ? gainParameters : null, Math.Max(1, gainScale), cancellationToken)
                : null;
            return new PhotoExportPicture
            {
                Width = canvas.Width,
                Height = canvas.Height,
                Samples = samples,
                HasAlpha = settings.KeepAlpha,
                Primaries = settings.Primaries,
                IsPq = pq,
                MaxContentLightLevel = maxCll,
                MaxFrameAverageLightLevel = maxFall,
                GainMap = gainImage,
            };
        }
        catch
        {
            samples.Dispose();
            throw;
        }
    }

    private static float MeasurePeak(LinearImage canvas, CancellationToken cancellationToken)
    {
        float peak = 0;
        object gate = new();
        PhotoExportColor.ForRows(canvas.Height, canvas.Width, cancellationToken, (first, last) =>
        {
            float local = 0;
            for (int y = first; y < last; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float* row = canvas.Row(y);
                for (int x = 0; x < canvas.Width; x++)
                {
                    if (row[x * 4 + 3] > 0)
                        local = MathF.Max(local, 0.2627f * row[x * 4] + 0.6780f * row[x * 4 + 1] + 0.0593f * row[x * 4 + 2]);
                }
            }

            lock (gate)
                peak = MathF.Max(peak, local);
        });
        return peak;
    }

    private static (float MaxCll, float MaxFall) RenderSdr(RenderContext context, NativeBuffer samples, CancellationToken cancellationToken)
    {
        LinearImage canvas = context.Canvas;
        ushort* output = (ushort*)samples.Address;
        PhotoExportColor.ForRows(canvas.Height, canvas.Width, cancellationToken, (first, last) =>
        {
            for (int y = first; y < last; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float* row = canvas.Row(y);
                ushort* target = output + (long)y * canvas.Width * 4;
                for (int x = 0; x < canvas.Width; x++)
                {
                    float alpha = Math.Clamp(row[x * 4 + 3], 0, 1);
                    Vector3 color = context.Sdr(x, y, new Vector3(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]));
                    (Vector3 encoded, float opacity) = context.FinishSdr(color, alpha);
                    target[x * 4] = PhotoExportColor.Quantize16(encoded.X);
                    target[x * 4 + 1] = PhotoExportColor.Quantize16(encoded.Y);
                    target[x * 4 + 2] = PhotoExportColor.Quantize16(encoded.Z);
                    target[x * 4 + 3] = PhotoExportColor.Quantize16(opacity);
                }
            }
        });
        return (0, 0);
    }

    private static (float MaxCll, float MaxFall) RenderPq(RenderContext context, NativeBuffer samples, CancellationToken cancellationToken)
    {
        LinearImage canvas = context.Canvas;
        ushort* output = (ushort*)samples.Address;
        PhotoExportGamutMapper gamut = context.Settings.Gamut;
        float peak = 0;
        double sum = 0;
        object gate = new();
        PhotoExportColor.ForRows(canvas.Height, canvas.Width, cancellationToken, (first, last) =>
        {
            float localPeak = 0;
            double localSum = 0;
            for (int y = first; y < last; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float* row = canvas.Row(y);
                ushort* target = output + (long)y * canvas.Width * 4;
                for (int x = 0; x < canvas.Width; x++)
                {
                    float alpha = Math.Clamp(row[x * 4 + 3], 0, 1);
                    Vector3 nits = context.Nits(x, y, new Vector3(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]));
                    if (!context.Settings.KeepAlpha)
                    {
                        nits = nits * alpha + context.BackgroundNits * (1 - alpha);
                        alpha = 1;
                    }

                    float r = nits.X, g = nits.Y, b = nits.Z;
                    gamut.MapHdr(ref r, ref g, ref b);
                    float largest = MathF.Min(MathF.Max(r, MathF.Max(g, b)), 10000);
                    localPeak = MathF.Max(localPeak, largest);
                    localSum += largest;
                    target[x * 4] = PhotoExportColor.Quantize16(PhotoExportColor.NitsToPq(r));
                    target[x * 4 + 1] = PhotoExportColor.Quantize16(PhotoExportColor.NitsToPq(g));
                    target[x * 4 + 2] = PhotoExportColor.Quantize16(PhotoExportColor.NitsToPq(b));
                    target[x * 4 + 3] = PhotoExportColor.Quantize16(alpha);
                }
            }

            lock (gate)
            {
                peak = MathF.Max(peak, localPeak);
                sum += localSum;
            }
        });
        return (peak, (float)(sum / ((double)canvas.Width * canvas.Height)));
    }

    /// <summary>
    /// Builds an RGB gain map against the stored SDR samples, so that readers reconstruct the HDR
    /// rendition from exactly the base they decode. Blocks of <paramref name="scale"/> pixels are
    /// averaged in linear light before taking the ratio.
    /// </summary>
    private static PhotoExportGainImage? BuildGainMap(RenderContext context, NativeBuffer samples,
        PhotoExportGainParameters? gainParameters, int scale, CancellationToken cancellationToken)
    {
        LinearImage canvas = context.Canvas;
        int width = (canvas.Width + scale - 1) / scale, height = (canvas.Height + scale - 1) / scale;
        float[] minimum = [float.MaxValue, float.MaxValue, float.MaxValue];
        float[] maximum = [float.MinValue, float.MinValue, float.MinValue];
        float peak = 0;
        object gate = new();
        var gains = new Vector3[checked(width * height)];
        PhotoExportColor.ForRows(height, width * scale * scale, cancellationToken, (first, last) =>
        {
            Span<float> localMin = [float.MaxValue, float.MaxValue, float.MaxValue];
            Span<float> localMax = [float.MinValue, float.MinValue, float.MinValue];
            float localPeak = 0;
            for (int gy = first; gy < last; gy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int gx = 0; gx < width; gx++)
                {
                    Vector3 gain = BlockGain(context, samples, gx, gy, scale, out float blockPeak);
                    gains[gy * width + gx] = gain;
                    localPeak = MathF.Max(localPeak, blockPeak);
                    for (int c = 0; c < 3; c++)
                    {
                        localMin[c] = MathF.Min(localMin[c], gain[c]);
                        localMax[c] = MathF.Max(localMax[c], gain[c]);
                    }
                }
            }

            lock (gate)
            {
                peak = MathF.Max(peak, localPeak);
                for (int c = 0; c < 3; c++)
                {
                    minimum[c] = MathF.Min(minimum[c], localMin[c]);
                    maximum[c] = MathF.Max(maximum[c], localMax[c]);
                }
            }
        });

        float headroom = MathF.Log2(MathF.Max(peak, 1));
        if (headroom <= 1f / 64)
            return null;
        for (int c = 0; c < 3; c++)
        {
            minimum[c] = MathF.Min(minimum[c], 0);
            maximum[c] = MathF.Max(maximum[c], minimum[c] + 1f / 256);
        }

        var metadata = new GainMapMetadata(minimum, maximum, [1, 1, 1],
            [GainOffset, GainOffset, GainOffset], [GainOffset, GainOffset, GainOffset],
            gainParameters?.CapacityMin ?? 0, gainParameters?.CapacityMax ?? headroom);
        var pixels = new NativeBuffer(checked(width * height * 3));
        try
        {
            byte* output = (byte*)pixels.Address;
            PhotoExportColor.ForRows(height, width, cancellationToken, (first, last) =>
            {
                for (int index = first * width; index < last * width; index++)
                {
                    Vector3 gain = gains[index];
                    byte* target = output + (long)index * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float recovery = Math.Clamp((gain[c] - minimum[c]) / (maximum[c] - minimum[c]), 0, 1);
                        target[c] = (byte)(recovery * 255 + 0.5f);
                    }
                }
            });
            return new PhotoExportGainImage { Width = width, Height = height, Pixels = pixels, Metadata = metadata, Peak = peak };
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static Vector3 BlockGain(RenderContext context, NativeBuffer samples, int gx, int gy, int scale, out float peak)
    {
        LinearImage canvas = context.Canvas;
        ushort* stored = (ushort*)samples.Address;
        Vector3 hdr = Vector3.Zero, sdr = Vector3.Zero;
        peak = 0;
        int count = 0;
        int bottom = Math.Min(canvas.Height, (gy + 1) * scale), right = Math.Min(canvas.Width, (gx + 1) * scale);
        for (int y = gy * scale; y < bottom; y++)
        {
            float* row = canvas.Row(y);
            for (int x = gx * scale; x < right; x++)
            {
                ushort* encoded = stored + ((long)y * canvas.Width + x) * 4;
                var standard = new Vector3(
                    PhotoExportColor.SrgbToLinear(encoded[0] / 65535f),
                    PhotoExportColor.SrgbToLinear(encoded[1] / 65535f),
                    PhotoExportColor.SrgbToLinear(encoded[2] / 65535f));
                Vector3 high = context.Hdr(x, y, new Vector3(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]),
                    Math.Clamp(row[x * 4 + 3], 0, 1));
                peak = MathF.Max(peak, MathF.Max(high.X, MathF.Max(high.Y, high.Z)));
                hdr += high;
                sdr += standard;
                count++;
            }
        }

        hdr /= count;
        sdr /= count;
        return new Vector3(
            MathF.Log2((hdr.X + GainOffset) / (sdr.X + GainOffset)),
            MathF.Log2((hdr.Y + GainOffset) / (sdr.Y + GainOffset)),
            MathF.Log2((hdr.Z + GainOffset) / (sdr.Z + GainOffset)));
    }

    /// <summary>Bilinearly samples the placed gain map at a canvas pixel.</summary>
    private static Vector3 ApplyGain(LinearImage gain, PhotoExportGainParameters parameters, LinearImage canvas, int x, int y,
        Vector3 color, float weight)
    {
        float u = Math.Clamp((x + 0.5f) * gain.Width / canvas.Width - 0.5f, 0, gain.Width - 1);
        float v = Math.Clamp((y + 0.5f) * gain.Height / canvas.Height - 0.5f, 0, gain.Height - 1);
        int left = (int)u, top = (int)v;
        int right = Math.Min(left + 1, gain.Width - 1), bottom = Math.Min(top + 1, gain.Height - 1);
        float tx = u - left, ty = v - top;
        float* upper = gain.Row(top), lower = gain.Row(bottom);
        Vector4 a = *(Vector4*)(upper + left * 4), b = *(Vector4*)(upper + right * 4);
        Vector4 c = *(Vector4*)(lower + left * 4), d = *(Vector4*)(lower + right * 4);
        Vector4 sample = Vector4.Lerp(Vector4.Lerp(a, b, tx), Vector4.Lerp(c, d, tx), ty);
        return parameters.Apply(color, new Vector3(sample.X, sample.Y, sample.Z), Math.Clamp(sample.W, 0, 1), weight);
    }

    /// <summary>The per-pixel conversions of one render, computed once from its settings.</summary>
    private sealed class RenderContext
    {
        private readonly LinearImage? gain;
        private readonly PhotoExportGainParameters? parameters;
        private readonly bool usesGain;
        private readonly PhotoExportToneCurve? sdrCurve;
        private readonly PhotoExportToneCurve? hdrCurve;
        private readonly float[] toOutput;
        private readonly Vector3 background;

        public RenderContext(LinearImage canvas, LinearImage? gain, PhotoExportGainParameters? parameters,
            PhotoExportRenderSettings settings, bool usesGain, PhotoExportToneCurve? sdrCurve, PhotoExportToneCurve? hdrCurve)
        {
            Canvas = canvas;
            Settings = settings;
            this.gain = gain;
            this.parameters = parameters;
            this.usesGain = usesGain;
            this.sdrCurve = sdrCurve;
            this.hdrCurve = hdrCurve;
            toOutput = PhotoExportColor.Matrix(PhotoExportPrimaries.Bt2020, settings.Primaries);
            Vector3 linear = settings.Background;
            background = PhotoExportColor.Transform(PhotoExportColor.Matrix(PhotoExportPrimaries.Bt709, settings.Primaries), linear);
            BackgroundNits = PhotoExportColor.Transform(PhotoExportColor.Matrix(PhotoExportPrimaries.Bt709, PhotoExportPrimaries.Bt2020),
                linear) * (float)(settings.Source == PhotoExportSourceKind.Absolute
                    ? hdrCurve?.WhiteNits ?? PhotoExportColor.ReferenceWhiteNits
                    : settings.SdrWhiteNits);
        }

        public LinearImage Canvas { get; }

        public PhotoExportRenderSettings Settings { get; }

        public Vector3 BackgroundNits { get; }

        /// <summary>The SDR rendition, relative and on BT.2020 primaries.</summary>
        public Vector3 Sdr(int x, int y, Vector3 color) => Settings.Source switch
        {
            PhotoExportSourceKind.Absolute => sdrCurve!.Map(color),
            PhotoExportSourceKind.GainMap when usesGain && parameters!.BaseIsHdr => ApplyGain(gain!, parameters, Canvas, x, y, color, 0),
            _ => color,
        };

        /// <summary>The HDR rendition relative to the SDR rendition's white, fitted to the output gamut.</summary>
        public Vector3 Hdr(int x, int y, Vector3 color, float alpha)
        {
            Vector3 high = Settings.Source == PhotoExportSourceKind.Absolute
                ? color / sdrCurve!.WhiteNits
                : usesGain ? ApplyGain(gain!, parameters!, Canvas, x, y, color, 1) : color;
            Vector3 output = PhotoExportColor.Transform(toOutput, high);
            output = output * alpha + background * (1 - alpha);
            float r = output.X, g = output.Y, b = output.Z;
            Settings.Gamut.MapHdr(ref r, ref g, ref b);
            return new Vector3(r, g, b);
        }

        /// <summary>The PQ rendition in BT.2020 nits.</summary>
        public Vector3 Nits(int x, int y, Vector3 color)
        {
            if (Settings.Source == PhotoExportSourceKind.Absolute)
                return hdrCurve is null ? color : hdrCurve.Map(color) * hdrCurve.WhiteNits;
            Vector3 relative = usesGain ? ApplyGain(gain!, parameters!, Canvas, x, y, color, 1) : color;
            return relative * (float)Settings.SdrWhiteNits;
        }

        /// <summary>Converts an SDR color to the output primaries, flattens or keeps alpha, fits the gamut, and encodes it.</summary>
        public (Vector3 Encoded, float Alpha) FinishSdr(Vector3 color, float alpha)
        {
            Vector3 output = PhotoExportColor.Transform(toOutput, color);
            if (!Settings.KeepAlpha)
            {
                output = output * alpha + background * (1 - alpha);
                alpha = 1;
            }

            float r = output.X, g = output.Y, b = output.Z;
            Settings.Gamut.MapSdr(ref r, ref g, ref b);
            return (new Vector3(PhotoExportColor.LinearToSrgb(r), PhotoExportColor.LinearToSrgb(g),
                PhotoExportColor.LinearToSrgb(b)), alpha);
        }
    }
}
