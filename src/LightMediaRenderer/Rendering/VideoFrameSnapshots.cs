using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Software;

namespace LightMediaRenderer.Rendering;

/// <summary>Turns video pictures into <see cref="VideoFrameSnapshot"/> pixels on the CPU.</summary>
public static unsafe class VideoFrameSnapshots
{
    private const int MaximumDimension = 65536;

    /// <summary>
    /// Whether <see cref="Decode"/> can read the frame as it was decoded: a progressive picture whose
    /// samples are in memory or can be downloaded, and which no device resized on its way here.
    /// </summary>
    /// <remarks>
    /// Interlaced pictures are excluded because the renderer reconstructs them from the frames before,
    /// which a single decoded frame cannot reproduce.
    /// </remarks>
    public static bool CanDecode(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame is
            {
                Width: > 0 and <= MaximumDimension,
                Height: > 0 and <= MaximumDimension,
                IsHardwareScaled: false,
                CoversOutput: false,
                PlaneSource: null,
                PhotoTiles: null,
                PhotoSurface: null,
                PhotoColor: null,
                GainMap: null,
            } &&
            !frame.FieldOrder.IsInterlaced() &&
            frame.Format is not (VideoPixelFormat.ExternalTexture or VideoPixelFormat.RgbaF16) &&
            (frame.HasSurface ? frame.DownloadSamples is not null : frame.Data != IntPtr.Zero) &&
            (frame.EnhancementLayer is null || !frame.EnhancementLayer.HasSurface && frame.EnhancementLayer.Data != IntPtr.Zero);
    }

    /// <summary>
    /// Converts the frame to RGB at its stored resolution, turned upright. Standard and HDR10 pictures
    /// keep the signal, transfer and primaries they were coded in; Dolby Vision is reconstructed into
    /// PQ on BT.2020 primaries. Nothing is tone mapped, scaled or adapted to a display.
    /// </summary>
    /// <remarks>Runs on the calling thread, downloading a graphics-memory frame first.</remarks>
    public static VideoFrameSnapshot Decode(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        if (!CanDecode(frame))
            throw new ArgumentException("The frame's samples cannot be read as decoded.", nameof(frame));
        cancellationToken.ThrowIfCancellationRequested();
        if (!frame.HasSurface)
            return Convert(frame, cancellationToken);

        VideoFrame downloaded = frame.DownloadSamples!() ??
            throw new InvalidOperationException("The frame's graphics-memory samples could not be downloaded.");
        try
        {
            if (downloaded.Width != frame.Width || downloaded.Height != frame.Height || downloaded.HasSurface ||
                downloaded.Data == IntPtr.Zero)
                throw new InvalidDataException("The downloaded samples do not match the frame.");
            return Convert(downloaded, cancellationToken);
        }
        finally
        {
            downloaded.Release();
        }
    }

    /// <summary>Copies 8-bit sRGB pixels stored in B, G, R, A order, as an output surface reads back.</summary>
    public static VideoFrameSnapshot FromSrgb(int width, int height, ReadOnlyMemory<byte> bgra, int rowBytes,
        TimeSpan timestamp = default)
    {
        if (rowBytes < width * 4 || bgra.Length < (long)rowBytes * (height - 1) + width * 4)
            throw new ArgumentException("The pixels do not cover the picture.", nameof(bgra));
        var snapshot = new VideoFrameSnapshot(width, height, 8)
        {
            Primaries = VideoColorPrimaries.Bt709,
            Transfer = VideoColorTransfer.Srgb,
            Timestamp = timestamp,
            Source = VideoFrameSnapshotSource.Rendered,
        };
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> source = bgra.Span.Slice(y * rowBytes, width * 4);
            Span<byte> row = snapshot.GetRow(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = source[x * 4 + 2];
                row[x * 3 + 1] = source[x * 4 + 1];
                row[x * 3 + 2] = source[x * 4];
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Encodes linear light on sRGB primaries, such as an extended-range surface reads back, as 16-bit
    /// PQ on BT.2020 primaries. The fourth component of each pixel is ignored.
    /// </summary>
    /// <param name="nitsPerUnit">Luminance a component value of 1 stands for.</param>
    public static VideoFrameSnapshot FromLinear(int width, int height, ReadOnlyMemory<float> rgba, double nitsPerUnit,
        TimeSpan timestamp = default, CancellationToken cancellationToken = default)
    {
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException("The pixels do not cover the picture.", nameof(rgba));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(nitsPerUnit, 0);
        float[] gamut = Array.ConvertAll(VideoColorGamut.Matrix(VideoColorPrimaries.Bt709, VideoColorPrimaries.Bt2020),
            value => (float)value);
        float scale = (float)nitsPerUnit;
        var snapshot = new VideoFrameSnapshot(width, height, 16)
        {
            Primaries = VideoColorPrimaries.Bt2020,
            Transfer = VideoColorTransfer.Pq,
            Timestamp = timestamp,
            Source = VideoFrameSnapshotSource.Rendered,
        };
        try
        {
            byte* target = (byte*)snapshot.Address;
            ForRows(width, height, cancellationToken, (first, last) =>
            {
                ReadOnlySpan<float> source = rgba.Span;
                for (int y = first; y < last; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ushort* row = (ushort*)(target + (long)y * snapshot.RowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        int index = (y * width + x) * 4;
                        float r = Finite(source[index]) * scale, g = Finite(source[index + 1]) * scale,
                            b = Finite(source[index + 2]) * scale;
                        row[x * 3] = To16(VideoToneCurve.NitsToPq(gamut[0] * r + gamut[1] * g + gamut[2] * b));
                        row[x * 3 + 1] = To16(VideoToneCurve.NitsToPq(gamut[3] * r + gamut[4] * g + gamut[5] * b));
                        row[x * 3 + 2] = To16(VideoToneCurve.NitsToPq(gamut[6] * r + gamut[7] * g + gamut[8] * b));
                    }
                }
            });
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static VideoFrameSnapshot Convert(VideoFrame frame, CancellationToken cancellationToken)
    {
        bool dolbyVision = frame.DolbyVision.IsApplicable;
        int depth = dolbyVision ? 16 : frame.Format switch
        {
            VideoPixelFormat.Bgra32 => 8,
            VideoPixelFormat.Rgba64 => 16,
            _ => frame.Layout.BitDepth,
        };
        int rotation = ((frame.Rotation % 360) + 360) % 360 / 90 * 90;
        bool turned = rotation is 90 or 270;
        double aspect = frame.PixelAspectRatio > 0 && double.IsFinite(frame.PixelAspectRatio) ? frame.PixelAspectRatio : 1;
        int width = frame.Width, height = frame.Height;
        var snapshot = new VideoFrameSnapshot(turned ? height : width, turned ? width : height, depth)
        {
            Primaries = dolbyVision ? VideoColorPrimaries.Bt2020 : frame.ColorPrimaries,
            Transfer = dolbyVision ? VideoColorTransfer.Pq : frame.ColorTransfer,
            PixelAspectRatio = turned ? 1 / aspect : aspect,
            Timestamp = double.IsFinite(frame.Pts) && frame.Pts > 0 ? TimeSpan.FromSeconds(Math.Min(frame.Pts, 1e9)) : TimeSpan.Zero,
            Source = VideoFrameSnapshotSource.Decoded,
        };
        try
        {
            var color = new VideoCpuColorPipeline(frame, sceneLight: true);
            VideoFrame? enhancement = dolbyVision && frame.DolbyVision.EnhancementLayerResidual ? frame.EnhancementLayer : null;
            bool mirror = frame.MirrorHorizontally;
            bool wide = snapshot.BytesPerSample == 2;
            byte* target = (byte*)snapshot.Address;
            long rowBytes = snapshot.RowBytes;
            ForRows(width, height, cancellationToken, (first, last) =>
            {
                float[] scratch = ArrayPool<float>.Shared.Rent(width * 7);
                try
                {
                    Span<float> r = scratch.AsSpan(0, width), g = scratch.AsSpan(width, width),
                        b = scratch.AsSpan(width * 2, width), alpha = scratch.AsSpan(width * 3, width),
                        er = scratch.AsSpan(width * 4, width), eg = scratch.AsSpan(width * 5, width),
                        eb = scratch.AsSpan(width * 6, width);
                    for (int y = first; y < last; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        VideoCpuPreview.SampleRow(frame, 0, y, r, g, b, alpha);
                        if (dolbyVision)
                        {
                            if (enhancement is not null)
                            {
                                float scaleX = MathF.Min((float)enhancement.Width / width, 1);
                                float row = (y + 0.5f) * MathF.Min((float)enhancement.Height / height, 1) - 0.5f;
                                for (int x = 0; x < width; x++)
                                    VideoCpuPreview.Sample(enhancement, x * scaleX, row, out er[x], out eg[x], out eb[x], out _);
                            }

                            color.Decode(r, g, b, er, eg, eb);
                            for (int x = 0; x < width; x++)
                            {
                                r[x] = VideoToneCurve.NitsToPq(r[x]);
                                g[x] = VideoToneCurve.NitsToPq(g[x]);
                                b[x] = VideoToneCurve.NitsToPq(b[x]);
                            }
                        }
                        else
                        {
                            color.DecodeSignal(r, g, b);
                        }

                        for (int x = 0; x < width; x++)
                        {
                            int sx = mirror ? width - 1 - x : x;
                            (int dx, int dy) = rotation switch
                            {
                                90 => (height - 1 - y, sx),
                                180 => (width - 1 - sx, height - 1 - y),
                                270 => (y, width - 1 - sx),
                                _ => (sx, y),
                            };
                            byte* pixel = target + dy * rowBytes;
                            if (wide)
                            {
                                ushort* samples = (ushort*)pixel + dx * 3;
                                samples[0] = To16(r[x]);
                                samples[1] = To16(g[x]);
                                samples[2] = To16(b[x]);
                            }
                            else
                            {
                                byte* samples = pixel + dx * 3;
                                samples[0] = To8(r[x]);
                                samples[1] = To8(g[x]);
                                samples[2] = To8(b[x]);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(scratch);
                }
            });
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;

    private static byte To8(float value) => (byte)(Math.Clamp(value, 0, 1) * 255 + 0.5f);

    private static ushort To16(float value) => (ushort)(Math.Clamp(value, 0, 1) * 65535 + 0.5f);

    /// <summary>Runs a callback over bands of rows, in parallel for large pictures outside the browser.</summary>
    private static void ForRows(int width, int height, CancellationToken cancellationToken, Action<int, int> band)
    {
        int workers = OperatingSystem.IsBrowser() ? 1
            : Math.Clamp((int)((long)width * height / 262144), 1, Math.Min(Environment.ProcessorCount, height));
        if (workers == 1)
        {
            band(0, height);
            return;
        }

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            worker => band((int)((long)height * worker / workers), (int)((long)height * (worker + 1) / workers)));
    }
}
