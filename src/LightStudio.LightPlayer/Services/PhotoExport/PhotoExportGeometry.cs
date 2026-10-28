using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>Where the upright picture lands on the output canvas, and which part of it is shown.</summary>
/// <param name="Crop">The shown region of the upright picture, in its display pixels.</param>
internal readonly record struct PhotoExportLayout(
    int Width,
    int Height,
    int ContentLeft,
    int ContentTop,
    int ContentWidth,
    int ContentHeight,
    (double Left, double Top, double Width, double Height) Crop)
{
    public static PhotoExportLayout Create(double displayWidth, double displayHeight, int width, int height,
        PhotoExportResize resize)
    {
        var whole = (0d, 0d, displayWidth, displayHeight);
        switch (resize)
        {
            case PhotoExportResize.Fit:
            {
                double scale = Math.Min(width / displayWidth, height / displayHeight);
                int contentWidth = Math.Clamp((int)Math.Round(displayWidth * scale), 1, width);
                int contentHeight = Math.Clamp((int)Math.Round(displayHeight * scale), 1, height);
                return new PhotoExportLayout(width, height, (width - contentWidth) / 2, (height - contentHeight) / 2,
                    contentWidth, contentHeight, whole);
            }

            case PhotoExportResize.Fill:
            {
                double scale = Math.Max(width / displayWidth, height / displayHeight);
                double cropWidth = Math.Min(displayWidth, width / scale), cropHeight = Math.Min(displayHeight, height / scale);
                return new PhotoExportLayout(width, height, 0, 0, width, height,
                    ((displayWidth - cropWidth) / 2, (displayHeight - cropHeight) / 2, cropWidth, cropHeight));
            }

            default:
                return new PhotoExportLayout(width, height, 0, 0, width, height, whole);
        }
    }
}

/// <summary>How the stored pixels become the upright picture.</summary>
/// <param name="Rotation">Clockwise quarter turns in degrees, applied after mirroring.</param>
/// <param name="SourceWidth">Stored width of the base image the layout was computed for.</param>
internal readonly record struct PhotoExportOrientation(int Rotation, bool Mirror, double PixelAspectRatio,
    int SourceWidth, int SourceHeight)
{
    public bool IsQuarterTurn => Rotation is 90 or 270;

    public double DisplayWidth => IsQuarterTurn ? SourceHeight : SourceWidth * PixelAspectRatio;

    public double DisplayHeight => IsQuarterTurn ? SourceWidth * PixelAspectRatio : SourceHeight;

    /// <summary>Maps a point of the upright picture back to stored pixel coordinates.</summary>
    public (double X, double Y) ToSource(double x, double y)
    {
        double width = SourceWidth * PixelAspectRatio, height = SourceHeight;
        (double u, double v) = Rotation switch
        {
            90 => (y, height - x),
            180 => (width - x, height - y),
            270 => (width - y, x),
            _ => (x, y),
        };
        return ((Mirror ? width - u : u) / PixelAspectRatio, v);
    }
}

/// <summary>Orients, resamples and places images in linear light with premultiplied alpha.</summary>
internal static unsafe class PhotoExportGeometry
{
    /// <summary>
    /// Places <paramref name="source"/> on a canvas <paramref name="scale"/> times the layout's size; areas
    /// outside the picture are transparent. Every core converts a few source rows at a time and resamples
    /// them right away, so only the rows the output shows are converted and full-resolution light is never
    /// held at once when the size changes.
    /// </summary>
    /// <param name="statistics">Receives the colors of the source rows the output shows.</param>
    public static LinearImage Place(IPhotoExportRows source, PhotoExportOrientation orientation, PhotoExportLayout layout,
        double scale, PhotoExportGamutStatistics? statistics, CancellationToken cancellationToken)
    {
        int canvasWidth = Math.Max(1, (int)Math.Round(layout.Width * scale));
        int canvasHeight = Math.Max(1, (int)Math.Round(layout.Height * scale));
        int left = Math.Clamp((int)Math.Round(layout.ContentLeft * scale), 0, canvasWidth - 1);
        int top = Math.Clamp((int)Math.Round(layout.ContentTop * scale), 0, canvasHeight - 1);
        int contentWidth = Math.Clamp((int)Math.Round(layout.ContentWidth * scale), 1, canvasWidth - left);
        int contentHeight = Math.Clamp((int)Math.Round(layout.ContentHeight * scale), 1, canvasHeight - top);

        (double cropLeft, double cropTop, double cropWidth, double cropHeight) = layout.Crop;
        (double x0, double y0) = orientation.ToSource(cropLeft, cropTop);
        (double x1, double y1) = orientation.ToSource(cropLeft + cropWidth, cropTop + cropHeight);
        double scaleX = source.Width / (double)orientation.SourceWidth, scaleY = source.Height / (double)orientation.SourceHeight;
        (x0, x1) = (Math.Min(x0, x1) * scaleX, Math.Max(x0, x1) * scaleX);
        (y0, y1) = (Math.Min(y0, y1) * scaleY, Math.Max(y0, y1) * scaleY);

        int targetWidth = orientation.IsQuarterTurn ? contentHeight : contentWidth;
        int targetHeight = orientation.IsQuarterTurn ? contentWidth : contentHeight;
        bool identity = targetWidth == source.Width && targetHeight == source.Height &&
            Math.Abs(x0) < 1e-6 && Math.Abs(y0) < 1e-6 &&
            Math.Abs(x1 - source.Width) < 1e-6 && Math.Abs(y1 - source.Height) < 1e-6;

        var canvas = new LinearImage(canvasWidth, canvasHeight);
        try
        {
            var target = new OrientedTarget(canvas, orientation.Rotation, orientation.Mirror, left, top, targetWidth, targetHeight);
            if (identity)
                Copy(source, target, statistics, cancellationToken);
            else
                Resample(source, x0, y0, x1, y1, target, statistics, cancellationToken);
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    /// <summary>Source pixels one core converts at a time, few enough to stay in its cache.</summary>
    private const int ChunkPixels = 65536;

    /// <summary>Horizontally resampled light kept at once for the vertical pass, unless one output row needs more.</summary>
    private const long StripBytes = 64L * 1024 * 1024;

    private delegate void ConvertedRows(int first, int count, float* pixels, int stride);

    /// <summary>The most horizontally resampled pixels <see cref="Place"/> keeps at once.</summary>
    /// <param name="sourceHeight">Stored height of the source.</param>
    /// <param name="targetWidth">Width of the placed picture before orientation.</param>
    /// <param name="targetHeight">Height of the placed picture before orientation.</param>
    public static long ResampledPixels(int sourceHeight, int targetWidth, int targetHeight)
    {
        long filterRows = (long)Math.Ceiling(4 * Math.Max(1, sourceHeight / (double)targetHeight)) + 2;
        return targetWidth * Math.Min(sourceHeight, Math.Max(StripBytes / (targetWidth * 16L), filterRows));
    }

    /// <summary>
    /// Converts <paramref name="count"/> source rows from <paramref name="first"/> on every core a few rows
    /// at a time, records the colors of the given columns in rows from <paramref name="recordFrom"/>, and
    /// hands each chunk to <paramref name="consume"/> with its first row relative to <paramref name="first"/>.
    /// The browser converts the chunks in order on the calling thread.
    /// </summary>
    private static void ConvertRows(IPhotoExportRows source, int first, int count, int recordFrom, int firstColumn,
        int columnCount, PhotoExportGamutStatistics? statistics, CancellationToken cancellationToken, ConvertedRows consume)
    {
        int chunkRows = Math.Clamp(ChunkPixels / source.Width, 1, count);
        int chunks = (count + chunkRows - 1) / chunkRows;
        if (OperatingSystem.IsBrowser())
        {
            using var buffer = new LinearImage(source.Width, chunkRows);
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConvertChunk(chunk, buffer);
            }

            return;
        }

        Parallel.For(0, chunks,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            () => new LinearImage(source.Width, chunkRows),
            (chunk, _, buffer) =>
            {
                ConvertChunk(chunk, buffer);
                return buffer;
            },
            buffer => buffer.Dispose());

        void ConvertChunk(int chunk, LinearImage buffer)
        {
            int start = chunk * chunkRows, rows = Math.Min(chunkRows, count - start);
            source.Render(first + start, rows, buffer.Pointer, buffer.Stride, cancellationToken);
            int skipped = Math.Clamp(recordFrom - first - start, 0, rows);
            if (skipped < rows)
                statistics?.Add((IntPtr)(buffer.Row(skipped) + (long)firstColumn * 4), columnCount, rows - skipped,
                    buffer.Stride, cancellationToken);
            consume(start, rows, buffer.Row(0), buffer.Stride);
        }
    }

    private static void Copy(IPhotoExportRows source, OrientedTarget target, PhotoExportGamutStatistics? statistics,
        CancellationToken cancellationToken)
    {
        int width = source.Width;
        ConvertRows(source, 0, source.Height, 0, 0, width, statistics, cancellationToken, (first, count, pixels, stride) =>
        {
            for (int row = 0; row < count; row++)
            {
                float* input = pixels + (long)row * stride;
                if (target.IsUpright)
                {
                    Buffer.MemoryCopy(input, target.Pixel(0, first + row), (long)width * 16, (long)width * 16);
                    continue;
                }

                for (int x = 0; x < width; x++)
                    *(Vector4*)target.Pixel(x, first + row) = *(Vector4*)(input + x * 4);
            }
        });
    }

    /// <summary>
    /// Resamples a region with a Mitchell-Netravali filter, widened for reductions so that every
    /// source pixel contributes. Filtering happens on premultiplied light, in strips of output rows
    /// whose horizontally resampled source rows fit in <see cref="StripBytes"/>.
    /// </summary>
    private static void Resample(IPhotoExportRows source, double x0, double y0, double x1, double y1, OrientedTarget target,
        PhotoExportGamutStatistics? statistics, CancellationToken cancellationToken)
    {
        int width = target.Width, height = target.Height;
        Contributions columns = Contributions.Create(x0, x1 - x0, source.Width, width);
        Contributions rows = Contributions.Create(y0, y1 - y0, source.Height, height);
        int firstColumn = columns.MinimumIndex, columnCount = columns.MaximumIndex - firstColumn + 1;
        long stripRows = Math.Max(1, StripBytes / (width * 16L));
        var strips = new List<(int First, int Last, int Top, int Count)>();
        int intermediateRows = 1;
        for (int first = 0; first < height;)
        {
            int top = int.MaxValue, bottom = -1, last = first;
            for (; last < height; last++)
            {
                int rowTop = top, rowBottom = bottom;
                for (int tap = rows.Offsets[last]; tap < rows.Offsets[last + 1]; tap++)
                    (rowTop, rowBottom) = (Math.Min(rowTop, rows.Indices[tap]), Math.Max(rowBottom, rows.Indices[tap]));
                if (last > first && rowBottom - rowTop + 1 > stripRows)
                    break;
                (top, bottom) = (rowTop, rowBottom);
            }

            strips.Add((first, last, top, bottom - top + 1));
            intermediateRows = Math.Max(intermediateRows, bottom - top + 1);
            first = last;
        }

        using var intermediate = new LinearImage(width, intermediateRows);
        int recorded = 0;
        foreach ((int firstOutput, int lastOutput, int top, int count) in strips)
        {
            ConvertRows(source, top, count, recorded, firstColumn, columnCount, statistics, cancellationToken,
                (first, chunkRows, pixels, stride) =>
                {
                    for (int row = 0; row < chunkRows; row++)
                    {
                        float* input = pixels + (long)row * stride;
                        float* output = intermediate.Row(first + row);
                        for (int x = 0; x < width; x++)
                        {
                            Vector4 sum = Vector4.Zero;
                            int end = columns.Offsets[x + 1];
                            for (int tap = columns.Offsets[x]; tap < end; tap++)
                            {
                                Vector4 pixel = *(Vector4*)(input + columns.Indices[tap] * 4);
                                float premultiplied = columns.Weights[tap] * pixel.W;
                                sum += new Vector4(pixel.X * premultiplied, pixel.Y * premultiplied, pixel.Z * premultiplied,
                                    premultiplied);
                            }

                            *(Vector4*)(output + x * 4) = sum;
                        }
                    }
                });
            recorded = Math.Max(recorded, top + count);

            PhotoExportColor.ForRows(lastOutput - firstOutput, rows.Indices.Length / Math.Max(1, height) * width,
                cancellationToken, (first, last) =>
                {
                    float* intermediatePixels = intermediate.Row(0);
                    int intermediateStride = intermediate.Stride;
                    for (int y = firstOutput + first; y < firstOutput + last; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int start = rows.Offsets[y], end = rows.Offsets[y + 1];
                        for (int x = 0; x < width; x++)
                        {
                            Vector4 sum = Vector4.Zero;
                            for (int tap = start; tap < end; tap++)
                            {
                                long row = rows.Indices[tap] - top;
                                sum += *(Vector4*)(intermediatePixels + row * intermediateStride + x * 4) * rows.Weights[tap];
                            }

                            float alpha = Math.Clamp(sum.W, 0, 1);
                            float inverse = alpha > 1e-6f ? 1 / alpha : 0;
                            *(Vector4*)target.Pixel(x, y) = new Vector4(sum.X * inverse, sum.Y * inverse, sum.Z * inverse, alpha);
                        }
                    }
                });
        }
    }

    /// <summary>Where a pixel of the picture in stored orientation lands on the canvas.</summary>
    private readonly struct OrientedTarget
    {
        private readonly IntPtr pixels;
        private readonly int rotation;
        private readonly bool mirror;
        private readonly int left;
        private readonly int top;

        public OrientedTarget(LinearImage canvas, int rotation, bool mirror, int left, int top, int width, int height)
        {
            pixels = canvas.Pointer;
            Stride = canvas.Stride;
            this.rotation = rotation;
            this.mirror = mirror;
            this.left = left;
            this.top = top;
            Width = width;
            Height = height;
        }

        /// <summary>Width of the placed picture before orientation.</summary>
        public int Width { get; }

        public int Height { get; }

        /// <summary>Canvas floats per row.</summary>
        public int Stride { get; }

        public bool IsUpright => rotation == 0 && !mirror;

        public float* Pixel(int x, int y)
        {
            int sourceX = mirror ? Width - 1 - x : x;
            (int targetX, int targetY) = rotation switch
            {
                90 => (Height - 1 - y, sourceX),
                180 => (Width - 1 - sourceX, Height - 1 - y),
                270 => (y, Width - 1 - sourceX),
                _ => (sourceX, y),
            };
            return (float*)pixels + (long)(top + targetY) * Stride + (long)(left + targetX) * 4;
        }
    }

    /// <summary>The source taps and normalized weights of every output pixel along one axis.</summary>
    private sealed class Contributions
    {
        private Contributions(int[] offsets, int[] indices, float[] weights)
        {
            Offsets = offsets;
            Indices = indices;
            Weights = weights;
            MinimumIndex = indices.Length == 0 ? 0 : int.MaxValue;
            foreach (int index in indices)
            {
                MinimumIndex = Math.Min(MinimumIndex, index);
                MaximumIndex = Math.Max(MaximumIndex, index);
            }
        }

        public int[] Offsets { get; }
        public int[] Indices { get; }
        public float[] Weights { get; }
        public int MinimumIndex { get; }
        public int MaximumIndex { get; }

        public static Contributions Create(double start, double length, int sourceSize, int targetSize)
        {
            double step = length / targetSize;
            double support = Math.Max(1, step);
            int taps = (int)Math.Ceiling(4 * support) + 2;
            var offsets = new int[targetSize + 1];
            var indices = new int[checked(targetSize * taps)];
            var weights = new float[indices.Length];
            int count = 0;
            for (int target = 0; target < targetSize; target++)
            {
                offsets[target] = count;
                double center = start + (target + 0.5) * step;
                int first = (int)Math.Floor(center - 2 * support), last = (int)Math.Ceiling(center + 2 * support);
                double total = 0;
                int begin = count;
                for (int tap = first; tap <= last && count - begin < taps; tap++)
                {
                    double weight = Mitchell((tap + 0.5 - center) / support);
                    if (weight == 0)
                        continue;
                    indices[count] = Math.Clamp(tap, 0, sourceSize - 1);
                    weights[count] = (float)weight;
                    total += weight;
                    count++;
                }

                if (Math.Abs(total) < 1e-9)
                {
                    count = begin;
                    indices[count] = Math.Clamp((int)Math.Floor(center), 0, sourceSize - 1);
                    weights[count] = 1;
                    count++;
                }
                else
                {
                    for (int tap = begin; tap < count; tap++)
                        weights[tap] = (float)(weights[tap] / total);
                }
            }

            offsets[targetSize] = count;
            return new Contributions(offsets, indices[..count], weights[..count]);
        }

        /// <summary>Mitchell-Netravali with B = C = 1/3.</summary>
        private static double Mitchell(double x)
        {
            const double b = 1.0 / 3, c = 1.0 / 3;
            x = Math.Abs(x);
            if (x < 1)
                return ((12 - 9 * b - 6 * c) * x * x * x + (-18 + 12 * b + 6 * c) * x * x + (6 - 2 * b)) / 6;
            if (x < 2)
                return ((-b - 6 * c) * x * x * x + (6 * b + 30 * c) * x * x + (-12 * b - 48 * c) * x + (8 * b + 24 * c)) / 6;
            return 0;
        }
    }
}
