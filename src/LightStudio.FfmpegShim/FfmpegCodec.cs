using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using static FFmpeg.AutoGen.ffmpeg;
using static LightStudio.FfmpegShim.FfmpegIO;

namespace LightStudio.FfmpegShim
{
    public unsafe class FfmpegCodec
    {
        static FfmpegCodec()
        {
            FfmpegNativeInitializer.Initialize();
        }

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
                AudiofileDuration =
                    (long)(pFormatContext->streams[nAudioStream]->duration *
                    av_q2d(pFormatContext->streams[nAudioStream]->time_base)
                    * 10000000L);
                if (metadata == null)
                {
                    metadata = pFormatContext->streams[nAudioStream]->metadata;
                }
            }
            var r = new FfmpegMediaInfo(AudiofileDuration, metadata);
            avformat_close_input(&pFormatContext);
            return r;
        }

        static private Stream InternalGetAlbumCover(AVIOContext* io)
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
            for (uint i = 0; i < pFormatContext->nb_streams; i++)
            {
                if ((pFormatContext->streams[i]->disposition &
                    AV_DISPOSITION_ATTACHED_PIC) != 0)
                {
                    var size = pFormatContext->streams[i]->attached_pic.size;
                    byte[] buffer = new byte[size];
                    fixed (byte* buf = buffer)
                        Buffer.MemoryCopy(pFormatContext->streams[i]->attached_pic.data, buf, size, size);
                    MemoryStream stream = new MemoryStream(buffer);
                    avformat_close_input(&pFormatContext);
                    return stream;
                }
            }
            avformat_close_input(&pFormatContext);
            return null;
        }

        static public Stream GetAlbumCoverFromStream(Stream stream, bool close = true)
        {
            var fr = new avio_alloc_context_read_packet(StreamRead);
            var fs = new avio_alloc_context_seek(StreamSeek);
            var ioobj = new FfmpegIO(stream);
            var streamReadHandle = GCHandle.Alloc(fr);
            var streamSeekHandle = GCHandle.Alloc(fs);
            var ioObjHandle = GCHandle.Alloc(ioobj);
            AVIOContext* io = null;
            io = avio_alloc_context(
                (byte*)av_malloc(16384),
                16384, 0,
                &ioObjHandle,
                fr,
                null,
                fs);
            try
            {
                return InternalGetAlbumCover(io);
            }
            finally
            {
                if (io != null)
                {
                    av_freep(&io->buffer);
                    av_free(io);
                }
                streamReadHandle.Free();
                streamSeekHandle.Free();
                ioObjHandle.Free();
                ioobj.Dispose(close);
            }
        }

        static public FfmpegMediaInfo GetMediaInfoFromStream(Stream stream, bool close = true)
        {
            var fr = new avio_alloc_context_read_packet(StreamRead);
            var fs = new avio_alloc_context_seek(StreamSeek);
            var ioobj = new FfmpegIO(stream);
            var streamReadHandle = GCHandle.Alloc(fr);
            var streamSeekHandle = GCHandle.Alloc(fs);
            var ioObjHandle = GCHandle.Alloc(ioobj);
            AVIOContext* io = null;
            io = avio_alloc_context(
                (byte*)av_malloc(16384),
                16384, 0,
                &ioObjHandle,
                fr,
                null,
                fs);
            try
            {
                return InternalGetMediaInfo(io);
            }
            finally
            {
                if (io != null)
                {
                    av_freep(&io->buffer);
                    av_free(io);
                }
                streamReadHandle.Free();
                streamSeekHandle.Free();
                ioObjHandle.Free();
                ioobj.Dispose(close);
            }
        }
    }
}
