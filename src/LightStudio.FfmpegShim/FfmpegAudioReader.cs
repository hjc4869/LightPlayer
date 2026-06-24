using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using static FFmpeg.AutoGen.ffmpeg;

namespace LightStudio.FfmpegShim
{
    public unsafe class FfmpegAudioReader : IDisposable
    {
        private const int BufferSize = 0x800000;

        static FfmpegAudioReader()
        {
            FfmpegNativeInitializer.Initialize();
        }

        public FfmpegAudioReader(Stream stream)
        {
            OpenStream(stream);
        }
        public FfmpegAudioReader(IDelayOpenFile file)
        {
            _file = file;
            delayedStream = true;
        }
        
        ~FfmpegAudioReader()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (decode_lock)
            {
                if (_disposed)
                    return;
                if (disposing)
                {
                    if (!delayedStream || delayedStreamOpened)
                    {
                        fileStream.Dispose();
                        fileStream = null;
                    }
                }

                while (buffer_queue.Count != 0)
                {
                    buffer_queue.Dequeue().Dispose();
                }
                ReleaseFfmpeg();
                if (!delayedStream || delayedStreamOpened)
                {
                    streamReadHandle.Free();
                    streamSeekHandle.Free();
                }
                if (swr_ctx != null)
                {
                    fixed (SwrContext** p = &swr_ctx) swr_free(p);
                }

                fixed (AVChannelLayout* layout = &out_ch_layout)
                {
                    av_channel_layout_uninit(layout);
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void SetResampleTarget(PcmEncodingProperties sample)
        {
            if (swr_ctx != null)
            {
                fixed (SwrContext** p = &swr_ctx) swr_free(p);
            }

            _sampleInfo = sample;
            if (delayedStream && !delayedStreamOpened) return;


            // channels may be extended later, only support 2 channesl for simplicity
            if (_sampleInfo.ChannelCount != 2) _sampleInfo.ChannelCount = 2;
            if (_sampleInfo.BitsPerSample == 0) _sampleInfo.BitsPerSample = (uint)codecpar->bits_per_raw_sample;

            // Only 16bit or 24bit output
            if (_sampleInfo.BitsPerSample <= 16) _sampleInfo.BitsPerSample = 16;
            else if (_sampleInfo.BitsPerSample > 16) _sampleInfo.BitsPerSample = 24;

            // Fallback to audio file sample rate if not set
            if (_sampleInfo.SampleRate == 0) _sampleInfo.SampleRate = (uint)codecpar->sample_rate;

            AVSampleFormat fmt = _sampleInfo.BitsPerSample == 16 ? AVSampleFormat.AV_SAMPLE_FMT_S16 : AVSampleFormat.AV_SAMPLE_FMT_S32;
            fixed (SwrContext** p = &swr_ctx)
            fixed (AVChannelLayout* layout = &out_ch_layout)
            {
                av_channel_layout_default(layout, (int)_sampleInfo.ChannelCount);  // stereo
                int ret = swr_alloc_set_opts2(
                    p,
                    layout,                         // output channel layout
                    fmt,                            // output sample format
                    (int)_sampleInfo.SampleRate,    // output sample rate
                    &codecpar->ch_layout,           // input channel layout
                    codec->sample_fmt,              // input sample format
                    codecpar->sample_rate,          // input sample rate
                    0,
                    null);

                if (ret < 0)
                {
                    var errstr = stackalloc byte[100];
                    av_strerror(ret, errstr, 100);
                    throw new Exception($"SW resample initialize failed with {ret}\nError message: {Utils.NullTerminatedUTF8StringToString((sbyte*)errstr)}");
                }

                ret = swr_init(swr_ctx);
                if (ret < 0)
                {
                    var errstr = stackalloc byte[100];
                    av_strerror(ret, errstr, 100);
                    throw new Exception($"SW resample initialize failed with {ret}\nError message: {Utils.NullTerminatedUTF8StringToString((sbyte*)errstr)}");
                }
            }
        }

        public void SetTrackTimeRange(TrackTimeInfo info)
        {
            lock (decode_lock)
            {
                CheckAndInitializeDelayedStream();
                if (info == null)
                {
                    audioDurationTicks = -1;
                    startOffsetTicks = 0;
                }
                else
                {
                    audioDurationTicks = info.Duration.Ticks;
                    startOffsetTicks = info.BeginTime.Ticks;
                    if (audioDurationTicks == 0)
                    {
                        audioDurationTicks = AudiofileDuration - startOffsetTicks;
                    }

                    DecodedTicks = 0;
                }
            }
        }
        public FfmpegMediaInfo ReadMetadata()
        {
            lock (decode_lock)
            {
                CheckAndInitializeDelayedStream();
                var metadata = pFormatContext->metadata;
                if (metadata == null)
                {
                    metadata = pFormatContext->streams[nAudioStream]->metadata;
                }
                if (_info == null)
                    _info = new FfmpegMediaInfo(GetActualDuration().Ticks, metadata);
                return _info;
            }
        }
        public UnmanagedBuffer ReadFrontCover()
        {
            lock (decode_lock)
            {
                CheckAndInitializeDelayedStream();
                for (uint i = 0; i < pFormatContext->nb_streams; i++)
                {
                    if ((pFormatContext->streams[i]->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        AVPacket pkt = pFormatContext->streams[i]->attached_pic;
                        var buffer = UnmanagedBuffer.Allocate(pkt.size);
                        Utils.memcpy(buffer.Content, pkt.data, pkt.size);
                        return buffer;
                    }
                }
                return new UnmanagedBuffer();
            }
        }
        public long Seek(long ticks)
        {
            lock (decode_lock)
            {
                CheckAndInitializeDelayedStream();
                avcodec_flush_buffers(codec);
                var tb = av_q2d(pFormatContext->streams[nAudioStream]->time_base);
                var timestamp = (long)((ticks + startOffsetTicks) / 10000000 / tb);
                timestamp = Math.Max(0, timestamp);
                timestamp = Math.Min(timestamp, pFormatContext->streams[nAudioStream]->duration);
                int ret = av_seek_frame(pFormatContext, nAudioStream, timestamp, AVSEEK_FLAG_BACKWARD);
                Console.WriteLine(
                    $"[FfmpegSeek] Seek ticks={ticks} timestamp={timestamp} timeBase={tb} " +
                    $"streamDuration={pFormatContext->streams[nAudioStream]->duration} av_seek_frame={ret}");
                if (timestamp == 0)
                {
                    DecodedTicks = 0;
                }
                else
                {
                    long lastTicks = -1;
                    int seekIterations = 0;
                    for (;;)
                    {
                        seekIterations++;
                        long dts = ReadAndDecodeInternal(out var buffer);

                        if (buffer.Content == null || AV_NOPTS_VALUE == dts)
                        {
                            DecodedTicks = ticks;
                            Console.WriteLine(
                                $"[FfmpegSeek] No audio enqueued: reason={(buffer.Content == null ? "EOF/empty" : "NOPTS-dts")} " +
                                $"iterations={seekIterations} lastFfmpegError={FfmpegLastError} (track will appear to end immediately)");
                            break;
                        }
                        else
                        {
                            using var b = buffer;
                            long actualTicks;
                            if (dts == -1)
                            {
                                if (lastTicks == -1)
                                {
                                    DecodedTicks = ticks;
                                    Console.WriteLine(
                                        $"[FfmpegSeek] No audio enqueued: reason=buffered-frame-without-timestamp " +
                                        $"iterations={seekIterations} (track will appear to end immediately)");
                                    break;
                                }

                                actualTicks = lastTicks;
                                lastTicks = -1;
                            }
                            else
                            {
                                actualTicks = (long)(dts * tb * 10000000L) - startOffsetTicks;
                            }

                            var bufferTicks = buffer.Length * 10000000L / _sampleInfo.SampleRate / (_sampleInfo.ChannelCount * _sampleInfo.BitsPerSample / 8);
                            if (actualTicks + bufferTicks > ticks)
                            {
                                var units = buffer.Length / (_sampleInfo.ChannelCount * _sampleInfo.BitsPerSample / 8);
                                var validUnits = units * (actualTicks + bufferTicks - ticks) / bufferTicks;
                                var validByteLength = (uint)(validUnits * (_sampleInfo.ChannelCount * _sampleInfo.BitsPerSample / 8));

                                // Integer rounding can drive the valid tail to zero when the seek target
                                // lands within the last fraction of this frame. Enqueuing an empty buffer
                                // makes ReadAndDecodeFrame report end-of-stream, which the playback loop
                                // treats as track-ended and skips to the next track. Drop the empty tail
                                // and let the next full frame become the first audio played instead.
                                if (validByteLength > 0)
                                {
                                    var seekBuffer = UnmanagedBuffer.Allocate((int)validByteLength);
                                    Buffer.MemoryCopy(b.Content + b.Length - validByteLength, seekBuffer.Content, validByteLength, validByteLength);
                                    buffer_queue.Enqueue(seekBuffer);
                                }

                                DecodedTicks = ticks;
                                Console.WriteLine(
                                    $"[FfmpegSeek] Target reached: enqueuedBytes={validByteLength} iterations={seekIterations} actualTicks={actualTicks}");
                                break;
                            }
                            else
                            {
                                lastTicks = actualTicks + bufferTicks;
                            }
                        }
                    }
                }
                return DecodedTicks;
            }
        }

        public bool CloseDelayedStream()
        {
            lock (decode_lock)
            {
                if (!(delayedStream && delayedStreamOpened))
                    return false;
                else
                {
                    ReleaseFfmpeg();
                    streamSeekHandle.Free();
                    streamReadHandle.Free();
                    fileStream.Dispose();
                    delayedStreamOpened = false;
                    return true;
                }
            }
        }

        public string ReadCueSheet()
        {
            lock (decode_lock)
            {
                return ReadMetadata().AllProperties["cuesheet"];
            }
        }

        public UnmanagedBuffer ReadAndDecodeFrame(out int bufferTicks)
        {
            lock (decode_lock)
            {
                if (pFormatContext == null)
                {
                    bufferTicks = 0;
                    return default;
                }

                if (audioDurationTicks != -1 && DecodedTicks >= audioDurationTicks)
                {
                    bufferTicks = 0;
                    return default;
                }

                UnmanagedBuffer buffer;
                if (buffer_queue.Count != 0)
                {
                    buffer = buffer_queue.Dequeue();
                }
                else
                {
                    ReadAndDecodeInternal(out buffer);
                }

                if (buffer.Length == 0)
                {
                    bufferTicks = 0;
                    return default;
                }

                if (_sampleInfo != null)
                {
                    bufferTicks = (int)(buffer.Length * 10000000L / _sampleInfo.SampleRate / (_sampleInfo.ChannelCount * _sampleInfo.BitsPerSample / 8));
                }
                else
                {
                    bufferTicks = (int)(buffer.Length * 10000000L / codecpar->sample_rate / (codecpar->ch_layout.nb_channels * bitspersample / 8));
                }

                DecodedTicks += bufferTicks;
                return buffer;
            }
        }
        public PcmEncodingProperties GetOutputAudioProperties()
        {
            lock (decode_lock)
            {
                CheckAndInitializeDelayedStream();
                return _sampleInfo;
            }
        }

        public int LastFfmpegError
        {
            get
            {
                lock (decode_lock)
                {
                    return FfmpegLastError;
                }
            }
        }

        public TimeSpan GetActualDuration()
        {
            if (audioDurationTicks == -1)
            {
                return TimeSpan.FromTicks(AudiofileDuration);
            }
            else
            {
                return TimeSpan.FromTicks(audioDurationTicks);
            }
        }

        #region private methods
        byte[] streamReadBuffer = new byte[4096];
        private int StreamRead(void* opaque, byte* buffer, int bufferSize)
        {
            try
            {
                int read = 0;
                if (streamReadBuffer.Length < bufferSize)
                {
                    streamReadBuffer = new byte[2 * bufferSize];
                }
                read = fileStream.Read(streamReadBuffer, 0, bufferSize);

                if (read == 0)
                {
                    return -0x5fb9b0bb;
                }
                Marshal.Copy(streamReadBuffer, 0, (IntPtr)buffer, read);
                return read;
            }
            catch
            {
                return -1;
            }
        }
        private long StreamSeek(void* opaque, long pos, int whence)
        {
            try
            {
                if (whence == AVSEEK_SIZE)
                {
                    return fileStream.Length;
                }

                return fileStream.Seek(pos, (SeekOrigin)whence);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FfmpegSeek] StreamSeek failed: pos={pos} whence={whence} ({ex.GetType().Name})");
                return -1;
            }
        }

        private void OpenStream(Stream stream)
        {
            fileStream = stream;
            var fr = new avio_alloc_context_read_packet(StreamRead);
            var fs = new avio_alloc_context_seek(StreamSeek);
            streamReadHandle = GCHandle.Alloc(fr);
            streamSeekHandle = GCHandle.Alloc(fs);
            var io = avio_alloc_context(
                (byte*)av_malloc(BufferSize),
                BufferSize, 0,
                null,
                fr,
                null,
                fs);
            int ret = 0;
            if (io == null)
                throw new Exception("Failed to allocate IO context");
            pFormatContext = avformat_alloc_context();
            pFormatContext->pb = io;
            fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                ret = avformat_open_input(ppFormatContext, "", null, null);
            if (ret < 0)
            {
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
                throw new Exception($"avformat_open_input failed with {ret:X}");
            }
            ret = avformat_find_stream_info(pFormatContext, null);
            if (ret < 0)
            {
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
                throw new Exception($"avformat_find_stream_info failed with {ret:X}");
            }
            nAudioStream = -1;
            for (int i = 0; i < pFormatContext->nb_streams; i++)
            {
                if (pFormatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    nAudioStream = i;
                    break;
                }
            }
            AudiofileDuration =
                (long)(pFormatContext->streams[nAudioStream]->duration *
                av_q2d(pFormatContext->streams[nAudioStream]->time_base)
                * 10000000L);
            codecpar = pFormatContext->streams[nAudioStream]->codecpar;
            ret = av_seek_frame(pFormatContext, nAudioStream, 0, 0);
            bitspersample = Math.Max(codecpar->bits_per_coded_sample, codecpar->bits_per_raw_sample);
            if (bitspersample == 0)
                bitspersample = 16;
            var pCodec = avcodec_find_decoder(codecpar->codec_id);
            if (pCodec == null)
            {
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
                throw new Exception($"No available codec for this file.");
            }

            codec = avcodec_alloc_context3(pCodec);
            ret = avcodec_open2(codec, pCodec, null);
            if (ret < 0)
            {
                fixed (AVCodecContext** p = &codec) avcodec_free_context(p);
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
                throw new Exception($"avcodec_open2 failed with {ret:X}");
            }

            // Try decode a sample to pop up decoder internal state.
            // The caller will seek back to start time later.
            ret = ReadPacketForDecode(out _);
            if (ret < 0)
            {
                fixed (AVCodecContext** p = &codec) avcodec_free_context(p);
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
                throw new Exception($"ReadPacketForDecode failed with {ret:X}");
            }

            SetResampleTarget(_sampleInfo ?? new PcmEncodingProperties());
        }

        private void ReleaseFfmpeg()
        {
            if (codec != null)
            {
                fixed (AVCodecContext** pcodec = &codec) avcodec_free_context(pcodec);
            }

            if (pFormatContext != null)
            {
                av_freep(&pFormatContext->pb->buffer);
                av_free(pFormatContext->pb);
                fixed (AVFormatContext** ppFormatContext = &pFormatContext)
                    avformat_close_input(ppFormatContext);
            }
        }

        private void CheckAndInitializeDelayedStream()
        {
            if (delayedStream && !delayedStreamOpened)
            {
                delayedStreamOpened = true;
                OpenFileForDelayedStream();
            }
        }
        private void OpenFileForDelayedStream()
        {
            OpenStream(_file.OpenRead());
        }

        private int ReadPacketForDecode(out long timestamp)
        {
            int ret = 0;
            timestamp = 0;
            AVPacket* p = av_packet_alloc();
            while (true)
            {
                ret = av_read_frame(pFormatContext, p);
                if (ret == AVERROR_EOF)
                {
                    av_packet_free(&p);
                    return avcodec_send_packet(codec, null);
                }
                else if (ret < 0)
                {
                    FfmpegLastError = ret;
                    av_packet_free(&p);
                    return avcodec_send_packet(codec, null);
                }

                if (p->stream_index == nAudioStream)
                {
                    break;
                }
                else
                {
                    av_packet_unref(p);
                }
            }

            timestamp = p->dts;
            ret = avcodec_send_packet(codec, p);
            av_packet_unref(p);
            av_packet_free(&p);
            return ret;
        }

        private long ReadAndDecodeInternal(out UnmanagedBuffer buffer)
        {
            long firstTimestamp = -1;
            AVFrame* f = av_frame_alloc();
            while (true)
            {
                int ret = avcodec_receive_frame(codec, f);
                if (ret == AVERROR_EOF)
                {
                    buffer = default;
                    av_frame_free(&f);
                    return -1;
                }
                else if (ret == AVERROR(EAGAIN))
                {
                    ret = ReadPacketForDecode(out var timestamp);
                    if (ret < 0)
                    {
                        FfmpegLastError = ret;
                        buffer = default;
                        av_frame_free(&f);
                        return -1;
                    }

                    if (firstTimestamp == -1)
                    {
                        firstTimestamp = timestamp;
                    }

                    continue;
                }
                else if (ret < 0)
                {
                    FfmpegLastError = ret;
                    buffer = default;
                    av_frame_free(&f);
                    return -1;
                }

                buffer = ResampleBuffer(f);
                av_frame_unref(f);
                av_frame_free(&f);
                return firstTimestamp;
            }
        }

        private UnmanagedBuffer ResampleBuffer(AVFrame* frame)
        {
            var delay = swr_get_delay(swr_ctx, codec->sample_rate);
            var dst_nb_samples = av_rescale_rnd(
                delay + frame->nb_samples,
                _sampleInfo.SampleRate,
                codec->sample_rate,
                AVRounding.AV_ROUND_UP);
            int bytes = (int)(dst_nb_samples * _sampleInfo.ChannelCount * (_sampleInfo.BitsPerSample == 24 ? 32 : _sampleInfo.BitsPerSample) / 8);
            byte* buf = (byte*)Marshal.AllocHGlobal(bytes);

            var ret = swr_convert(swr_ctx, &buf, (int)dst_nb_samples, frame->extended_data, frame->nb_samples);
            if (ret < 0)
            {
                Marshal.FreeHGlobal((IntPtr)buf);
                return new UnmanagedBuffer();
            }

            bytes = (int)(ret * (_sampleInfo.BitsPerSample == 24 ? 32 : _sampleInfo.BitsPerSample) * (_sampleInfo.ChannelCount == 0 ? codecpar->ch_layout.nb_channels : (int)_sampleInfo.ChannelCount) / 8);
            if (_sampleInfo.BitsPerSample == 24)
            {
                var tmpBytes = buf;
                var newBytes = bytes * 3 / 4;
                buf = (byte*)Marshal.AllocHGlobal(newBytes);
                for (int i = 0, j = 0; j < newBytes; i++)
                {
                    if ((i % 4) == 0)
                        continue;
                    buf[j] = tmpBytes[i];
                    j++;
                }
                Marshal.FreeHGlobal((IntPtr)tmpBytes);
                bytes = newBytes;
            }

            return new UnmanagedBuffer { Content = buf, Length = bytes };
        }
        #endregion

        #region members
        public long DecodedTicks = 0;
	    int FfmpegLastError = 0;
        long AudiofileDuration;
        PcmEncodingProperties _sampleInfo;
        FfmpegMediaInfo _info;
        long startOffsetTicks = 0;
        long audioDurationTicks = -1;
        bool delayedStream = false;
        bool delayedStreamOpened = false;
        Stream fileStream;
        object decode_lock = new object();
        Queue<UnmanagedBuffer> buffer_queue = new Queue<UnmanagedBuffer>();
        int nAudioStream;
        int bitspersample;
        bool _disposed = false;
        IDelayOpenFile _file;
        #endregion

        #region unmanaged
        GCHandle streamReadHandle;
        GCHandle streamSeekHandle;
        AVFormatContext* pFormatContext;
        SwrContext* swr_ctx;
        AVCodecParameters* codecpar;
        AVCodecContext* codec;

        AVChannelLayout out_ch_layout;
        #endregion
    }
}
