using System;
using LightStudio.NativeBindings.Types;
using LightStudio.FfmpegShim.Interop;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim;

public sealed unsafe partial class FfmpegPcmResampler : IDisposable
{
    private readonly int inputRate;
    private readonly int outputRate;
    private SwrContext* context;
    private bool disposed;

    public FfmpegPcmResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        this.inputRate = inputRate;
        this.outputRate = outputRate;
        Reset();
    }

    public byte[] Convert(ReadOnlySpan<float> stereoSamples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stereoSamples.Length % 2 != 0)
            throw new ArgumentException("Expected interleaved stereo samples.", nameof(stereoSamples));
        var frames = stereoSamples.Length / 2;
        var capacity = checked((int)av_rescale_rnd(swr_get_delay(context, inputRate) + frames,
            outputRate, inputRate, AVRounding.AV_ROUND_UP) + 32);
        var data = new byte[checked(capacity * 2 * sizeof(int))];
        int produced;
        fixed (float* source = stereoSamples)
        fixed (byte* destination = data)
        {
            byte** inputs = stackalloc byte*[1];
            inputs[0] = (byte*)source;
            byte** outputs = stackalloc byte*[1];
            outputs[0] = destination;
            produced = swr_convert(context, outputs, capacity, frames == 0 ? null : inputs, frames);
            FfmpegError.ThrowIfError(produced, "swr_convert");
            for (var index = 0; index < produced * 2; index++)
            {
                destination[index * 3] = destination[index * 4 + 1];
                destination[index * 3 + 1] = destination[index * 4 + 2];
                destination[index * 3 + 2] = destination[index * 4 + 3];
            }
        }
        Array.Resize(ref data, produced * 6);
        return data;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        fixed (SwrContext** pointer = &context)
            swr_free(pointer);
        AVChannelLayout layout = default;
        av_channel_layout_default(&layout, 2);
        try
        {
            fixed (SwrContext** pointer = &context)
                FfmpegError.ThrowIfError(swr_alloc_set_opts2(pointer, &layout, AVSampleFormat.AV_SAMPLE_FMT_S32,
                    outputRate, &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, inputRate, 0, null), "swr_alloc_set_opts2");
            FfmpegError.ThrowIfError(swr_init(context), "swr_init");
        }
        catch
        {
            fixed (SwrContext** pointer = &context)
                swr_free(pointer);
            throw;
        }
        finally
        {
            av_channel_layout_uninit(&layout);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        fixed (SwrContext** pointer = &context)
            swr_free(pointer);
    }
}