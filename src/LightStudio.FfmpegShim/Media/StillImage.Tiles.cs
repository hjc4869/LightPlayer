using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Interop;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

public static unsafe partial class StillImage
{
    private static Func<TileSource, IPhotoTileSource> tileDecoderFactory;

    public static void RegisterTileDecoder(Func<TileSource, IPhotoTileSource> factory) =>
        tileDecoderFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    public static TileSource OpenTiles(Stream stream, uint? itemId = null, CancellationToken cancellationToken = default,
        bool includeSingleImages = false)
    {
        long started = Stopwatch.GetTimestamp();
        long origin = stream.Position;
        bool hasExif = ExifOrientation.TryRead(stream, out int exifRotation, out bool exifMirror);
        IsoImageProperties properties = IsoImageProperties.Read(stream);
        double densityAspect = ReadDensityAspectRatio(stream);
        long metadataRead = Stopwatch.GetTimestamp();
        hasExif &= properties == null;
        using var io = new FfmpegStreamIO(stream, IoBufferSize);
        AVFormatContext* format = avformat_alloc_context();
        if (format == null)
            return null;
        format->pb = io.Context;
        AVPacket* packet = null;
        AVFrame* metadata = null;
        var tiles = new List<EncodedTile>();
        try
        {
            if (avformat_open_input(&format, string.Empty, null, null) < 0)
                return null;
            long inputOpened = Stopwatch.GetTimestamp();
            if (avformat_find_stream_info(format, null) < 0)
                return null;
            long streamsProbed = Stopwatch.GetTimestamp();
            itemId ??= FindPrimaryItem(format, properties);
            if (itemId is { } primaryId && properties?.FindAlpha(primaryId) is not null)
                return null;
            AVStreamGroup* mosaic = FindTileGrid(format, itemId);
            if (mosaic == null)
                return includeSingleImages ? OpenSingleTile(format, itemId, properties, densityAspect,
                    hasExif, exifRotation, exifMirror, cancellationToken) : null;
            ApplyGridCrop(mosaic, properties?.Get((uint)mosaic->id));
            AVStreamGroupTileGrid* grid = mosaic->@params.tile_grid;
            if (grid->nb_tiles is 0 or > 65536 || grid->offsets == null || grid->width <= 0 || grid->height <= 0 ||
                grid->coded_width <= 0 || grid->coded_height <= 0 || grid->horizontal_offset < 0 || grid->vertical_offset < 0 ||
                (long)grid->horizontal_offset + grid->width > grid->coded_width ||
                (long)grid->vertical_offset + grid->height > grid->coded_height)
                return null;
            var tileOfStream = new int[format->nb_streams];
            Array.Fill(tileOfStream, -1);
            for (uint index = 0; index < grid->nb_tiles; index++)
            {
                uint member = grid->offsets[index].idx;
                if (member >= mosaic->nb_streams)
                    return null;
                AVStream* item = mosaic->streams[member];
                if (item->index < 0 || item->index >= tileOfStream.Length || tileOfStream[item->index] != -1)
                    return null;
                tileOfStream[item->index] = (int)index;
            }
            for (int index = 0; index < tileOfStream.Length; index++)
                format->streams[index]->discard = tileOfStream[index] >= 0 ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            packet = av_packet_alloc();
            metadata = av_frame_alloc();
            if (packet == null || metadata == null)
                return null;
            while (tiles.Count < grid->nb_tiles && av_read_frame(format, packet) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int streamIndex = packet->stream_index;
                    int index = streamIndex >= 0 && streamIndex < tileOfStream.Length ? tileOfStream[streamIndex] : -1;
                    if (index < 0)
                        continue;
                    AVCodecParameters* parameters = avcodec_parameters_alloc();
                    AVPacket* retained = av_packet_alloc();
                    var tile = new EncodedTile { Parameters = (IntPtr)parameters, Packet = (IntPtr)retained };
                    tiles.Add(tile);
                    if (parameters == null || retained == null ||
                        avcodec_parameters_copy(parameters, format->streams[streamIndex]->codecpar) < 0 ||
                        av_packet_ref(retained, packet) < 0)
                        return null;
                    if (parameters->width <= 0 || parameters->height <= 0 ||
                        grid->offsets[(uint)index].horizontal < 0 || grid->offsets[(uint)index].vertical < 0 ||
                        (long)grid->offsets[(uint)index].horizontal + parameters->width > grid->coded_width ||
                        (long)grid->offsets[(uint)index].vertical + parameters->height > grid->coded_height)
                        return null;
                    tile.Bounds = new PhotoTileBounds(grid->offsets[(uint)index].horizontal - grid->horizontal_offset,
                        grid->offsets[(uint)index].vertical - grid->vertical_offset, parameters->width, parameters->height);
                    tileOfStream[streamIndex] = -1;
                }
                finally { av_packet_unref(packet); }
            }
            if (tiles.Count != grid->nb_tiles)
                return null;
            AVCodecParameters* primary = (AVCodecParameters*)tiles[0].Parameters;
            ApplyTileGridOrientation(mosaic, metadata);
            ReadOrientation(metadata, primary, hasExif, exifRotation, exifMirror, out int rotation, out bool mirror);
            AVPacketSideData* sideData = primary->coded_side_data != null && primary->nb_coded_side_data > 0
                ? av_packet_side_data_get(primary->coded_side_data, primary->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_ICC_PROFILE)
                : null;
            byte[] profile = sideData != null && sideData->data != null && sideData->size is > 0 and <= 4 * 1024 * 1024
                ? new ReadOnlySpan<byte>(sideData->data, (int)sideData->size).ToArray() : [];
            var result = new TileSource(tiles.ToArray(), grid->width, grid->height, rotation, mirror,
                profile, (int)primary->color_primaries, (int)primary->color_trc,
                properties?.Get((uint)mosaic->id)?.PixelAspectRatio is > 0 and var aspect ? aspect :
                    Utils.RationalToDouble(ReadSampleAspectRatio(metadata, primary)));
            tiles.Clear();
            AppLog.Information("Photo.Decoding", $"Opened {result.Width}x{result.Height} photo tiles: " +
                $"tiles={result.Count}, encodedBytes={result.EncodedBytes}, " +
                $"metadata={Stopwatch.GetElapsedTime(started, metadataRead).TotalMilliseconds:F1} ms, " +
                $"open={Stopwatch.GetElapsedTime(metadataRead, inputOpened).TotalMilliseconds:F1} ms, " +
                $"probe={Stopwatch.GetElapsedTime(inputOpened, streamsProbed).TotalMilliseconds:F1} ms, " +
                $"packets={Stopwatch.GetElapsedTime(streamsProbed).TotalMilliseconds:F1} ms.");
            return result;
        }
        finally
        {
            foreach (EncodedTile tile in tiles)
                tile.Dispose();
            if (packet != null)
                av_packet_free(&packet);
            if (metadata != null)
                av_frame_free(&metadata);
            if (format != null)
                avformat_close_input(&format);
            stream.Position = origin;
        }
    }

    private static TileSource OpenSingleTile(AVFormatContext* format, uint? itemId, IsoImageProperties properties, double densityAspect, bool hasExif,
        int exifRotation, bool exifMirror, CancellationToken cancellationToken)
    {
        int index = itemId is { } selected ? FindItemStream(format, selected) :
            av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (index < 0)
            return null;
        AVCodecParameters* original = format->streams[index]->codecpar;
        if (original->width <= 0 || original->height <= 0)
            return null;
        int width = original->width;
        int height = original->height;
        int left = 0;
        int top = 0;
        if (TryReadCrop(original, width, height, out var crop))
        {
            width -= (int)(crop.Left + crop.Right);
            height -= (int)(crop.Top + crop.Bottom);
            left = -(int)crop.Left;
            top = -(int)crop.Top;
        }
        var tile = new EncodedTile
        {
            Packet = (IntPtr)av_packet_alloc(),
            Parameters = (IntPtr)avcodec_parameters_alloc(),
            Bounds = new PhotoTileBounds(left, top, original->width, original->height),
        };
        bool retained = false;
        try
        {
            if (tile.Packet == IntPtr.Zero || tile.Parameters == IntPtr.Zero ||
                avcodec_parameters_copy((AVCodecParameters*)tile.Parameters, original) < 0)
                return null;
            var packet = (AVPacket*)tile.Packet;
            for (int streamIndex = 0; streamIndex < format->nb_streams; streamIndex++)
                format->streams[streamIndex]->discard = streamIndex == index ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            while (av_read_frame(format, packet) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (packet->stream_index != index)
                {
                    av_packet_unref(packet);
                    continue;
                }
                VideoDisplayMatrix.Read(original, out int rotation, out bool mirror);
                if (rotation == 0 && !mirror && hasExif)
                {
                    rotation = exifRotation;
                    mirror = exifMirror;
                }
                AVPacketSideData* sideData = original->coded_side_data != null && original->nb_coded_side_data > 0
                    ? av_packet_side_data_get(original->coded_side_data, original->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_ICC_PROFILE)
                    : null;
                byte[] profile = sideData != null && sideData->data != null && sideData->size is > 0 and <= 4 * 1024 * 1024
                    ? new ReadOnlySpan<byte>(sideData->data, (int)sideData->size).ToArray() : [];
                var source = new TileSource([tile], width, height, rotation, mirror,
                    profile, (int)original->color_primaries, (int)original->color_trc,
                    properties?.Get((uint)format->streams[index]->id)?.PixelAspectRatio is > 0 and var aspect ? aspect :
                        densityAspect > 0 ? densityAspect :
                        original->sample_aspect_ratio.num > 0 && original->sample_aspect_ratio.den > 0
                            ? Utils.RationalToDouble(original->sample_aspect_ratio) : 1);
                retained = true;
                return source;
            }
            return null;
        }
        finally
        {
            if (!retained)
                tile.Dispose();
        }
    }

    public sealed class TileSource : IPhotoTileSource
    {
        private readonly EncodedTile[] tiles;
        private StillImageHardwareDecoder hardware;
        private bool initialized;
        private bool disposed;

        internal TileSource(EncodedTile[] tiles, int width, int height, int rotation, bool mirror,
            byte[] profile, int primaries, int transfer, double pixelAspectRatio)
        {
            this.tiles = tiles;
            Width = width;
            Height = height;
            Rotation = rotation;
            MirrorHorizontally = mirror;
            PixelAspectRatio = pixelAspectRatio;
            IccProfile = profile;
            ColorPrimaries = primaries;
            ColorTransfer = transfer;
            AVCodec* codec = avcodec_find_decoder(((AVCodecParameters*)tiles[0].Parameters)->codec_id);
            PhotoTileBounds bounds = tiles[0].Bounds;
            HasScaledOverview = tiles.Length == 1 && codec != null && codec->max_lowres > 0 &&
                bounds.X == 0 && bounds.Y == 0 && bounds.Width == width && bounds.Height == height;
            foreach (EncodedTile tile in tiles)
                EncodedBytes += ((AVPacket*)tile.Packet)->size + ((AVCodecParameters*)tile.Parameters)->extradata_size;
        }

        public int Width { get; }
        public int Height { get; }
        public int Rotation { get; }
        public bool MirrorHorizontally { get; }
        public double PixelAspectRatio { get; }
        public byte[] IccProfile { get; }
        public int ColorPrimaries { get; }
        public int ColorTransfer { get; }
        public int Count => tiles.Length;
        public long EncodedBytes { get; }
        public bool HasScaledOverview { get; }
        public PhotoTileBounds GetBounds(int index) => index == -1 && HasScaledOverview
            ? new PhotoTileBounds(0, 0, Width, Height) : tiles[index].Bounds;

        public IPhotoTileSource CreateDecoder() => tileDecoderFactory?.Invoke(this) ?? this;

        public CodecParameters GetCodecParameters(int index)
        {
            lock (tiles)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return CodecParameters.FromNative((AVCodecParameters*)tiles[index].Parameters,
                    new AVRational { num = 1, den = 1 }, default);
            }
        }

        public byte[] GetEncodedPacket(int index)
        {
            lock (tiles)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var packet = (AVPacket*)tiles[index].Packet;
                return new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
            }
        }

        public int GetEncodedPacketSize(int index)
        {
            lock (tiles)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return ((AVPacket*)tiles[index].Packet)->size;
            }
        }

        public void CopyEncodedPacket(int index, Span<byte> destination)
        {
            lock (tiles)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var packet = (AVPacket*)tiles[index].Packet;
                new ReadOnlySpan<byte>(packet->data, packet->size).CopyTo(destination);
            }
        }

        public ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests,
            CancellationToken cancellationToken) =>
            new(Task.Run(() => DecodeTiles(requests, cancellationToken), cancellationToken));

        /// <summary>Decodes on the calling thread, for hosts that cannot hand work to a thread pool.</summary>
        public VideoFrame[] DecodeBatch(ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken) =>
            DecodeTiles(requests, cancellationToken);

        private VideoFrame[] DecodeTiles(ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken)
        {
            lock (tiles)
            {
                InitializeHardware(cancellationToken);
                var frames = new VideoFrame[requests.Length];
                int hardwareFailed = 0;
                try
                {
                    int workers = PhotoDecodeResources.PreferThroughput && !OperatingSystem.IsBrowser()
                        ? Math.Min(requests.Length, Math.Min(Environment.ProcessorCount, 8)) : Math.Min(requests.Length, 1);
                    if (workers == 0)
                        return frames;
                    var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
                    if (workers == 1)
                        DecodeWorker(0);
                    else
                        Parallel.For(0, workers, options, DecodeWorker);
                    cancellationToken.ThrowIfCancellationRequested();
                    return frames;

                    void DecodeWorker(int worker)
                    {
                        StillImageHardwareDecoder decoder = hardware;
                        for (int index = worker; index < requests.Length; index += workers)
                        {
                            PhotoTileRequest request = requests.Span[index];
                            frames[index] = DecodeTile(request.Index == -1 && HasScaledOverview ? 0 : request.Index,
                                request.Reduction, worker, HasScaledOverview && request.Reduction > 1 ? null : decoder,
                                cancellationToken, out bool failed);
                            if (failed)
                            {
                                decoder = null;
                                Interlocked.Exchange(ref hardwareFailed, 1);
                            }
                        }
                    }
                }
                catch
                {
                    foreach (VideoFrame frame in frames)
                        frame?.Release();
                    throw;
                }
                finally
                {
                    if (hardwareFailed != 0)
                        ReleaseHardware();
                }
            }
        }

        private void InitializeHardware(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialized)
            {
                initialized = true;
                hardware = (long)Width * Height >= MinimumHardwarePixels
                    ? StillImageHardwareDecoder.TryCreate((AVCodecParameters*)tiles[0].Parameters) : null;
                AppLog.Information("FFmpeg.Photo", $"Photo tile decoder {Width}x{Height}: " +
                    $"backend={(hardware is null ? "software" : hardware.PixelFormat)}, " +
                    $"tiles={Count}, encodedBytes={EncodedBytes}.");
            }
        }

        private VideoFrame DecodeTile(int index, int reduction, int worker, StillImageHardwareDecoder decoder,
            CancellationToken cancellationToken, out bool hardwareFailed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hardwareFailed = false;
            EncodedTile tile = tiles[index];
            var parameters = (AVCodecParameters*)tile.Parameters;
            AVFrame* decoded = av_frame_alloc();
            if (decoded == null)
                throw new OutOfMemoryException();
            try
            {
                AVStream item = default;
                item.codecpar = parameters;
                item.time_base = new AVRational { num = 1, den = 1 };
                bool success = TryDecodeTile(&item, (AVPacket*)tile.Packet, decoded, decoder, worker, preserveHardware: true, reduction);
                if (!success && decoder != null)
                {
                    hardwareFailed = true;
                    av_frame_unref(decoded);
                    success = TryDecodeTile(&item, (AVPacket*)tile.Packet, decoded, null, 0, reduction: reduction);
                }
                if (!success)
                    throw new InvalidDataException($"Could not decode photo tile {index}.");
                cancellationToken.ThrowIfCancellationRequested();
                VideoFrame frame = RetainNative(decoded, parameters)
                    ?? throw new InvalidDataException("Unsupported photo tile layout.");
                try
                {
                    ApplyColor(frame, decoded, parameters);
                    frame.FieldOrder = VideoFieldOrder.Progressive;
                    return frame;
                }
                catch
                {
                    frame.Release();
                    throw;
                }
            }
            finally { av_frame_free(&decoded); }
        }

        public void Dispose()
        {
            lock (tiles)
            {
                if (disposed)
                    return;
                disposed = true;
                ReleaseHardware();
                foreach (EncodedTile tile in tiles)
                    tile.Dispose();
            }
        }

        private void ReleaseHardware()
        {
            hardware?.Dispose();
            hardware = null;
        }
    }

    internal sealed class EncodedTile : IDisposable
    {
        public IntPtr Packet;
        public IntPtr Parameters;
        public PhotoTileBounds Bounds;

        public void Dispose()
        {
            AVPacket* packet = (AVPacket*)Packet;
            AVCodecParameters* parameters = (AVCodecParameters*)Parameters;
            Packet = Parameters = IntPtr.Zero;
            if (packet != null)
                av_packet_free(&packet);
            if (parameters != null)
                avcodec_parameters_free(&parameters);
        }
    }
}