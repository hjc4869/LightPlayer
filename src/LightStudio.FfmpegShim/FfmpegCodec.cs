using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using static LightStudio.FfmpegShim.Ffmpeg;


namespace LightStudio.FfmpegShim
{
    public unsafe partial class FfmpegCodec
    {
        private const int IoBufferSize = 16384;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static private FfmpegMediaInfo InternalGetMediaInfo(AVIOContext* io)
        {
            int ret = 0;
            if (io == null)
            {
                throw new Exception("Failed to allocate IO context");
            }
            var pFormatContext = avformat_alloc_context();
            pFormatContext->pb = io;
            ret = avformat_open_input(&pFormatContext, "", null, null);
            if (ret < 0)
            {
                avformat_close_input(&pFormatContext);
                throw new Exception($"avformat_open_input failed with {ret:X}");
            }
            ret = avformat_find_stream_info(pFormatContext, null);
            if (ret < 0)
            {
                avformat_close_input(&pFormatContext);
                throw new Exception($"avformat_find_stream_info failed with {ret:X}");
            }
            var nAudioStream = -1;
            for (int i = 0; i < pFormatContext->nb_streams; i++)
            {
                if (pFormatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    nAudioStream = i;
                    break;
                }
            }
            var metadata = pFormatContext->metadata;
            var AudiofileDuration = 1L;
            if (nAudioStream!= -1)
            {
                AVStream* audio = pFormatContext->streams[nAudioStream];
                // Containers such as Matroska leave the stream duration unset and only state the file's.
                AudiofileDuration = audio->duration != AV_NOPTS_VALUE
                    ? (long)(audio->duration * Utils.RationalToDouble(audio->time_base) * 10000000L)
                    : pFormatContext->duration != AV_NOPTS_VALUE
                        ? pFormatContext->duration * (10000000L / AV_TIME_BASE)
                        : 1L;
                if (metadata == null)
                {
                    metadata = audio->metadata;
                }
            }
            var r = new FfmpegMediaInfo(AudiofileDuration, metadata);
            avformat_close_input(&pFormatContext);
            return r;
        }

        static private T InternalGetAlbumCover<T>(FfmpegStreamIO io, Func<ReadOnlySpan<byte>, T> read) where T : class
        {
            int ret = 0;
            if (io.Context == null)
            {
                throw new Exception("Failed to allocate IO context");
            }
            var pFormatContext = avformat_alloc_context();
            pFormatContext->pb = io.Context;
            ret = avformat_open_input(&pFormatContext, "", null, null);
            try
            {
                io.ThrowIfReadFailed();
                Interop.FfmpegError.ThrowIfError(ret, "avformat_open_input");
                for (uint i = 0; i < pFormatContext->nb_streams; i++)
                {
                    if ((pFormatContext->streams[i]->disposition &
                        AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        var picture = pFormatContext->streams[i]->attached_pic;
                        // FFmpeg can return a partial attached picture after an I/O failure and mark its packet corrupt.
                        if ((picture.flags & AV_PKT_FLAG_CORRUPT) != 0)
                        {
                            AppLog.Warning("FFmpeg.Artwork", $"Skipping corrupt embedded artwork: stream={i}; bytes={picture.size}.");
                            continue;
                        }

                        if (picture.data != null && picture.size > 0)
                            return read(new ReadOnlySpan<byte>(picture.data, picture.size));
                    }
                }
                return null;
            }
            finally
            {
                avformat_close_input(&pFormatContext);
            }
        }

        static public byte[] GetAlbumCoverFromStream(Stream stream, bool close = true) =>
            ReadAlbumCoverFromStream(stream, static data => data.ToArray(), close);

        /// <summary>Passes the embedded cover to <paramref name="read"/> while it is in FFmpeg's buffer.</summary>
        static public T ReadAlbumCoverFromStream<T>(Stream stream, Func<ReadOnlySpan<byte>, T> read, bool close = true)
            where T : class
        {
            var io = new FfmpegStreamIO(stream, IoBufferSize);
            try
            {
                return InternalGetAlbumCover(io, read);
            }
            finally
            {
                io.Dispose();
                if (close)
                    stream.Dispose();
            }
        }

        static public FfmpegMediaInfo GetMediaInfoFromStream(Stream stream, bool close = true)
        {
            var io = new FfmpegStreamIO(stream, IoBufferSize);
            try
            {
                return InternalGetMediaInfo(io.Context);
            }
            finally
            {
                io.Dispose();
                if (close)
                    stream.Dispose();
            }
        }
    }
}
