using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Establishes which plane layouts a graphics backend actually reproduces.
/// </summary>
/// <remarks>
/// <para>
/// Backends differ in which texture formats they hold -- 16-bit single-channel ones are absent from
/// some GL ES and WebGL configurations -- and Skia answers an unsupported one by quietly converting
/// the pixels rather than by failing, so the only dependable question is whether a known frame
/// survives. The answer is remembered, and a format that fails is also withdrawn from the decoders,
/// which convert on the CPU from then on.
/// </para>
/// <para>
/// Withdrawing a layout is permanent and process-wide, so it needs evidence about the layout. A
/// scratch target that could not be allocated, or a readback the backend refused, says nothing
/// about the planes and must not be allowed to answer: treating those as a failure once cost the
/// browser head every 10-bit stream for the rest of the session, and did it mid-session, after
/// earlier frames had already been displayed from the same planes.
/// </para>
/// </remarks>
public static class VideoPlaneFormatProbe
{
    private const int Size = 8;

    /// <summary>Unanswered probes before the layout is taken on trust rather than asked again.</summary>
    private const int MaxUnanswered = 8;

    private static readonly object sync = new();
    private static readonly Dictionary<VideoPixelFormat, bool> probed = [];
    private static GRContext? probedContext;
    private static int unanswered;

    // CanvasKit can accept a 16-bit plane description without reproducing its samples faithfully,
    // and Android GL drivers can advertise the format while Skia silently narrows a raster upload.
    // Both platforms' software frames already live in CPU memory, so start with the dependable
    // two-byte texture representation. Imported hardware planes override this choice themselves.
    private static volatile bool wideAsBytePairs =
        OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid();

    private enum ProbeOutcome
    {
        /// <summary>The frame came back as it went in.</summary>
        Displayable,

        /// <summary>The frame came back wrong, or Skia would not describe the planes at all.</summary>
        NotDisplayable,

        /// <summary>The scratch target failed, which is not an answer about the planes.</summary>
        Unanswered,
    }

    public static bool UseBytePairPlanes
    {
        get => wideAsBytePairs;
        set
        {
            lock (sync)
            {
                wideAsBytePairs = value;
                probed.Clear();
            }
        }
    }

    public static bool Supports(GRContext? context, VideoPixelFormat format)
    {
        if (!VideoPlanarLayout.IsPlanar(format))
            return true;

        lock (sync)
        {
            if (!ReferenceEquals(probedContext, context))
            {
                probed.Clear();
                probedContext = context;
                unanswered = 0;
            }

            if (probed.TryGetValue(format, out bool supported))
                return supported;

            VideoPlanarLayout layout = VideoPlanarLayout.For(format);
            ProbeOutcome outcome = Probe(context, format);

            // A 16-bit texture format the backend does not hold is worked around by taking those
            // planes apart into bytes. An unanswered native probe is not safe to take on trust:
            // Skia may have converted the raster image to RGBA8 before sampling it. Only when the
            // byte-pair representation fails too is the layout given up on.
            if (outcome != ProbeOutcome.Displayable &&
                layout.BytesPerSample == 2 &&
                !wideAsBytePairs)
            {
                ProbeOutcome nativeOutcome = outcome;
                wideAsBytePairs = true;
                probed.Clear();
                ProbeOutcome bytePairOutcome = Probe(context, format);
                if (bytePairOutcome != ProbeOutcome.NotDisplayable)
                {
                    outcome = ProbeOutcome.Displayable;
                    RendererLog.Information(
                        "Video.Planes",
                        nativeOutcome == ProbeOutcome.Unanswered
                            ? $"{format}: native 16-bit planes could not be verified; using byte pairs."
                            : $"{format}: 16-bit planes taken as byte pairs on this backend.");
                }
                else
                {
                    wideAsBytePairs = false;
                    outcome = ProbeOutcome.NotDisplayable;
                }
            }

            if (outcome == ProbeOutcome.Unanswered)
            {
                if (++unanswered < MaxUnanswered)
                    return true;

                RendererLog.Warning(
                    "Video.Planes",
                    $"{format}: the backend never completed the probe; its planes are taken on trust.");
                probed[format] = true;
                return true;
            }

            bool displayable = outcome == ProbeOutcome.Displayable;
            probed[format] = displayable;
            if (!displayable)
                VideoFrameCapabilities.RejectPlanarSamples(layout.BytesPerSample);

            return displayable;
        }
    }

    /// <summary>
    /// Renders a low-code gray ramp and checks it comes back. The ramp deliberately avoids only
    /// testing a midpoint: an unsupported LSB-aligned 10-bit plane converted to RGBA8 can map its
    /// midpoint back to the right value after sample scaling while reducing the whole image to four
    /// levels. Neutral chroma leaves every output channel equal to the luma value.
    /// </summary>
    private static ProbeOutcome Probe(GRContext? context, VideoPixelFormat format)
    {
        VideoPlanarLayout layout = VideoPlanarLayout.For(format);
        int maxCode = (1 << layout.BitDepth) - 1;

        int chromaCode = 1 << (layout.BitDepth - 1);

        var pool = new VideoFramePool(1);
        VideoFrame frame = pool.Rent(Size, Size, format);
        frame.ColorSpace = VideoColorSpace.Bt709;
        frame.ColorPrimaries = VideoColorPrimaries.Bt709;
        frame.ColorTransfer = VideoColorTransfer.Srgb;
        frame.ColorRange = VideoColorRange.Full;

        try
        {
            FillLumaRamp(frame, layout);
            for (int plane = 1; plane < frame.PlaneCount; plane++)
                FillPlane(frame, layout, plane, chromaCode, chromaCode);

            var info = new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using SKSurface? surface = context is null
                ? SKSurface.Create(info)
                : SKSurface.Create(context, true, info);

            if (surface is null)
            {
                RendererLog.Debug("Video.Planes", $"{format}: no scratch target to probe into.");
                return ProbeOutcome.Unanswered;
            }

            var request = new VideoRenderRequest(
                surface.Canvas,
                context,
                frame,
                SKRect.Create(0, 0, Size, Size),
                byte.MaxValue,
                VideoScalers.For(VideoScalingQuality.NearestNeighbour),
                VideoOutputColorSpace.Srgb,
                HdrPassthrough: false,
                ColorVolume: new VideoOutputColorVolume(VideoOutputTransferFunction.Srgb, 0, null, null, null));

            // Skia declining to describe these planes is an answer about the planes.
            if (!VideoFramePipeline.Render(request))
            {
                RendererLog.Information("Video.Planes", $"{format}: this backend will not sample these planes.");
                return ProbeOutcome.NotDisplayable;
            }

            var pixels = new byte[Size * Size * 4];
            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            bool read = surface.ReadPixels(info, handle.AddrOfPinnedObject(), Size * 4, 0, 0);
            handle.Free();
            if (!read)
            {
                RendererLog.Debug("Video.Planes", $"{format}: the scratch target refused readback.");
                return ProbeOutcome.Unanswered;
            }

            ReadOnlySpan<byte> middleRow = pixels.AsSpan((Size / 2) * Size * 4, Size * 4);
            bool ok = ProbeLumaRampMatches(layout, middleRow, out int failedColumn, out byte expected);

            if (!ok)
            {
                int pixel = failedColumn * 4;
                RendererLog.Information(
                    "Video.Planes",
                    $"{format} is not displayable here at ramp column {failedColumn}: " +
                    $"expected {expected} gray, got " +
                    $"{middleRow[pixel + 2]},{middleRow[pixel + 1]},{middleRow[pixel]}.");
            }

            return ok ? ProbeOutcome.Displayable : ProbeOutcome.NotDisplayable;
        }
        catch (Exception exception)
        {
            RendererLog.Warning("Video.Planes", $"{format} probe failed.", exception);
            return ProbeOutcome.Unanswered;
        }
        finally
        {
            frame.Release();
            pool.Clear();
        }
    }

    internal static int ProbeLumaCode(VideoPlanarLayout layout, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Size);
        int maxCode = (1 << layout.BitDepth) - 1;
        return (int)Math.Round(maxCode * column / (Size * 4.0));
    }

    internal static bool ProbeLumaRampMatches(
        VideoPlanarLayout layout,
        ReadOnlySpan<byte> bgraRow,
        out int failedColumn,
        out byte expected)
    {
        if (bgraRow.Length < Size * 4)
            throw new ArgumentException("The probe row is too short.", nameof(bgraRow));

        int maxCode = (1 << layout.BitDepth) - 1;
        for (int column = 0; column < Size; column++)
        {
            expected = (byte)Math.Round(ProbeLumaCode(layout, column) / (double)maxCode * 255.0);
            int pixel = column * 4;
            if (Math.Abs(bgraRow[pixel] - expected) > 4 ||
                Math.Abs(bgraRow[pixel + 1] - expected) > 4 ||
                Math.Abs(bgraRow[pixel + 2] - expected) > 4)
            {
                failedColumn = column;
                return false;
            }
        }

        failedColumn = -1;
        expected = 0;
        return true;
    }

    private static void FillLumaRamp(VideoFrame frame, VideoPlanarLayout layout)
    {
        int shift = layout.MsbAligned ? 16 - layout.BitDepth : 0;
        int stride = frame.GetStride(0);
        int rowBytes = frame.GetRowBytes(0);
        var row = new byte[rowBytes];

        for (int column = 0; column < Size; column++)
        {
            int offset = column * layout.BytesPerSample;
            int value = ProbeLumaCode(layout, column) << shift;
            row[offset] = (byte)value;
            if (layout.BytesPerSample == 2)
                row[offset + 1] = (byte)(value >> 8);
        }

        for (int y = 0; y < frame.GetPlaneHeight(0); y++)
            Marshal.Copy(row, 0, frame.GetPlane(0) + (y * stride), rowBytes);
    }

    private static void FillPlane(VideoFrame frame, VideoPlanarLayout layout, int plane, int first, int second)
    {
        int shift = layout.MsbAligned ? 16 - layout.BitDepth : 0;
        int stride = frame.GetStride(plane);
        int rowBytes = frame.GetRowBytes(plane);
        bool pair = plane > 0 && layout.InterleavedChroma;

        var row = new byte[rowBytes];
        for (int offset = 0; offset < rowBytes; offset += layout.BytesPerSample)
        {
            bool secondOfPair = pair && (offset / layout.BytesPerSample) % 2 == 1;
            int value = (secondOfPair ? second : first) << shift;
            row[offset] = (byte)value;
            if (layout.BytesPerSample == 2)
                row[offset + 1] = (byte)(value >> 8);
        }

        for (int y = 0; y < frame.GetPlaneHeight(plane); y++)
            Marshal.Copy(row, 0, frame.GetPlane(plane) + (y * stride), rowBytes);
    }
}
