using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

public static unsafe partial class StillImage
{
    public static AnimationSource OpenAnimation(Stream stream, out StillImageNative first,
        CancellationToken cancellationToken = default)
    {
        first = null;
        ImageAnimationMetadata metadata = ImageAnimationMetadata.Read(stream, cancellationToken);
        if (metadata is null)
            return null;
        long origin = stream.Position;
        long length = stream.Length - origin;
        if (length is <= 0 or > 256L * 1024 * 1024)
            throw new InvalidDataException("The encoded animation exceeds the 256 MiB limit.");
        var bytes = new NativeBuffer((int)length);
        AnimationSource source = null;
        try
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(bytes.Length - offset, 64 * 1024);
                stream.ReadExactly(bytes.GetSpan().Slice(offset, count));
                offset += count;
            }
            source = new AnimationSource(bytes, metadata);
            using AnimationDecoder decoder = source.CreateDecoder();
            first = decoder.Read(cancellationToken) ?? throw new InvalidDataException("The animation contains no frames.");
            return source;
        }
        catch
        {
            first?.Dispose();
            first = null;
            if (source is not null)
                source.Dispose();
            else
                bytes.Dispose();
            throw;
        }
        finally { stream.Position = origin; }
    }

    public sealed class AnimationSource : IDisposable
    {
        private readonly NativeBuffer bytes;
        private readonly ImageAnimationMetadata metadata;
        private int references = 1;

        internal AnimationSource(NativeBuffer bytes, ImageAnimationMetadata metadata)
        {
            this.bytes = bytes;
            this.metadata = metadata;
        }

        public long EncodedBytes => bytes.Length;
        public long PlayCount => metadata.PlayCount;

        public AnimationSource AddRef()
        {
            int count;
            do
            {
                count = Volatile.Read(ref references);
                ObjectDisposedException.ThrowIf(count <= 0, this);
            } while (Interlocked.CompareExchange(ref references, count + 1, count) != count);
            return this;
        }

        public AnimationDecoder CreateDecoder() => new(this, metadata.TrackId, metadata.AlphaTrackId is not null);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref references) == 0)
                bytes.Dispose();
        }

        internal Stream OpenRead() => bytes.OpenRead();
        internal NativeBuffer Bytes => bytes;
        internal int? AlphaTrackId => metadata.AlphaTrackId;
        internal bool PremultipliedAlpha => metadata.PremultipliedAlpha;
    }

    public sealed class AnimationDecoder : IDisposable
    {
        private readonly AnimationSource source;
        private readonly int? trackId;
        private readonly bool includeAlpha;
        private SkiaImageAnimation skia;
        private AnimationDecoder alphaDecoder;
        private AVFrame* alphaFrame;
        private AVCodecParameters* alphaParameters;
        private double alphaTime;
        private byte[] colorProfile;
        private Stream stream;
        private FfmpegStreamIO io;
        private AVFormatContext* format;
        private AVCodecContext* context;
        private AVPacket* packet;
        private AVFrame* decoded;
        private int videoIndex;
        private bool draining;
        private bool disposed;
        private long completedPlays;
        private int framesInPlay;
        private double pixelAspect;
        private bool hasExif;
        private int rotation;
        private bool mirror;

        internal AnimationDecoder(AnimationSource source, int? trackId, bool includeAlpha)
        {
            this.source = source.AddRef();
            this.trackId = trackId;
            this.includeAlpha = includeAlpha;
            try { Open(); }
            catch { Dispose(); throw; }
        }

        private void Open()
        {
            stream = source.OpenRead();
            hasExif = ExifOrientation.TryRead(stream, out rotation, out mirror);
            pixelAspect = ReadDensityAspectRatio(stream);
            if (SkiaImageAnimation.Supports(source.Bytes.GetSpan()))
            {
                skia = new SkiaImageAnimation(source.Bytes, rotation, mirror,
                    pixelAspect > 0 ? pixelAspect : ReadPixelAspectRatio(stream));
                return;
            }
            io = new FfmpegStreamIO(stream, IoBufferSize);
            AVFormatContext* opening = avformat_alloc_context();
            if (opening == null)
                throw new OutOfMemoryException();
            opening->pb = io.Context;
            opening->max_streams = 256;
            int result = avformat_open_input(&opening, string.Empty, null, null);
            format = opening;
            Check(result, "open animated image");
            Check(avformat_find_stream_info(format, null), "read animation streams");
            videoIndex = -1;
            for (int index = 0; index < format->nb_streams; index++)
            {
                AVStream* candidate = format->streams[index];
                if (candidate->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                    (trackId is not { } track || candidate->id == track) &&
                    (videoIndex < 0 || candidate->nb_frames > format->streams[videoIndex]->nb_frames ||
                        candidate->nb_frames == format->streams[videoIndex]->nb_frames &&
                        candidate->duration > format->streams[videoIndex]->duration))
                {
                    videoIndex = index;
                }
            }
            if (videoIndex < 0)
                throw new InvalidDataException("The animation has no image sequence.");
            AVStream* video = format->streams[videoIndex];
            if (video->codecpar->width <= 0 || video->codecpar->height <= 0 ||
                (long)video->codecpar->width * video->codecpar->height > 32L * 1024 * 1024)
                throw new InvalidDataException("The animation canvas exceeds the 32 megapixel limit.");
            for (int index = 0; index < format->nb_streams; index++)
                format->streams[index]->discard = index == videoIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            context = OpenDecoder(video, Math.Min(Environment.ProcessorCount, 4), null);
            if (context == null)
                throw new NotSupportedException("This FFmpeg build cannot decode the animated image.");
            context->max_pixels = 32L * 1024 * 1024;
            packet = av_packet_alloc();
            decoded = av_frame_alloc();
            if (packet == null || decoded == null)
                throw new OutOfMemoryException();
            draining = false;
            framesInPlay = 0;
            if (includeAlpha)
            {
                alphaDecoder = new AnimationDecoder(source, source.AlphaTrackId, false);
                alphaFrame = av_frame_alloc();
                alphaParameters = avcodec_parameters_alloc();
                if (alphaFrame == null || alphaParameters == null)
                    throw new OutOfMemoryException();
                Check(avcodec_parameters_copy(alphaParameters,
                    alphaDecoder.format->streams[alphaDecoder.videoIndex]->codecpar), "copy animation alpha properties");
                alphaTime = double.NegativeInfinity;
            }
        }

        public StillImageNative Read(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (skia is not null)
                return skia.Read(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int result = avcodec_receive_frame(context, decoded);
                if (result == 0)
                {
                    framesInPlay++;
                    try
                    {
                        ApplyFrameCrop();
                        if (includeAlpha)
                            CombineAnimationAlpha(cancellationToken);
                        return RetainFrame(cancellationToken);
                    }
                    finally { av_frame_unref(decoded); }
                }
                if (result == AVERROR_EOF)
                {
                    if (framesInPlay == 0)
                        throw new InvalidDataException("The animation contains an empty sequence.");
                    completedPlays++;
                    if (source.PlayCount > 0 && completedPlays >= source.PlayCount)
                        return null;
                    CloseDecoder();
                    Open();
                    continue;
                }
                if (!FfmpegError.IsTryAgain(result))
                    Check(result, "decode animation frame");
                if (draining)
                    throw new InvalidDataException("The animation decoder did not finish draining.");
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    av_packet_unref(packet);
                    result = av_read_frame(format, packet);
                    io.ThrowIfReadFailed();
                    if (result == AVERROR_EOF)
                    {
                        draining = true;
                        Check(avcodec_send_packet(context, null), "drain animation frames");
                        break;
                    }
                    Check(result, "read animation packet");
                } while (packet->stream_index != videoIndex);
                if (!draining)
                {
                    try { Check(avcodec_send_packet(context, packet), "submit animation packet"); }
                    finally { av_packet_unref(packet); }
                }
            }
        }

        private void ApplyFrameCrop()
        {
            AVStream* video = format->streams[videoIndex];
            if (TryReadCrop(video->codecpar, decoded->width, decoded->height, out var crop))
            {
                decoded->crop_top = crop.Top;
                decoded->crop_bottom = crop.Bottom;
                decoded->crop_left = crop.Left;
                decoded->crop_right = crop.Right;
                Check(av_frame_apply_cropping(decoded, FrameCropUnaligned), "crop animation frame");
            }
        }

        private StillImageNative RetainFrame(CancellationToken cancellationToken)
        {
            AVStream* video = format->streams[videoIndex];
            if (pixelAspect > 0)
                decoded->sample_aspect_ratio = av_d2q(pixelAspect, int.MaxValue);
            VideoFrame frame = RetainNative(decoded, video->codecpar)
                ?? throw new InvalidDataException("The animation pixels could not be retained.");
            try
            {
                ApplyColor(frame, decoded, video->codecpar);
                ApplyOrientation(frame, decoded, video->codecpar, hasExif, rotation, mirror);
                frame.FieldOrder = VideoFieldOrder.Progressive;
                double duration = decoded->duration * Utils.RationalToDouble(video->time_base);
                if (!double.IsFinite(duration) || duration <= 0)
                {
                    AVRational rate = av_guess_frame_rate(format, video, decoded);
                    duration = rate.num > 0 && rate.den > 0 ? Utils.RationalToDouble(Utils.InvertRational(rate)) : 0.1;
                }
                frame.Duration = Math.Clamp(duration, 0.01, 24 * 60 * 60);
                AVFrameSideData* profile = av_frame_get_side_data(decoded, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
                cancellationToken.ThrowIfCancellationRequested();
                return new StillImageNative
                {
                    Frame = frame,
                    IccProfile = colorProfile ??= profile != null && profile->data != null && profile->size is > 0 and <= 4 * 1024 * 1024
                        ? new ReadOnlySpan<byte>(profile->data, (int)profile->size).ToArray() : [],
                    ColorPrimaries = (int)(decoded->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                        ? video->codecpar->color_primaries : decoded->color_primaries),
                    ColorTransfer = (int)ReadTransfer(decoded, video->codecpar),
                };
            }
            catch { frame.Release(); throw; }
        }

        private void CombineAnimationAlpha(CancellationToken cancellationToken)
        {
            AVStream* video = format->streams[videoIndex];
            double time = decoded->pts == AV_NOPTS_VALUE ? 0 : decoded->pts * Utils.RationalToDouble(video->time_base);
            while (alphaTime <= time + 1e-6)
            {
                if (!alphaDecoder.ReadAlphaFrame(alphaFrame, out double duration, cancellationToken))
                    throw new InvalidDataException("The animated alpha sequence ended before the color sequence.");
                alphaTime = double.IsNegativeInfinity(alphaTime) ? duration : alphaTime + duration;
            }
            AVFrame* combined = CombinePhotoAlpha(decoded, video->codecpar, alphaFrame, alphaParameters, source.PremultipliedAlpha);
            try
            {
                av_frame_unref(decoded);
                av_frame_move_ref(decoded, combined);
            }
            finally { av_frame_free(&combined); }
        }

        private bool ReadAlphaFrame(AVFrame* destination, out double duration, CancellationToken cancellationToken)
        {
            using StillImageNative image = Read(cancellationToken);
            duration = image?.Frame.Duration ?? 0;
            if (image is null)
                return false;
            VideoFrame frame = image.Frame;
            AVFrame* pixels = av_frame_alloc();
            if (pixels == null)
                throw new OutOfMemoryException();
            try
            {
                pixels->width = frame.Width;
                pixels->height = frame.Height;
                pixels->format = (int)(frame.Format == VideoPixelFormat.Rgba64 ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_BGRA);
                Check(av_frame_get_buffer(pixels, 0), "allocate animation alpha frame");
                if (frame.Format is VideoPixelFormat.Bgra32 or VideoPixelFormat.Rgba64)
                    av_image_copy_plane(pixels->data[0], pixels->linesize[0], (byte*)frame.Data, frame.Stride,
                        frame.GetRowBytes(0), frame.Height);
                else
                    throw new InvalidDataException("The animation alpha decoder did not produce grayscale pixels.");
                pixels->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                pixels->colorspace = AVColorSpace.AVCOL_SPC_RGB;
                av_frame_unref(destination);
                av_frame_move_ref(destination, pixels);
                return true;
            }
            finally { av_frame_free(&pixels); }
        }

        private void Check(int result, string operation)
        {
            io?.ThrowIfReadFailed();
            if (result < 0)
                throw new FfmpegException($"Could not {operation}: {FfmpegError.Describe(result)}", result);
        }

        private void CloseDecoder()
        {
            skia?.Dispose();
            skia = null;
            alphaDecoder?.Dispose();
            alphaDecoder = null;
            AVFrame* opacity = alphaFrame;
            alphaFrame = null;
            av_frame_free(&opacity);
            AVCodecParameters* opacityParameters = alphaParameters;
            alphaParameters = null;
            avcodec_parameters_free(&opacityParameters);
            AVFrame* frame = decoded;
            decoded = null;
            av_frame_free(&frame);
            AVPacket* pending = packet;
            packet = null;
            av_packet_free(&pending);
            AVCodecContext* codec = context;
            context = null;
            avcodec_free_context(&codec);
            AVFormatContext* opened = format;
            format = null;
            if (opened != null)
                avformat_close_input(&opened);
            io?.Dispose();
            io = null;
            stream?.Dispose();
            stream = null;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            CloseDecoder();
            source.Dispose();
        }
    }
}