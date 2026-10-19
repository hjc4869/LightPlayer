using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Interop;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>A still picture decoded for display, with the transform that brings it upright.</summary>
public sealed partial class StillImageThumbnail
{
    /// <summary>Tightly packed BGRA32, already turned upright.</summary>
    public byte[] Pixels { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

/// <summary>An upright BGRA thumbnail with explicitly owned native pixel storage.</summary>
public sealed class NativeImageThumbnail(int width, int height, NativeBuffer pixels) : IDisposable
{
    private NativeBuffer buffer = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public NativeBuffer Pixels => buffer ?? throw new ObjectDisposedException(nameof(NativeImageThumbnail));
    public NativeBuffer TakePixels() => Interlocked.Exchange(ref buffer, null)
        ?? throw new ObjectDisposedException(nameof(NativeImageThumbnail));
    public void Dispose() => Interlocked.Exchange(ref buffer, null)?.Dispose();
}

/// <summary>
/// Decodes a single still picture into the same <see cref="VideoFrame"/> a decoder produces.
/// </summary>
/// <remarks>
/// <para>
/// Stills go through the video renderer rather than a path of their own because an HDR photo is a
/// video frame in every respect that matters: an AVIF is an AV1 intra picture and a HEIC an HEVC
/// one. Pictures carrying HDR directly can arrive as 10-bit BT.2020 with a PQ or HLG curve. Handing them to the renderer as
/// they are means the tone mapping, gamut conversion, dithering and wide-gamut surface negotiation
/// already built for video apply to them unchanged.
/// </para>
/// </remarks>
public static unsafe partial class StillImage
{
    private const int IoBufferSize = 64 * 1024;
    private const int MinimumHardwarePixels = 1_000_000;

    /// <summary><c>SWS_BILINEAR</c>.</summary>
    private const int SwsBilinear = 2;

    /// <summary>SWS_CS_ITU709 and SWS_CS_BT2020, which swscale numbers as the colorspace tags do.</summary>
    private const int SwsColorspaceBt709 = 1;
    private const int SwsColorspaceBt2020 = 9;

    /// <summary><c>AV_FRAME_CROP_UNALIGNED</c>.</summary>
    private const int FrameCropUnaligned = 1;

    /// <summary>Whether the FFmpeg in use can decode this picture at all.</summary>
    /// <remarks>
    /// Which still codecs exist depends entirely on how the platform's FFmpeg was configured --
    /// JPEG XL needs <c>libjxl</c>, AVIF an AV1 decoder -- so this is asked rather than assumed.
    /// </remarks>
    public static bool IsDecoderAvailable(AVCodecID codec) => avcodec_find_decoder(codec) != null;

    public static (double Width, double Height) ReadDisplaySize(Stream stream) => ReadSize(stream, true, out _, out _, out _);

    /// <summary>Reads the primary HEIF/AVIF item's cropped pixel size, or zero when unavailable.</summary>
    public static Task<(int Width, int Height)> ReadContainerPixelSizeAsync(
        Stream stream, CancellationToken cancellationToken = default) =>
        IsoImageProperties.ReadPrimaryPixelSizeAsync(stream, cancellationToken);

    public static (int Width, int Height) ReadPixelSize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (TryReadHeaderPixelSize(stream, out int width, out int height))
            return (width, height);
        var size = ReadSize(stream, false, out _, out _, out _);
        return ((int)size.Width, (int)size.Height);
    }

    public static (int Width, int Height, double PixelAspectRatio, int Rotation, bool MirrorHorizontally) ReadGeometry(Stream stream)
    {
        var size = ReadSize(stream, false, out double pixelAspectRatio, out int rotation, out bool mirror);
        return ((int)size.Width, (int)size.Height, pixelAspectRatio, rotation, mirror);
    }

    public static double ReadPixelAspectRatio(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        double densityAspect = ReadDensityAspectRatio(stream);
        if (densityAspect > 0)
            return densityAspect;
        ReadSize(stream, false, out double pixelAspectRatio, out _, out _);
        return pixelAspectRatio;
    }

    private static double ReadDensityAspectRatio(Stream stream)
    {
        if (!stream.CanSeek)
            return 0;
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[16];
            int count = stream.ReadAtLeast(header, header.Length, false);
            if (count >= 13 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
                return header[12] == 0 ? 1 : (header[12] + 15) / 64d;
            if (count >= 8 && BinaryPrimitives.ReadUInt64BigEndian(header) == 0x89504e470d0a1a0a)
            {
                long offset = origin + 8;
                while (offset <= stream.Length - 12)
                {
                    stream.Position = offset;
                    if (stream.ReadAtLeast(header[..8], 8, false) != 8)
                        return 0;
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length > stream.Length - offset - 12)
                        return 0;
                    if (header.Slice(4, 4).SequenceEqual("pHYs"u8))
                    {
                        if (length != 9 || stream.ReadAtLeast(header[..9], 9, false) != 9 || header[8] > 1)
                            return 0;
                        uint horizontal = BinaryPrimitives.ReadUInt32BigEndian(header);
                        uint vertical = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                        return horizontal > 0 && vertical > 0 ? vertical / (double)horizontal : 0;
                    }
                    if (header.Slice(4, 4).SequenceEqual("IEND"u8))
                        return 0;
                    offset += length + 12L;
                }
            }
            else if (count >= 2 && header[0] == 0xff && header[1] == 0xd8)
            {
                long offset = origin + 2;
                while (offset <= stream.Length - 4)
                {
                    stream.Position = offset;
                    if (stream.ReadAtLeast(header[..4], 4, false) != 4 || header[0] != 0xff)
                        return 0;
                    byte marker = header[1];
                    if (marker == 0xff)
                    {
                        offset++;
                        continue;
                    }
                    if (marker is 0xd9 or 0xda)
                        return 0;
                    if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7)
                    {
                        offset += 2;
                        continue;
                    }
                    int length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
                    if (length < 2 || length > stream.Length - offset - 2)
                        return 0;
                    if (marker == 0xe0 && length >= 16 && stream.ReadAtLeast(header[..14], 14, false) == 14 &&
                        header[..5].SequenceEqual("JFIF\0"u8) && header[7] <= 2)
                    {
                        int horizontal = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
                        int vertical = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
                        return horizontal > 0 && vertical > 0 ? vertical / (double)horizontal : 0;
                    }
                    offset += length + 2;
                }
            }
            return 0;
        }
        catch (IOException exception)
        {
            AppLog.Warning("Photo.Metadata", "Could not read image pixel density.", exception);
            return 0;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static (double Width, double Height) ReadSize(Stream stream, bool display, out double pixelAspectRatio,
        out int rotation, out bool mirrorHorizontally)
    {
        pixelAspectRatio = 1;
        rotation = 0;
        mirrorHorizontally = false;
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            return default;

        long origin = stream.Position;
        int exifRotation = 0;
        bool exifMirror = false;
        bool hasExif = ExifOrientation.TryRead(stream, out exifRotation, out exifMirror);
        IsoImageProperties properties = IsoImageProperties.Read(stream);
        double densityAspect = ReadDensityAspectRatio(stream);
        hasExif &= properties == null;
        using var io = new FfmpegStreamIO(stream, IoBufferSize);
        AVFormatContext* format = avformat_alloc_context();
        AVFrame* frame = null;
        try
        {
            if (format == null)
                return default;
            format->pb = io.Context;
            if (avformat_open_input(&format, string.Empty, null, null) < 0 ||
                avformat_find_stream_info(format, null) < 0)
                return default;

            uint? itemId = FindPrimaryItem(format, properties);
            AVStreamGroup* mosaic = FindTileGrid(format, itemId);
            if (mosaic != null)
                ApplyGridCrop(mosaic, properties?.Get((uint)mosaic->id));
            int index = mosaic == null
                ? itemId is { } selected ? FindItemStream(format, selected)
                    : av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0)
                : mosaic->streams[0]->index;
            if (index < 0)
                return default;

            AVCodecParameters* parameters = format->streams[index]->codecpar;
            double width = mosaic == null ? parameters->width : mosaic->@params.tile_grid->width;
            double height = mosaic == null ? parameters->height : mosaic->@params.tile_grid->height;
            if (width <= 0 || height <= 0)
                return default;
            if (mosaic == null && TryReadCrop(parameters, parameters->width, parameters->height, out var crop))
            {
                width -= crop.Left + crop.Right;
                height -= crop.Top + crop.Bottom;
            }
            double itemAspect = properties?.Get(mosaic == null ? (uint)format->streams[index]->id : (uint)mosaic->id)?.PixelAspectRatio ?? 0;
            pixelAspectRatio = itemAspect > 0 ? itemAspect : densityAspect > 0 ? densityAspect :
                parameters->sample_aspect_ratio.num > 0 && parameters->sample_aspect_ratio.den > 0
                    ? Utils.RationalToDouble(parameters->sample_aspect_ratio) : 1;
            frame = av_frame_alloc();
            if (frame == null)
                return default;
            if (mosaic != null)
                ApplyTileGridOrientation(mosaic, frame);
            ReadOrientation(frame, parameters, hasExif, exifRotation, exifMirror, out rotation, out mirrorHorizontally);
            if (!display)
                return (width, height);
            width *= pixelAspectRatio;
            return rotation is 90 or 270 ? (height, width) : (width, height);
        }
        finally
        {
            if (frame != null)
                av_frame_free(&frame);
            if (format != null)
                avformat_close_input(&format);
            stream.Position = origin;
        }
    }

    /// <summary>
    /// Decodes the first picture of <paramref name="stream"/>, or null when FFmpeg cannot decode it.
    /// LibRaw and ICC processing report malformed or unsupported data as an exception.
    /// </summary>
    public static VideoFrame Decode(Stream stream, bool leaveOpen = true) =>
        Decode(stream, leaveOpen, CancellationToken.None);

    /// <summary>Decodes a still picture, with cancellation during RAW and ICC processing.</summary>
    public static VideoFrame Decode(Stream stream, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (RawImage.IsDng(stream))
        {
            try
            {
                return RawImage.Open(stream, true, frame =>
                {
                    AVCodecParameters parameters = default;
                    VideoFrame target = Convert(frame, &parameters);
                    if (target != null)
                        ApplyColor(target, frame, &parameters);
                    return target;
                }, cancellationToken);
            }
            finally
            {
                if (!leaveOpen)
                    stream.Dispose();
            }
        }

        // Read before libavformat takes the stream over, because the wrapper leaves the position
        // wherever demuxing ended up and this has to start from the beginning.
        bool hasExif = ExifOrientation.TryRead(stream, out int exifRotation, out bool exifMirror);

        bool associatedAlpha = IsAssociatedTiff(stream);
        return Open(stream, leaveOpen, 0, (parameters, frame) =>
        {
            using IccImage colorManaged = IccImage.TryCreate(frame, parameters, true, cancellationToken,
                associatedAlpha: associatedAlpha);
            AVFrame* pixels = colorManaged == null ? frame : colorManaged.Frame;
            VideoFrame target = Convert(pixels, parameters, associatedAlpha: colorManaged is null && associatedAlpha);
            if (target is null)
                return null;

            ApplyColor(target, pixels, parameters);
            ApplyOrientation(target, frame, parameters, hasExif, exifRotation, exifMirror);
            return target;
        });
    }

    public static StillImagePixels DecodePixels(Stream stream, uint? itemId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        bool hasExif = ExifOrientation.TryRead(stream, out int exifRotation, out bool exifMirror);
        return Open(stream, true, 0, (parameters, frame) =>
        {
            AVFrame* rgb = IccImage.DecodeRgb(frame, parameters);
            try
            {
                ushort[] pixels = new ushort[checked(rgb->width * rgb->height * 4)];
                for (int row = 0; row < rgb->height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var green = (ushort*)(rgb->data[0] + (long)row * rgb->linesize[0]);
                    var blue = (ushort*)(rgb->data[1] + (long)row * rgb->linesize[1]);
                    var red = (ushort*)(rgb->data[2] + (long)row * rgb->linesize[2]);
                    var alpha = (ushort*)(rgb->data[3] + (long)row * rgb->linesize[3]);
                    int start = row * rgb->width * 4;
                    for (int column = 0; column < rgb->width; column++)
                    {
                        pixels[start + column * 4] = red[column];
                        pixels[start + column * 4 + 1] = green[column];
                        pixels[start + column * 4 + 2] = blue[column];
                        pixels[start + column * 4 + 3] = alpha[column];
                    }
                }

                AVFrameSideData* profile = av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
                byte[] icc = profile != null && profile->data != null && profile->size is > 0 and <= 4 * 1024 * 1024
                    ? new ReadOnlySpan<byte>(profile->data, (int)profile->size).ToArray() : [];
                ReadOrientation(frame, parameters, hasExif, exifRotation, exifMirror, out int rotation, out bool mirror);
                return new StillImagePixels
                {
                    Width = rgb->width,
                    Height = rgb->height,
                    Pixels = pixels,
                    IccProfile = icc,
                    ColorPrimaries = (int)(frame->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                        ? parameters->color_primaries : frame->color_primaries),
                    ColorTransfer = (int)ReadTransfer(frame, parameters),
                    Rotation = rotation,
                    MirrorHorizontally = mirror,
                    PixelAspectRatio = Utils.RationalToDouble(ReadSampleAspectRatio(frame, parameters)),
                };
            }
            finally
            {
                av_frame_free(&rgb);
            }
        }, itemId: itemId);
    }

    /// <summary>
    /// Decodes a picture scaled to fit <paramref name="maxDimension"/>, upright and ready to show
    /// as a library tile, with HDR mapped into 200-nit sRGB at 1x headroom.
    /// </summary>
    public static StillImageThumbnail DecodeThumbnail(Stream stream, int maxDimension, bool leaveOpen = true) =>
        DecodeThumbnail(stream, maxDimension, leaveOpen, CancellationToken.None);

    /// <summary>Decodes an upright thumbnail, with cancellation during RAW and ICC processing.</summary>
    public static StillImageThumbnail DecodeThumbnail(
        Stream stream, int maxDimension, bool leaveOpen, CancellationToken cancellationToken)
        => DecodeThumbnail(stream, maxDimension, leaveOpen, cancellationToken, 0);

    public static StillImageThumbnail DecodeThumbnail(
        Stream stream, int maxDimension, bool leaveOpen, CancellationToken cancellationToken, int fallbackOrientation)
    {
        using NativeImageThumbnail thumbnail = DecodeNativeThumbnail(stream, maxDimension, leaveOpen, cancellationToken, fallbackOrientation);
        return thumbnail is null ? null : new StillImageThumbnail
        {
            Pixels = thumbnail.Pixels.GetSpan().ToArray(), Width = thumbnail.Width, Height = thumbnail.Height,
        };
    }

    /// <summary>Decodes an upright thumbnail whose pixel storage belongs to the caller.</summary>
    public static NativeImageThumbnail DecodeNativeThumbnail(Stream stream, int maxDimension, bool leaveOpen = true,
        CancellationToken cancellationToken = default, int fallbackOrientation = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDimension, 0);

        if (RawImage.IsDng(stream))
        {
            try
            {
                return RawImage.Open(stream, false, frame =>
                {
                    ComputeSize(frame->width, frame->height, new AVRational { num = 1, den = 1 },
                        maxDimension, out int width, out int height);
                    NativeBuffer pixels = ToBgraNative(frame, width, height);
                    return pixels == null ? null : new NativeImageThumbnail(width, height, pixels);
                }, cancellationToken);
            }
            finally
            {
                if (!leaveOpen)
                    stream.Dispose();
            }
        }

        bool hasExif = ExifOrientation.TryRead(stream, out int exifRotation, out bool exifMirror);
        if ((!hasExif || exifRotation == 0 && !exifMirror) && fallbackOrientation is >= 2 and <= 8)
            hasExif = ExifOrientation.TryDecode(fallbackOrientation, out exifRotation, out exifMirror);

        return Open(stream, leaveOpen, maxDimension <= 1024 ? Math.Min(maxDimension, 256) : maxDimension, (parameters, frame) =>
        {
            ReadOrientation(frame, parameters, hasExif, exifRotation, exifMirror, out int rotation, out bool mirror);
            ComputeSize(
                frame->width,
                frame->height,
                ReadSampleAspectRatio(frame, parameters),
                maxDimension,
                out int width,
                out int height);

            bool softwareColor = NeedsSoftwareColor(frame, parameters);
            using IccImage colorManaged = IccImage.TryCreate(frame, parameters, softwareColor, cancellationToken,
                softwareColor ? 0 : width, softwareColor ? 0 : height);
            if (softwareColor)
            {
                VideoFrame retained = RetainSoftwareFrame(colorManaged == null ? frame : colorManaged.Frame, parameters);
                try
                {
                    NativeBuffer mapped = RenderSoftwarePixels(retained, width, height, cancellationToken);
                    return Orient(mapped, width, height, rotation, mirror);
                }
                finally { retained.Release(); }
            }
            NativeBuffer pixels = ToBgraNative(colorManaged == null ? frame : colorManaged.Frame, width, height);
            return pixels is null
                ? null
                : Orient(pixels, width, height, rotation, mirror);
            }, decoderThreadCount: 1, maxDecodeDimension: maxDimension);
    }

    private delegate T Decoded<out T>(AVCodecParameters* parameters, AVFrame* frame)
        where T : class;

    /// <summary>
    /// Opens the stream, decodes its picture, and hands it to <paramref name="use"/> while
    /// everything is still alive. Null whenever any step fails.
    /// </summary>
    /// <param name="smallestUsefulDimension">
    /// Zero to insist on the picture itself. Anything else permits a smaller copy the file already
    /// carries, so long as it is at least that many pixels across, which only matters for a mosaic:
    /// assembling one costs every tile, and a caller that wants a tile-sized picture anyway would
    /// pay it for nothing.
    /// </param>
    private static T Open<T>(
        Stream stream,
        bool leaveOpen,
        int smallestUsefulDimension,
        Decoded<T> use,
        int decoderThreadCount = 0,
        uint? itemId = null,
        bool preserveHardware = false,
        int maxDecodeDimension = 0,
        bool includeAlpha = true,
        AVCodecID codecHint = AVCodecID.AV_CODEC_ID_NONE)
        where T : class
    {
        long origin = stream.CanSeek ? stream.Position : 0;
        try
        {
            T result = OpenCore(stream, smallestUsefulDimension, use, decoderThreadCount, itemId,
                stream.CanSeek, preserveHardware, maxDecodeDimension, includeAlpha, codecHint, out bool hardwareAttempted);
            if (result != null || !hardwareAttempted)
                return result;

            AppLog.Warning("FFmpeg.Photo", "Hardware photo decoding failed; retrying with software.");
            stream.Position = origin;
            return OpenCore(stream, smallestUsefulDimension, use, decoderThreadCount, itemId, false, preserveHardware, maxDecodeDimension, includeAlpha, codecHint, out _);
        }
        finally
        {
            if (!leaveOpen)
                stream.Dispose();
        }
    }

    private static T OpenCore<T>(
        Stream stream,
        int smallestUsefulDimension,
        Decoded<T> use,
        int decoderThreadCount,
        uint? itemId,
        bool allowHardware,
        bool preserveHardware,
        int maxDecodeDimension,
        bool includeAlpha,
        AVCodecID codecHint,
        out bool hardwareAttempted)
        where T : class
    {
        hardwareAttempted = false;
        long origin = stream.CanSeek ? stream.Position : 0;
        IsoImageProperties properties = IsoImageProperties.Read(stream);
        double densityAspect = ReadDensityAspectRatio(stream);
        var io = new FfmpegStreamIO(stream, IoBufferSize);
        AVFormatContext* format = avformat_alloc_context();
        if (format == null)
        {
            io.Dispose();
            return null;
        }

        format->pb = io.Context;
        AVCodecContext* context = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        StillImageHardwareDecoder hardware = null;
        try
        {
            format->video_codec_id = codecHint;
            AVInputFormat* inputFormat = codecHint == AVCodecID.AV_CODEC_ID_NONE ? null : av_find_input_format("image2pipe");
            if (avformat_open_input(&format, string.Empty, inputFormat, null) < 0)
            {
                // avformat_open_input frees and nulls the context on failure.
                format = null;
                return null;
            }

            if (avformat_find_stream_info(format, null) < 0)
                return null;

            itemId ??= FindPrimaryItem(format, properties);
            uint? alphaItem = includeAlpha && itemId is { } primaryId ? properties?.FindAlpha(primaryId) : null;
            AVStreamGroup* mosaic = FindTileGrid(format, itemId);
            bool croppedGrid = mosaic != null && ApplyGridCrop(mosaic, properties?.Get((uint)mosaic->id));
            int videoIndex = mosaic == null
                ? itemId is { } selected ? FindItemStream(format, selected)
                    : av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0)
                : croppedGrid ? -1 : FindWholePicture(format, mosaic, smallestUsefulDimension);
            if (mosaic == null && videoIndex < 0)
                return null;

            packet = av_packet_alloc();
            frame = av_frame_alloc();
            if (packet == null || frame == null)
                return null;

            AVStream* video = videoIndex >= 0 ? format->streams[videoIndex] : mosaic->streams[0];
            long pixelCount = videoIndex >= 0 ? (long)video->codecpar->width * video->codecpar->height
                : (long)mosaic->@params.tile_grid->width * mosaic->@params.tile_grid->height;
            hardware = allowHardware && alphaItem is null && pixelCount >= MinimumHardwarePixels
                ? StillImageHardwareDecoder.TryCreate(video->codecpar) : null;
            hardwareAttempted = hardware != null;
            if (videoIndex >= 0)
            {
                int reduction = 1;
                if (maxDecodeDimension > 0 && video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MJPEG)
                {
                    double aspect = densityAspect > 0 ? densityAspect :
                        video->codecpar->sample_aspect_ratio.num > 0 && video->codecpar->sample_aspect_ratio.den > 0
                            ? Utils.RationalToDouble(video->codecpar->sample_aspect_ratio) : 1;
                    double longestEdge = Math.Max(video->codecpar->width * aspect, video->codecpar->height);
                    reduction = 1 << Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, longestEdge / maxDecodeDimension))), 0, 3);
                }
                context = OpenDecoder(video, decoderThreadCount, hardware, reduction: reduction);
                if (context == null)
                    return null;

                for (int i = 0; i < format->nb_streams; i++)
                    format->streams[i]->discard = i == videoIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;

                if (!TryDecodeFirst(format, context, videoIndex, packet, frame, hardware) ||
                    (hardware != null && !(preserveHardware ? hardware.Preserve(frame) : hardware.Download(frame))))
                    return null;

                if (TryReadCrop(video->codecpar, frame->width, frame->height, out var crop))
                {
                    if (frame->hw_frames_ctx != null && (hardware == null || !hardware.Download(frame)))
                        return null;
                    frame->crop_top = crop.Top;
                    frame->crop_bottom = crop.Bottom;
                    frame->crop_left = crop.Left;
                    frame->crop_right = crop.Right;
                    if (av_frame_apply_cropping(frame, FrameCropUnaligned) < 0)
                        return null;
                }
            }
            else
            {
                if (!TryAssembleTileGrid(format, mosaic, packet, frame, decoderThreadCount, hardware))
                    return null;
            }

            IsoImageProperties.Item itemProperties = properties?.Get(videoIndex >= 0 ? (uint)video->id : (uint)mosaic->id);
            double pixelAspectRatio = itemProperties?.PixelAspectRatio is > 0 ? itemProperties.PixelAspectRatio : densityAspect;
            if (pixelAspectRatio > 0)
                frame->sample_aspect_ratio = av_d2q(pixelAspectRatio, int.MaxValue);
            ApplyItemOrientation(frame, video->codecpar, itemProperties);
            if (video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MJPEG && frame->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED)
                frame->color_range = video->codecpar->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
                    ? AVColorRange.AVCOL_RANGE_JPEG : video->codecpar->color_range;
            if (hardware?.UsedHardware == true)
                AppLog.Information("FFmpeg.Photo", $"Decoded {frame->width}x{frame->height} {video->codecpar->codec_id} photo with {hardware.PixelFormat}" +
                    (frame->hw_frames_ctx != null ? " surface delivery." : " readback."));

            if (alphaItem is { } alphaId)
            {
                stream.Position = origin;
                AVFrame* colorFrame = frame;
                return Open(stream, true, 0, (alphaParameters, alphaFrame) =>
                {
                    AVFrame* combined = CombinePhotoAlpha(colorFrame, video->codecpar, alphaFrame, alphaParameters,
                        properties.IsPremultiplied(itemId.Value, alphaId));
                    try { return use(video->codecpar, combined); }
                    finally { av_frame_free(&combined); }
                }, decoderThreadCount, itemId: alphaId, includeAlpha: false)
                    ?? throw new InvalidDataException("The photo alpha image could not be decoded.");
            }
            return frame->width <= 0 || frame->height <= 0 || frame->format < 0
                ? null
                : use(video->codecpar, frame);
        }
        catch (FfmpegException exception)
        {
            AppLog.Error("FFmpeg.Photo", "Could not decode photo pixels.", exception);
            return null;
        }
        finally
        {
            if (frame != null)
                av_frame_free(&frame);
            if (packet != null)
                av_packet_free(&packet);
            if (context != null)
                avcodec_free_context(&context);
            hardware?.Dispose();
            if (format != null)
                avformat_close_input(&format);
            io.Dispose();
        }
    }

    private static AVCodecContext* OpenDecoder(AVStream* stream, int threadCount, StillImageHardwareDecoder hardware,
        int retainedSurfaces = 0, int reduction = 1)
    {
        AVCodec* codec = hardware == null ? avcodec_find_decoder(stream->codecpar->codec_id)
            : hardware.FindDecoder(stream->codecpar);
        if (codec == null)
            return null;

        AVCodecContext* context = avcodec_alloc_context3(codec);
        if (context == null)
            return null;

        if (avcodec_parameters_to_context(context, stream->codecpar) < 0)
        {
            avcodec_free_context(&context);
            return null;
        }

        context->thread_count = OperatingSystem.IsBrowser() ? 1 : threadCount;
        context->lowres = hardware is null ? Math.Min(codec->max_lowres, (int)Math.Log2(Math.Max(1, reduction))) : 0;
        context->extra_hw_frames = retainedSurfaces;
        context->pkt_timebase = stream->time_base;
        hardware?.Configure(context);
        if (avcodec_open2(context, codec, null) < 0)
        {
            avcodec_free_context(&context);
            return null;
        }

        return context;
    }

    /// <summary>Reads packets until the decoder produces its first picture.</summary>
    private static bool TryDecodeFirst(
        AVFormatContext* format,
        AVCodecContext* context,
        int videoIndex,
        AVPacket* packet,
        AVFrame* frame,
        StillImageHardwareDecoder hardware)
    {
        while (av_read_frame(format, packet) >= 0)
        {
            try
            {
                if (packet->stream_index != videoIndex)
                    continue;

                if (avcodec_send_packet(context, packet) < 0)
                    continue;

                if (avcodec_receive_frame(context, frame) == 0)
                    return true;
            }
            finally
            {
                av_packet_unref(packet);
            }
        }

        // A still that fits in one packet is often only produced once the decoder is told no more
        // are coming.
        return avcodec_send_packet(context, null) >= 0 &&
               ReceiveDrainedFrame(context, frame, hardware);
    }

    private static bool ReceiveDrainedFrame(AVCodecContext* context, AVFrame* frame, StillImageHardwareDecoder hardware)
    {
        int result = avcodec_receive_frame(context, frame);
        if (result < 0 && hardware != null)
            AppLog.Warning("FFmpeg.Photo", $"Could not receive a hardware photo frame: {FfmpegError.Describe(result)}; using software.");
        return result == 0;
    }

    /// <summary>
    /// The mosaic a HEIF may store its picture as, or null when the file holds a plain image.
    /// </summary>
    /// <remarks>
    /// Phones that shoot HEIC routinely cut the picture into a grid of small, independently coded
    /// tiles. FFmpeg presents each tile as its own stream and the layout as a stream group, so
    /// anything that simply takes the best video stream gets one tile -- a corner of the photo at a
    /// few hundred pixels square -- and never sees the picture.
    /// </remarks>
    private static AVStreamGroup* FindTileGrid(AVFormatContext* format, uint? itemId)
    {
        for (uint index = 0; index < format->nb_stream_groups; index++)
        {
            AVStreamGroup* group = format->stream_groups[index];
            if (group != null &&
                (itemId is null || group->id == itemId.Value) &&
                group->type == AVStreamGroupParamsType.AV_STREAM_GROUP_PARAMS_TILE_GRID &&
                group->@params.tile_grid != null &&
                group->nb_streams > 0)
            {
                return group;
            }
        }

        return null;
    }

    private static uint? FindPrimaryItem(AVFormatContext* format, IsoImageProperties properties) =>
        properties?.PrimaryItemId is { } itemId &&
        (FindTileGrid(format, itemId) != null || FindItemStream(format, itemId) >= 0) ? itemId : null;

    private static int FindItemStream(AVFormatContext* format, uint itemId)
    {
        for (uint index = 0; index < format->nb_streams; index++)
        {
            AVStream* stream = format->streams[index];
            if (stream->id == itemId && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return stream->index;
        }

        return -1;
    }

    /// <summary>
    /// The smallest stream that is a picture in its own right rather than a tile of
    /// <paramref name="mosaic"/>, and is still at least <paramref name="minimumDimension"/> across.
    /// -1 when the caller wants the full picture or the file offers nothing that would do.
    /// </summary>
    private static int FindWholePicture(AVFormatContext* format, AVStreamGroup* mosaic, int minimumDimension)
    {
        if (minimumDimension <= 0)
            return -1;

        int best = -1;
        long bestPixels = long.MaxValue;
        for (int index = 0; index < format->nb_streams; index++)
        {
            AVStream* stream = format->streams[index];
            AVCodecParameters* parameters = stream->codecpar;
            if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO ||
                (stream->disposition & AV_DISPOSITION_DEPENDENT) != 0 ||
                Math.Max(parameters->width, parameters->height) < minimumDimension ||
                IsTile(mosaic, index))
            {
                continue;
            }

            long pixels = (long)parameters->width * parameters->height;
            if (pixels < bestPixels)
            {
                bestPixels = pixels;
                best = index;
            }
        }

        return best;
    }

    private static bool IsTile(AVStreamGroup* mosaic, int streamIndex)
    {
        for (uint index = 0; index < mosaic->nb_streams; index++)
        {
            if (mosaic->streams[index]->index == streamIndex)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Decodes every tile of <paramref name="mosaic"/> into <paramref name="canvas"/>, laid out and
    /// cropped as the grid says. False unless the whole picture came together.
    /// </summary>
    private static bool TryAssembleTileGrid(
        AVFormatContext* format,
        AVStreamGroup* mosaic,
        AVPacket* packet,
        AVFrame* canvas,
        int decoderThreadCount,
        StillImageHardwareDecoder hardware)
    {
        AVStreamGroupTileGrid* grid = mosaic->@params.tile_grid;
        if (grid->nb_tiles == 0 ||
            grid->offsets == null ||
            grid->coded_width <= 0 ||
            grid->coded_height <= 0 ||
            grid->width <= 0 ||
            grid->height <= 0)
        {
            return false;
        }

        int streamCount = (int)format->nb_streams;
        var tileOfStream = new int[streamCount];
        Array.Fill(tileOfStream, -1);
        for (uint tile = 0; tile < grid->nb_tiles; tile++)
        {
            uint member = grid->offsets[tile].idx;
            if (member >= mosaic->nb_streams)
                return false;

            int streamIndex = mosaic->streams[member]->index;
            if (streamIndex < 0 || streamIndex >= streamCount || tileOfStream[streamIndex] >= 0)
                return false;

            tileOfStream[streamIndex] = (int)tile;
        }

        for (int index = 0; index < streamCount; index++)
        {
            format->streams[index]->discard = tileOfStream[index] >= 0
                ? AVDiscard.AVDISCARD_DEFAULT
                : AVDiscard.AVDISCARD_ALL;
        }

        int workers = OperatingSystem.IsBrowser() || decoderThreadCount == 1
            ? 1 : Math.Min(Environment.ProcessorCount, StillImageHardwareDecoder.MaximumTileWorkers);
        if (workers > 1 && grid->nb_tiles > 1)
            return TryAssembleTileGridParallel(format, mosaic, packet, canvas, tileOfStream, workers, hardware);

        AVFrame* tileFrame = av_frame_alloc();
        if (tileFrame == null)
            return false;

        try
        {
            uint placed = 0;
            while (placed < grid->nb_tiles && av_read_frame(format, packet) >= 0)
            {
                try
                {
                    int streamIndex = packet->stream_index;
                    int tile = streamIndex >= 0 && streamIndex < streamCount ? tileOfStream[streamIndex] : -1;
                    if (tile < 0)
                        continue;

                    if (!TryDecodeTile(format->streams[streamIndex], packet, tileFrame, hardware, 0))
                        return false;

                    bool laid = (canvas->data[0] != null || TryAllocateCanvas(canvas, tileFrame, grid)) &&
                                TryPlaceTile(
                                    canvas,
                                    tileFrame,
                                    grid->offsets[(uint)tile].horizontal,
                                    grid->offsets[(uint)tile].vertical);
                    av_frame_unref(tileFrame);
                    if (!laid)
                        return false;

                    // A stream contributes one tile; forgetting it keeps a repeat from being laid twice.
                    tileOfStream[streamIndex] = -1;
                    placed++;
                }
                finally
                {
                    av_packet_unref(packet);
                }
            }

            if (placed != grid->nb_tiles || !TryCropCanvas(canvas, grid))
                return false;

            ApplyTileGridOrientation(mosaic, canvas);
            return true;
        }
        finally
        {
            av_frame_free(&tileFrame);
        }
    }

    private static bool TryAssembleTileGridParallel(
        AVFormatContext* format,
        AVStreamGroup* mosaic,
        AVPacket* packet,
        AVFrame* canvas,
        int[] tileOfStream,
        int workers,
        StillImageHardwareDecoder hardware)
    {
        AVStreamGroupTileGrid* grid = mosaic->@params.tile_grid;
        var packets = new IntPtr[workers];
        var frames = new IntPtr[workers];
        var tiles = new int[workers];
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
        try
        {
            uint placed = 0;
            while (placed < grid->nb_tiles)
            {
                int count = 0;
                while (count < workers && av_read_frame(format, packet) >= 0)
                {
                    try
                    {
                        int streamIndex = packet->stream_index;
                        int tile = streamIndex >= 0 && streamIndex < tileOfStream.Length ? tileOfStream[streamIndex] : -1;
                        if (tile < 0)
                            continue;

                        AVPacket* retained = av_packet_alloc();
                        packets[count] = (IntPtr)retained;
                        if (retained == null || av_packet_ref(retained, packet) < 0)
                            return false;
                        tiles[count++] = tile;
                        tileOfStream[streamIndex] = -1;
                    }
                    finally
                    {
                        av_packet_unref(packet);
                    }
                }

                if (count == 0)
                    return false;

                Parallel.For(0, count, options, work =>
                {
                    AVFrame* decoded = av_frame_alloc();
                    frames[work] = (IntPtr)decoded;
                    if (decoded == null)
                        return;
                    AVPacket* retained = (AVPacket*)packets[work];
                    if (!TryDecodeTile(format->streams[retained->stream_index], retained, decoded, hardware, work))
                    {
                        av_frame_free(&decoded);
                        frames[work] = IntPtr.Zero;
                    }
                });

                for (int work = 0; work < count; work++)
                {
                    AVFrame* decoded = (AVFrame*)frames[work];
                    if (decoded == null ||
                        !(canvas->data[0] != null || TryAllocateCanvas(canvas, decoded, grid)) ||
                        !TryPlaceTile(canvas, decoded, grid->offsets[(uint)tiles[work]].horizontal,
                            grid->offsets[(uint)tiles[work]].vertical))
                    {
                        return false;
                    }

                    av_frame_free(&decoded);
                    frames[work] = IntPtr.Zero;
                    AVPacket* retained = (AVPacket*)packets[work];
                    av_packet_free(&retained);
                    packets[work] = IntPtr.Zero;
                    placed++;
                }
            }

            if (!TryCropCanvas(canvas, grid))
                return false;
            ApplyTileGridOrientation(mosaic, canvas);
            return true;
        }
        finally
        {
            for (int work = 0; work < workers; work++)
            {
                AVFrame* decoded = (AVFrame*)frames[work];
                if (decoded != null)
                    av_frame_free(&decoded);
                AVPacket* retained = (AVPacket*)packets[work];
                if (retained != null)
                    av_packet_free(&retained);
            }
        }
    }

    /// <summary>Applies the grid item's display matrix to the assembled picture.</summary>
    private static void ApplyTileGridOrientation(AVStreamGroup* mosaic, AVFrame* canvas)
    {
        AVStreamGroupTileGrid* grid = mosaic->@params.tile_grid;
        const int matrixBytes = 9 * sizeof(int);
        AVPacketSideData* sideData = grid->coded_side_data != null && grid->nb_coded_side_data > 0
            ? av_packet_side_data_get(grid->coded_side_data, grid->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX)
            : null;
        if (sideData == null || sideData->data == null || sideData->size < matrixBytes)
            return;

        av_frame_remove_side_data(canvas, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        AVFrameSideData* target = av_frame_new_side_data(canvas,
            AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX, matrixBytes);
        if (target != null)
            Buffer.MemoryCopy(sideData->data, target->data, matrixBytes, matrixBytes);
    }

    private static void ApplyItemOrientation(AVFrame* frame, AVCodecParameters* parameters, IsoImageProperties.Item item)
    {
        if (item == null || VideoDisplayMatrix.TryRead(frame, out _, out _))
            return;
        const int matrixBytes = 9 * sizeof(int);
        AVPacketSideData* sideData = parameters->coded_side_data != null && parameters->nb_coded_side_data > 0
            ? av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX)
            : null;
        bool hasMatrix = sideData != null && sideData->data != null && sideData->size >= matrixBytes;
        av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        AVFrameSideData* target = av_frame_new_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX, matrixBytes);
        if (target == null)
            return;
        if (hasMatrix)
            Buffer.MemoryCopy(sideData->data, target->data, matrixBytes, matrixBytes);
        else
        {
            var matrix = new Span<int>(target->data, 9);
            matrix.Clear();
            matrix[0] = matrix[4] = 1 << 16;
            matrix[8] = 1 << 30;
        }
    }

    /// <summary>
    /// Decodes one independent tile, reusing a hardware decoder when its parameters match.
    /// </summary>
    private static bool TryDecodeTile(AVStream* stream, AVPacket* packet, AVFrame* frame, StillImageHardwareDecoder hardware, int worker,
        bool preserveHardware = false, int reduction = 1)
    {
        AVCodecContext* context = hardware == null ? null : hardware.GetTileDecoder(stream->codecpar, worker);
        if (context == null)
        {
            context = OpenDecoder(stream, threadCount: 1, hardware, retainedSurfaces: preserveHardware ? 16 : 0, reduction);
            if (context == null)
                return false;
            if (hardware != null && !hardware.CacheTileDecoder(context, stream->codecpar, worker))
            {
                avcodec_free_context(&context);
                return false;
            }
        }

        try
        {
            return avcodec_send_packet(context, packet) >= 0 &&
                   avcodec_send_packet(context, null) >= 0 &&
                                         ReceiveDrainedFrame(context, frame, hardware) &&
                     (hardware == null || (preserveHardware ? hardware.Preserve(frame) : hardware.Download(frame)));
        }
        finally
        {
            if (hardware == null)
                avcodec_free_context(&context);
        }
    }

    private static bool TryAllocateCanvas(AVFrame* canvas, AVFrame* tile, AVStreamGroupTileGrid* grid)
    {
        canvas->format = tile->format;
        canvas->width = grid->coded_width;
        canvas->height = grid->coded_height;
        if (av_frame_get_buffer(canvas, 0) < 0 || av_frame_copy_props(canvas, tile) < 0)
            return false;

        // The tiles are meant to cover the canvas exactly, but a grid that leaves a gap must show
        // black there rather than whatever the allocation happened to be handed.
        byte** planePointers = stackalloc byte*[4];
        nint* stridePointers = stackalloc nint[4];
        for (uint plane = 0; plane < 4; plane++)
        {
            planePointers[plane] = canvas->data[plane];
            stridePointers[plane] = canvas->linesize[plane];
        }

        av_image_fill_black(
            planePointers,
            stridePointers,
            (AVPixelFormat)canvas->format,
            canvas->color_range,
            canvas->width,
            canvas->height);
        return true;
    }

    /// <summary>Copies a decoded tile onto the canvas at the offset the grid gives it.</summary>
    private static bool TryPlaceTile(AVFrame* canvas, AVFrame* tile, int x, int y)
    {
        if (tile->format != canvas->format ||
            tile->width <= 0 ||
            tile->height <= 0 ||
            x < 0 ||
            y < 0 ||
            x + tile->width > canvas->width ||
            y + tile->height > canvas->height)
        {
            return false;
        }

        var pixelFormat = (AVPixelFormat)canvas->format;
        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(pixelFormat);
        int planeCount = av_pix_fmt_count_planes(pixelFormat);
        if (descriptor == null ||
            planeCount <= 0 ||
            planeCount > 4 ||
            (descriptor->flags & (AV_PIX_FMT_FLAG_PAL | AV_PIX_FMT_FLAG_BITSTREAM)) != 0)
        {
            return false;
        }

        // A subsampled plane only has samples on its own lattice, so an offset landing between them
        // is a layout that cannot be expressed as a copy.
        int chromaX = descriptor->log2_chroma_w;
        int chromaY = descriptor->log2_chroma_h;
        if ((x & ((1 << chromaX) - 1)) != 0 || (y & ((1 << chromaY) - 1)) != 0)
            return false;

        // Turning a pixel count into a byte count per plane is exactly what linesizes are, so the
        // horizontal offset and the row width are both asked for as though they were rows.
        int* column = stackalloc int[4];
        int* row = stackalloc int[4];
        if (av_image_fill_linesizes(column, pixelFormat, x) < 0 ||
            av_image_fill_linesizes(row, pixelFormat, tile->width) < 0)
        {
            return false;
        }

        for (uint plane = 0; plane < planeCount; plane++)
        {
            bool subsampled = plane is 1 or 2;
            int top = subsampled ? y >> chromaY : y;
            int rows = subsampled ? -((-tile->height) >> chromaY) : tile->height;

            av_image_copy_plane(
                canvas->data[plane] + ((long)top * canvas->linesize[plane]) + column[plane],
                canvas->linesize[plane],
                tile->data[plane],
                tile->linesize[plane],
                row[plane],
                rows);
        }

        return true;
    }

    private static bool ApplyGridCrop(AVStreamGroup* mosaic, IsoImageProperties.Item properties)
    {
        AVStreamGroupTileGrid* grid = mosaic->@params.tile_grid;
        if (properties == null || !properties.TryGetCrop(grid->width, grid->height, out var crop) ||
            grid->horizontal_offset < 0 || grid->vertical_offset < 0 ||
            (long)grid->horizontal_offset + grid->width > grid->coded_width ||
            (long)grid->vertical_offset + grid->height > grid->coded_height)
            return false;
        grid->horizontal_offset += (int)crop.Left;
        grid->vertical_offset += (int)crop.Top;
        grid->width -= (int)(crop.Left + crop.Right);
        grid->height -= (int)(crop.Top + crop.Bottom);
        return true;
    }

    private static bool TryReadCrop(AVCodecParameters* parameters, int width, int height,
        out (uint Top, uint Bottom, uint Left, uint Right) crop)
    {
        crop = default;
        if (width <= 0 || height <= 0 ||
            parameters->coded_side_data == null || parameters->nb_coded_side_data <= 0)
        {
            return false;
        }

        AVPacketSideData* sideData = av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_FRAME_CROPPING);
        if (sideData == null || sideData->data == null || sideData->size < 16)
            return false;

        var bytes = new ReadOnlySpan<byte>(sideData->data, 16);
        crop = (BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        return (ulong)crop.Left + crop.Right < (uint)width && (ulong)crop.Top + crop.Bottom < (uint)height &&
            (crop.Top | crop.Bottom | crop.Left | crop.Right) != 0;
    }

    /// <summary>
    /// Trims the canvas to the picture. A grid is coded in whole tiles, so it usually runs past the
    /// photograph on the right and bottom edges.
    /// </summary>
    private static bool TryCropCanvas(AVFrame* canvas, AVStreamGroupTileGrid* grid)
    {
        if (grid->horizontal_offset < 0 ||
            grid->vertical_offset < 0 ||
            grid->horizontal_offset + grid->width > canvas->width ||
            grid->vertical_offset + grid->height > canvas->height)
        {
            return false;
        }

        canvas->crop_top = (nuint)grid->vertical_offset;
        canvas->crop_bottom = (nuint)(canvas->height - grid->vertical_offset - grid->height);
        canvas->crop_left = (nuint)grid->horizontal_offset;
        canvas->crop_right = (nuint)(canvas->width - grid->horizontal_offset - grid->width);
        return av_frame_apply_cropping(canvas, FrameCropUnaligned) >= 0;
    }

    /// <summary>
    /// Moves the decoded picture into a frame the renderer can sample, converting only where its
    /// layout is one the renderer has no sampler for.
    /// </summary>
    private static VideoFrame Convert(AVFrame* input, AVCodecParameters* parameters, bool associatedAlpha = false)
    {
        var format = (AVPixelFormat)input->format;
        int width = input->width;
        int height = input->height;

        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(format);
        if (descriptor != null && (descriptor->flags & AV_PIX_FMT_FLAG_ALPHA) != 0)
        {
            VideoFrame packed = PackNative(input, descriptor->comp[0].depth > 8, associatedAlpha);
            if (packed is not null)
                packed.HasAlpha = true;
            return packed;
        }

        if (VideoFrameMetadata.MapLayout(format) is { } mapped)
        {
            VideoFrame direct = VideoFrame.CreateStandalone(width, height, mapped);
            for (int plane = 0; plane < direct.PlaneCount; plane++)
            {
                av_image_copy_plane(
                    (byte*)direct.GetPlane(plane),
                    direct.GetStride(plane),
                    input->data[(uint)plane],
                    input->linesize[(uint)plane],
                    direct.GetRowBytes(plane),
                    direct.GetPlaneHeight(plane));
            }

            return direct;
        }

        // Everything else is RGB of some description, which no sampler reads directly. Eight-bit
        // sources become BGRA, which Skia can often draw without the shader pipeline at all;
        // deeper ones become 4:4:4 at sixteen bits, where the round trip through a luma/chroma
        // matrix costs nothing visible and the extra bits an HDR still carries survive.
        bool deep = descriptor != null && descriptor->comp[0].depth > 8;

        return deep
            ? ConvertDeep(input, parameters, width, height)
            : ConvertPacked(input, width, height);
    }

    private static VideoFrame ConvertDeep(AVFrame* input, AVCodecParameters* parameters, int width, int height)
    {
        VideoFrame target = VideoFrame.CreateStandalone(width, height, VideoPixelFormat.Yuv444p16);
        SwsContext* scaler = sws_getContext(
            width,
            height,
            (AVPixelFormat)input->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_YUV444P16LE,
            SwsBilinear,
            null,
            null,
            null);
        if (scaler == null)
        {
            target.Release();
            return null;
        }

        try
        {
            // The renderer inverts exactly what is chosen here, so any matrix would round trip.
            // Matching the source's own primaries keeps the two descriptions of the picture
            // consistent for anything that reads them apart from the shader.
            int coefficients = ReadPrimaries(input, parameters) == VideoColorPrimaries.Bt2020
                ? SwsColorspaceBt2020
                : SwsColorspaceBt709;
            int configured = sws_setColorspaceDetails(
                scaler,
                sws_getCoefficients(SwsColorspaceBt709),
                1,
                sws_getCoefficients(coefficients),
                1,
                0,
                1 << 16,
                1 << 16);
            if (configured < 0)
            {
                target.Release();
                throw new FfmpegException($"Could not configure image color conversion: {FfmpegError.Describe(configured)}", configured);
            }

            return Scale(scaler, input, target, height) ? target : Fail(target);
        }
        finally
        {
            sws_freeContext(scaler);
        }
    }

    private static VideoFrame ConvertPacked(AVFrame* input, int width, int height)
    {
        VideoFrame target = VideoFrame.CreateStandalone(width, height, VideoPixelFormat.Bgra32);
        SwsContext* scaler = sws_getContext(
            width,
            height,
            (AVPixelFormat)input->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            SwsBilinear,
            null,
            null,
            null);
        if (scaler == null)
        {
            target.Release();
            return null;
        }

        try
        {
            if (!Scale(scaler, input, target, height))
                return Fail(target);
        }
        finally
        {
            sws_freeContext(scaler);
        }

        return target;
    }

    private static VideoFrame Fail(VideoFrame target)
    {
        target.Release();
        return null;
    }

    private static bool Scale(SwsContext* scaler, AVFrame* input, VideoFrame target, int height)
    {
        byte** sourceData = stackalloc byte*[4] { input->data[0], input->data[1], input->data[2], input->data[3] };
        int* sourceStride = stackalloc int[4]
        {
            input->linesize[0],
            input->linesize[1],
            input->linesize[2],
            input->linesize[3],
        };

        byte** destinationData = stackalloc byte*[4];
        int* destinationStride = stackalloc int[4];
        for (int plane = 0; plane < 4; plane++)
        {
            destinationData[plane] = plane < target.PlaneCount ? (byte*)target.GetPlane(plane) : null;
            destinationStride[plane] = plane < target.PlaneCount ? target.GetStride(plane) : 0;
        }

        return sws_scale(scaler, sourceData, sourceStride, 0, height, destinationData, destinationStride) == height;
    }

    private static void ApplyColor(VideoFrame target, AVFrame* input, AVCodecParameters* parameters)
    {
        // A converted frame carries the matrix and range the conversion produced, not the source's.
        AVPixelFormat pixelFormat = input->hw_frames_ctx != null
            ? ((AVHWFramesContext*)input->hw_frames_ctx->data)->sw_format : (AVPixelFormat)input->format;
        bool converted = VideoFrameMetadata.MapLayout(pixelFormat) is null;

        AVColorSpace colorSpace = input->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? parameters->color_space
            : input->colorspace;
        AVColorRange range = input->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
            ? parameters->color_range
            : input->color_range;
        AVChromaLocation chromaLocation = input->chroma_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED
            ? parameters->chroma_location
            : input->chroma_location;

        target.ColorPrimaries = ReadPrimaries(input, parameters);
        target.ColorTransfer = VideoFrameMetadata.MapColorTransfer(ReadTransfer(input, parameters));
        target.HdrPeakLuminance = VideoFrameMetadata.ReadHdrPeakLuminance(input);
        target.HdrMasteringLuminance = VideoFrameMetadata.ReadHdrMasteringLuminance(input) ??
            CodecParameters.ReadHdrMasteringLuminance(parameters);
        target.ChromaLocation = VideoFrameMetadata.MapChromaLocation(chromaLocation);

        if (converted)
        {
            target.ColorSpace = target.ColorPrimaries == VideoColorPrimaries.Bt2020
                ? VideoColorSpace.Bt2020
                : VideoColorSpace.Bt709;
            target.ColorRange = VideoColorRange.Full;

            // 4:4:4 has every chroma sample on its luma, and BGRA has no chroma to site at all.
            target.ChromaLocation = VideoChromaLocation.TopLeft;
            return;
        }

        target.ColorSpace = VideoFrameMetadata.MapColorSpace(colorSpace, input->height);
        target.ColorRange = range == AVColorRange.AVCOL_RANGE_JPEG ||
                            VideoFrameMetadata.IsFullRangeFormat(pixelFormat)
            ? VideoColorRange.Full
            : VideoColorRange.Limited;
    }

    private static VideoColorPrimaries ReadPrimaries(AVFrame* input, AVCodecParameters* parameters)
    {
        AVColorPrimaries primaries = input->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
            ? parameters->color_primaries
            : input->color_primaries;
        if (primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED &&
            VideoFrameMetadata.TryReadIccCicp(input, out AVColorPrimaries iccPrimaries, out _))
        {
            primaries = iccPrimaries;
        }

        AVColorSpace colorSpace = input->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? parameters->color_space
            : input->colorspace;

        return VideoFrameMetadata.MapColorPrimaries(
            primaries,
            VideoFrameMetadata.MapColorSpace(colorSpace, input->height));
    }

    private static AVColorTransferCharacteristic ReadTransfer(AVFrame* input, AVCodecParameters* parameters)
    {
        AVColorTransferCharacteristic transfer = input->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
            ? parameters->color_trc
            : input->color_trc;
        if (transfer == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED &&
            VideoFrameMetadata.TryReadIccCicp(input, out _, out AVColorTransferCharacteristic iccTransfer))
        {
            transfer = iccTransfer;
        }

        return transfer;
    }

    private static void ApplyOrientation(
        VideoFrame target,
        AVFrame* input,
        AVCodecParameters* parameters,
        bool hasExif,
        int exifRotation,
        bool exifMirror)
    {
        ReadOrientation(input, parameters, hasExif, exifRotation, exifMirror, out int rotation, out bool mirror);
        target.Rotation = rotation;
        target.MirrorHorizontally = mirror;
        target.PixelAspectRatio = Utils.RationalToDouble(ReadSampleAspectRatio(input, parameters));
    }

    private static AVRational ReadSampleAspectRatio(AVFrame* input, AVCodecParameters* parameters) =>
        input->sample_aspect_ratio.num > 0 && input->sample_aspect_ratio.den > 0 ? input->sample_aspect_ratio :
        parameters->sample_aspect_ratio.num > 0 && parameters->sample_aspect_ratio.den > 0 ? parameters->sample_aspect_ratio :
        new AVRational { num = 1, den = 1 };

    /// <summary>
    /// The turn that brings the picture upright. A container that records one is believed over the
    /// EXIF tag, because HEIF and AVIF put the transform in <c>irot</c>/<c>imir</c> and a stale
    /// EXIF block copied in from the original capture may disagree with it.
    /// </summary>
    private static void ReadOrientation(
        AVFrame* input,
        AVCodecParameters* parameters,
        bool hasExif,
        int exifRotation,
        bool exifMirror,
        out int rotation,
        out bool mirrorHorizontally)
    {
        if (VideoDisplayMatrix.TryRead(input, out rotation, out mirrorHorizontally))
            return;

        VideoDisplayMatrix.Read(parameters, out rotation, out mirrorHorizontally);
        if (rotation != 0 || mirrorHorizontally)
            return;

        if (hasExif)
        {
            rotation = exifRotation;
            mirrorHorizontally = exifMirror;
        }
    }

    private static byte[] ToBgra(AVFrame* input, int width, int height)
    {
        using NativeBuffer pixels = ToBgraNative(input, width, height);
        return pixels?.GetSpan().ToArray();
    }

    private static NativeBuffer ToBgraNative(AVFrame* input, int width, int height)
    {
        SwsContext* scaler = sws_getContext(
            input->width,
            input->height,
            (AVPixelFormat)input->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            SwsBilinear,
            null,
            null,
            null);
        if (scaler == null)
            return null;

        try
        {
            if (input->color_range != AVColorRange.AVCOL_RANGE_UNSPECIFIED)
            {
                int coefficients = VideoFrameMetadata.MapColorSpace(input->colorspace, input->height) switch
                {
                    VideoColorSpace.Bt601 => 5,
                    VideoColorSpace.Bt2020 => SwsColorspaceBt2020,
                    _ => SwsColorspaceBt709,
                };
                if (sws_setColorspaceDetails(scaler,
                    sws_getCoefficients(coefficients), input->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0,
                    sws_getCoefficients(SwsColorspaceBt709), 1, 0, 1 << 16, 1 << 16) < 0)
                    return null;
            }

            int stride = checked(width * 4);
            var pixels = new NativeBuffer(checked(stride * height));
            try
            {
                byte* destination = (byte*)pixels.Address;
                byte** sourceData = stackalloc byte*[4] { input->data[0], input->data[1], input->data[2], input->data[3] };
                int* sourceStride = stackalloc int[4]
                {
                    input->linesize[0],
                    input->linesize[1],
                    input->linesize[2],
                    input->linesize[3],
                };
                byte** destinationData = stackalloc byte*[4] { destination, null, null, null };
                int* destinationStride = stackalloc int[4] { stride, 0, 0, 0 };
                if (sws_scale(
                        scaler,
                        sourceData,
                        sourceStride,
                        0,
                        input->height,
                        destinationData,
                        destinationStride) != height)
                {
                    pixels.Dispose();
                    return null;
                }
            }
            catch
            {
                pixels.Dispose();
                throw;
            }

            return pixels;
        }
        finally
        {
            sws_freeContext(scaler);
        }
    }

    /// <summary>
    /// Turns a thumbnail upright. The renderer does this on the canvas for the picture it draws,
    /// but a tile is handed to the UI as plain pixels with nowhere to carry a transform.
    /// </summary>
    private static NativeImageThumbnail Orient(NativeBuffer pixels, int width, int height, int rotation, bool mirror)
    {
        if (rotation == 0 && !mirror)
            return new NativeImageThumbnail(width, height, pixels);

        using NativeBuffer source = pixels;
        bool swap = rotation is 90 or 270;
        int targetWidth = swap ? height : width;
        int targetHeight = swap ? width : height;
        var target = new NativeBuffer(checked(targetWidth * targetHeight * 4));
        try
        {
            Span<byte> sourceBytes = source.GetSpan();
            Span<byte> targetBytes = target.GetSpan();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sourceX = mirror ? width - 1 - x : x;
                    (int targetX, int targetY) = rotation switch
                    {
                        90 => (height - 1 - y, sourceX),
                        180 => (width - 1 - sourceX, height - 1 - y),
                        270 => (y, width - 1 - sourceX),
                        _ => (sourceX, y),
                    };

                    sourceBytes.Slice(((y * width) + x) * 4, 4)
                        .CopyTo(targetBytes.Slice(((targetY * targetWidth) + targetX) * 4, 4));
                }
            }

            return new NativeImageThumbnail(targetWidth, targetHeight, target);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>Fits the picture inside a square of <paramref name="maxDimension"/>, aspect corrected.</summary>
    private static void ComputeSize(
        int width,
        int height,
        AVRational sampleAspectRatio,
        int maxDimension,
        out int targetWidth,
        out int targetHeight)
    {
        double aspect = sampleAspectRatio.num > 0 && sampleAspectRatio.den > 0
            ? Utils.RationalToDouble(sampleAspectRatio)
            : 1.0;

        double displayWidth = width * aspect;
        double scale = Math.Min(1.0, maxDimension / Math.Max(displayWidth, height));
        targetWidth = Math.Max(1, (int)Math.Round(displayWidth * scale));
        targetHeight = Math.Max(1, (int)Math.Round(height * scale));
    }
}
