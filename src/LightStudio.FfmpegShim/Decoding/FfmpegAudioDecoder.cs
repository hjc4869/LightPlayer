using System;
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// Software audio decoder that resamples straight to the layout the audio device was opened with,
/// so the audio renderer only ever copies bytes.
/// </summary>
/// <remarks>
/// Playback rate is a time stretch rather than a resample: the samples always leave the resampler
/// at the device rate, and <see cref="AudioTimeStretcher"/> then lays them down at the requested
/// spacing. Speeding up therefore shortens the material without lifting its pitch. At exactly 1x
/// the stretcher is out of the chain and the resampler writes the device's own format directly.
/// </remarks>
public sealed unsafe partial class FfmpegAudioDecoder : IAudioDecoder
{
    private static long nextId;

    // A source whose timestamps jump further than this is a break rather than jitter, and the
    // stretcher is restarted instead of carrying its seam state across the gap.
    private const double DiscontinuityTolerance = 0.05;

    private readonly IPacketSource source;
    private readonly AudioFramePool pool;
    private readonly int streamIndex;
    private readonly long id;

    private AVCodecContext* codec;
    private AVFrame* frame;
    private SwrContext* resampler;
    private AVChannelLayout outputLayout;

    private AudioStreamFormat outputFormat;
    private double speed = 1.0;
    private AudioTimeStretcher stretcher;
    private float[] stretchScratch = Array.Empty<float>();
    private int pendingFrames;
    private double pendingPts;
    private int resamplerRate;
    private int inputRate;
    private int inputFormat = -1;
    private ulong inputLayoutHash;
    private double nextPts = MediaTime.NoTimestamp;
    private bool disposed;
    private long decodedFrames;

    public FfmpegAudioDecoder(IPacketSource packetSource, AudioStreamFormat requestedFormat)
        : this(packetSource, (packetSource ?? throw new ArgumentNullException(nameof(packetSource))).AudioStreamIndex, requestedFormat)
    {
    }

    /// <summary>Opens a named stream of <paramref name="packetSource"/>, for sources with several tracks.</summary>
    public FfmpegAudioDecoder(IPacketSource packetSource, int audioStreamIndex, AudioStreamFormat requestedFormat)
    {
        ArgumentNullException.ThrowIfNull(packetSource);

        id = Interlocked.Increment(ref nextId);
        source = packetSource;
        streamIndex = audioStreamIndex;
        if (streamIndex < 0)
            throw new InvalidOperationException("The media has no audio stream.");

        pool = new AudioFramePool();
        outputFormat = requestedFormat;

        CodecParameters description = packetSource.GetCodecParameters(streamIndex);
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} creating audio decoder: stream={streamIndex}; codec={description.CodecName}; " +
            $"sourceRate={description.SampleRate}; sourceChannels={description.ChannelCount}; " +
            $"requestedRate={requestedFormat.SampleRate}; requestedChannels={requestedFormat.ChannelCount}; " +
            $"requestedBits={requestedFormat.BitsPerSample}.");
        AVCodecParameters* parameters = description.ToNative();
        try
        {
            AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
            if (decoder == null)
                throw new FfmpegException($"No decoder for {description.CodecName}.", 0);

            CodecName = description.CodecName;
            codec = avcodec_alloc_context3(decoder);
            if (codec == null)
                throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

            FfmpegError.ThrowIfError(avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = description.NativeTimeBase();
            codec->thread_count = 1;

            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            frame = av_frame_alloc();
            if (frame == null)
                throw new FfmpegException("av_frame_alloc returned null", 0);

            SourceSampleRate = parameters->sample_rate;
            SourceChannelCount = parameters->ch_layout.nb_channels;
            AppLog.Information(
                "FFmpeg.Decode",
                $"#{id} opened audio decoder '{CodecNameOf(decoder)}': sourceRate={SourceSampleRate}; " +
                $"sourceChannels={SourceChannelCount}.");
        }
        finally
        {
            CodecParameters.FreeNative(&parameters);
        }
    }

    public int StreamIndex => streamIndex;

    public string CodecName { get; } = string.Empty;

    public int SourceSampleRate { get; }

    public int SourceChannelCount { get; }

    public AudioStreamFormat OutputFormat => outputFormat;

    public long DecodedFrameCount => Interlocked.Read(ref decodedFrames);

    public void Configure(AudioStreamFormat format, double newSpeed)
    {
        if (format.BitsPerSample is not (16 or 24))
            throw new ArgumentException("Only 16-bit or 24-bit output is supported by this decoder.", nameof(format));

        outputFormat = format;
        speed = newSpeed <= 0 ? 1.0 : newSpeed;

        // The resampler's output format depends on whether the stretcher is in the chain, so it has
        // to be rebuilt either way.
        ReleaseResampler();
        ConfigureStretcher();
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} configured audio output: rate={outputFormat.SampleRate}; channels={outputFormat.ChannelCount}; " +
            $"bits={outputFormat.BitsPerSample}; speed={speed:F3}; timeStretch={stretcher is not null}.");
    }

    public void Flush()
    {
        if (disposed)
            return;

        avcodec_flush_buffers(codec);

        // Samples still inside swr belong to the previous position; recreating it is cheaper than
        // reasoning about how much of its internal buffer is stale.
        ReleaseResampler();
        stretcher?.Reset();
        pendingFrames = 0;
        nextPts = MediaTime.NoTimestamp;
    }

    private void ConfigureStretcher()
    {
        if (Math.Abs(speed - 1.0) < 1e-6)
        {
            stretcher = null;
            pendingFrames = 0;
            return;
        }

        if (stretcher is null ||
            stretcher.SampleRate != outputFormat.SampleRate ||
            stretcher.Channels != outputFormat.ChannelCount)
        {
            stretcher = new AudioTimeStretcher(outputFormat.SampleRate, outputFormat.ChannelCount);
            pendingFrames = 0;
        }

        stretcher.Speed = speed;
    }

    public DecoderStatus Send(MediaPacket packet)
    {
        if (disposed)
            return DecoderStatus.Fatal;

        if (packet.IsEndOfStream)
        {
            int drain = avcodec_send_packet(codec, null);
            return drain < 0 && !FfmpegError.IsEndOfFile(drain) ? DecoderStatus.Recovered : DecoderStatus.Ok;
        }

        int ret = avcodec_send_packet(codec, packet.Handle);
        if (ret >= 0)
            return DecoderStatus.Ok;

        if (FfmpegError.IsTryAgain(ret))
            return DecoderStatus.NeedMoreInput;

        AppLog.Warning("FFmpeg.Decode", $"#{id} audio packet decode failed: {FfmpegError.Describe(ret)}.");
        return DecoderStatus.Recovered;
    }

    public DecoderStatus Receive(out AudioFrame decoded)
    {
        decoded = null;
        if (disposed)
            return DecoderStatus.Fatal;

        // The stretcher only emits once it holds a whole sequence, so a decoded frame does not
        // always become a block. Looping keeps that from being reported as a stall.
        while (true)
        {
            int ret = avcodec_receive_frame(codec, frame);
            if (FfmpegError.IsTryAgain(ret))
                return DecoderStatus.NeedMoreInput;

            if (FfmpegError.IsEndOfFile(ret))
                return DecoderStatus.EndOfStream;

            if (ret < 0)
            {
                AppLog.Warning("FFmpeg.Decode", $"#{id} audio frame decode failed: {FfmpegError.Describe(ret)}.");
                av_frame_unref(frame);
                return DecoderStatus.Recovered;
            }

            try
            {
                decoded = Resample(frame);
            }
            catch (FfmpegException exception)
            {
                AppLog.Warning("FFmpeg.Decode", $"#{id} audio resampling failed.", exception);
                return DecoderStatus.Recovered;
            }
            finally
            {
                av_frame_unref(frame);
            }

            if (decoded is null)
                continue;

            Interlocked.Increment(ref decodedFrames);
            return DecoderStatus.Ok;
        }
    }

    private AudioFrame Resample(AVFrame* input)
    {
        EnsureResampler(input);

        // Samples already buffered inside swr precede this frame, so the block we are about to
        // produce starts that much earlier than the frame's own timestamp. Asked for in input
        // samples, so the delay and the frame can be rescaled to the output rate together.
        long bufferedInputSamples = swr_get_delay(resampler, input->sample_rate);

        int maximumOutputSamples = (int)av_rescale_rnd(
            bufferedInputSamples + input->nb_samples,
            resamplerRate,
            input->sample_rate,
            AVRounding.AV_ROUND_UP);

        long timestamp = input->best_effort_timestamp != AV_NOPTS_VALUE ? input->best_effort_timestamp : input->pts;
        double framePts = source.ToSeconds(streamIndex, timestamp);
        double blockPts = MediaTime.IsValid(framePts)
            ? framePts - ((double)bufferedInputSamples / input->sample_rate)
            : MediaTime.NoTimestamp;

        return stretcher is null
            ? ResampleDirect(input, maximumOutputSamples, blockPts)
            : ResampleStretched(input, maximumOutputSamples, blockPts);
    }

    private AudioFrame ResampleDirect(AVFrame* input, int maximumOutputSamples, double blockPts)
    {
        bool pack24 = outputFormat.BitsPerSample == 24;
        int channels = outputFormat.ChannelCount;
        int bytesPerFrame = channels * (outputFormat.BitsPerSample / 8);

        // swr has no 24-bit packed format, so 24-bit output is resampled as S32 and narrowed in
        // place afterwards; the block has to be rented at that wider intermediate size.
        AudioFrame target = pool.Rent(maximumOutputSamples * channels * (pack24 ? sizeof(int) : sizeof(short)));

        int produced;
        fixed (byte* destination = target.Data)
        {
            byte** planes = stackalloc byte*[1];
            planes[0] = destination;
            produced = swr_convert(resampler, planes, maximumOutputSamples, input->extended_data, input->nb_samples);
            if (produced > 0 && pack24)
                PackS32ToS24(destination, produced * channels);
        }

        if (produced < 0)
        {
            target.Recycle();
            throw new FfmpegException("swr_convert failed", produced);
        }

        if (produced == 0)
        {
            target.Recycle();
            return null;
        }

        double startPts = MediaTime.IsValid(blockPts)
            ? blockPts
            : MediaTime.IsValid(nextPts) ? nextPts : 0;

        target.Length = produced * bytesPerFrame;
        target.Duration = (double)produced / resamplerRate;
        target.Pts = startPts;
        nextPts = startPts + target.Duration;
        return target;
    }

    /// <summary>
    /// Resamples to float at the device rate, stretches the result to the requested playback rate,
    /// then writes it out in the device's own sample format.
    /// </summary>
    private AudioFrame ResampleStretched(AVFrame* input, int maximumOutputSamples, double blockPts)
    {
        int channels = outputFormat.ChannelCount;
        int scratchLength = maximumOutputSamples * channels;
        if (stretchScratch.Length < scratchLength)
            stretchScratch = new float[scratchLength];

        int produced;
        fixed (float* destination = stretchScratch)
        {
            byte** planes = stackalloc byte*[1];
            planes[0] = (byte*)destination;
            produced = swr_convert(resampler, planes, maximumOutputSamples, input->extended_data, input->nb_samples);
        }

        if (produced < 0)
            throw new FfmpegException("swr_convert failed", produced);

        if (produced == 0)
            return null;

        AnchorStretcher(blockPts);

        // What comes out stands for the media the stretcher took in, so the block is timed by how
        // much input it swallowed rather than by how many samples it wrote.
        double startPts = pendingPts;
        long consumedBefore = stretcher.ConsumedFrames;
        stretcher.Process(stretchScratch.AsSpan(0, produced * channels));
        pendingFrames += produced;

        long consumed = stretcher.ConsumedFrames - consumedBefore;
        pendingFrames -= (int)consumed;
        pendingPts += (double)consumed / resamplerRate;

        int emitted = stretcher.OutputFrames;
        if (emitted == 0)
            return null;

        bool pack24 = outputFormat.BitsPerSample == 24;
        int bytesPerFrame = channels * (outputFormat.BitsPerSample / 8);
        AudioFrame target = pool.Rent(emitted * bytesPerFrame);

        ReadOnlySpan<float> stretched = stretcher.Output;
        Span<byte> output = target.Data.AsSpan(0, emitted * bytesPerFrame);
        if (pack24)
            WriteS24(stretched, output);
        else
            WriteS16(stretched, output);

        stretcher.ClearOutput();

        target.Length = emitted * bytesPerFrame;
        target.Duration = (double)consumed / resamplerRate;
        target.Pts = startPts;
        nextPts = startPts + target.Duration;
        return target;
    }

    /// <summary>Places the samples about to be fed on the media timeline, restarting the stretcher
    /// when they do not continue the ones already inside it.</summary>
    private void AnchorStretcher(double blockPts)
    {
        if (pendingFrames > 0)
        {
            double expected = pendingPts + ((double)pendingFrames / resamplerRate);
            if (!MediaTime.IsValid(blockPts) || Math.Abs(blockPts - expected) <= DiscontinuityTolerance)
                return;

            stretcher.Reset();
            pendingFrames = 0;
        }

        pendingPts = MediaTime.IsValid(blockPts)
            ? blockPts
            : MediaTime.IsValid(nextPts) ? nextPts : 0;
    }

    private static void WriteS16(ReadOnlySpan<float> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            int sample = Math.Clamp((int)MathF.Round(source[i] * 32768f), short.MinValue, short.MaxValue);
            destination[i * 2] = (byte)sample;
            destination[(i * 2) + 1] = (byte)(sample >> 8);
        }
    }

    private static void WriteS24(ReadOnlySpan<float> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            int sample = Math.Clamp((int)MathF.Round(source[i] * 8388608f), -8388608, 8388607);
            destination[i * 3] = (byte)sample;
            destination[(i * 3) + 1] = (byte)(sample >> 8);
            destination[(i * 3) + 2] = (byte)(sample >> 16);
        }
    }

    private void EnsureResampler(AVFrame* input)
    {
        AVChannelLayout* inputLayout = &input->ch_layout;
        ulong layoutHash = HashLayout(inputLayout);
        int targetRate = outputFormat.SampleRate;

        if (resampler != null &&
            inputRate == input->sample_rate &&
            inputFormat == input->format &&
            inputLayoutHash == layoutHash &&
            resamplerRate == targetRate)
        {
            return;
        }

        ReleaseResampler();

        AVSampleFormat sampleFormat;
        if (stretcher is not null)
            sampleFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;
        else if (outputFormat.BitsPerSample == 24)
            sampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S32;
        else
            sampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;

        fixed (AVChannelLayout* layout = &outputLayout)
        fixed (SwrContext** context = &resampler)
        {
            av_channel_layout_default(layout, outputFormat.ChannelCount);
            FfmpegError.ThrowIfError(
                swr_alloc_set_opts2(
                    context,
                    layout,
                    sampleFormat,
                    targetRate,
                    inputLayout,
                    (AVSampleFormat)input->format,
                    input->sample_rate,
                    0,
                    null),
                "swr_alloc_set_opts2");
        }

        FfmpegError.ThrowIfError(swr_init(resampler), "swr_init");

        inputRate = input->sample_rate;
        inputFormat = input->format;
        inputLayoutHash = layoutHash;
        resamplerRate = targetRate;
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} configured audio resampler: inputRate={inputRate}; " +
            $"inputFormat={av_get_sample_fmt_name((AVSampleFormat)inputFormat)}; " +
            $"inputChannels={inputLayout->nb_channels}; outputRate={targetRate}; " +
            $"outputChannels={outputFormat.ChannelCount}; outputFormat={av_get_sample_fmt_name(sampleFormat)}.");
    }

    private static string CodecNameOf(AVCodec* decoder) =>
        decoder == null || decoder->name == null ? "unknown" : new string((sbyte*)decoder->name);

    private static ulong HashLayout(AVChannelLayout* layout) =>
        ((ulong)layout->nb_channels << 40) ^ ((ulong)layout->order << 32) ^ layout->u.mask;

    /// <summary>
    /// Narrows interleaved S32 to packed S24 by dropping each sample's low byte. Runs in place:
    /// sample <c>i</c> writes bytes 3i..3i+2 after reading 4i+1..4i+3, so the write never reaches
    /// a source byte that has not been read yet.
    /// </summary>
    private static void PackS32ToS24(byte* buffer, int sampleCount)
    {
        byte* src = buffer;
        byte* dst = buffer;
        for (int i = 0; i < sampleCount; i++)
        {
            dst[0] = src[1];
            dst[1] = src[2];
            dst[2] = src[3];
            src += 4;
            dst += 3;
        }
    }

    private void ReleaseResampler()
    {
        if (resampler != null)
        {
            fixed (SwrContext** context = &resampler)
                swr_free(context);
            resampler = null;
        }

        fixed (AVChannelLayout* layout = &outputLayout)
            av_channel_layout_uninit(layout);

        inputRate = 0;
        inputFormat = -1;
        inputLayoutHash = 0;
        resamplerRate = 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        ReleaseResampler();

        if (frame != null)
        {
            AVFrame* handle = frame;
            av_frame_free(&handle);
            frame = null;
        }

        if (codec != null)
        {
            AVCodecContext* handle = codec;
            avcodec_free_context(&handle);
            codec = null;
        }

        pool.Clear();
    }
}
