using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.Inference;

internal sealed class ConvTdfNet
{
    private readonly float[] window;
    private readonly float[] envelope;
    private readonly int fftSize;
    private const int Hop = 1024;

    internal ConvTdfNet(int frequencies, int frames, int fftSize)
    {
        if (frequencies < 1 || frequencies > fftSize / 2 + 1 || frames < 2 || frames > 1024 ||
            fftSize is < 2048 or > 32768 || fftSize % 4 != 0)
            throw new ArgumentException("Unsupported MDX transform dimensions.");
        Frequencies = frequencies;
        Frames = frames;
        this.fftSize = fftSize;
        ChunkSize = Hop * (frames - 1);
        if (ChunkSize <= fftSize)
            throw new ArgumentException("The MDX window is shorter than its context.");
        window = new float[fftSize];
        for (var index = 0; index < fftSize; index++)
            window[index] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * index / fftSize);
        envelope = new float[fftSize + ChunkSize];
        for (var frame = 0; frame < frames; frame++)
            for (var index = 0; index < fftSize; index++)
                envelope[frame * Hop + index] += window[index] * window[index];
    }

    internal int Frequencies { get; }
    internal int Frames { get; }
    internal int ChunkSize { get; }
    internal int Trim => fftSize / 2;
    internal int OutputFrames => ChunkSize - fftSize;

    internal Worker CreateWorker() => new(this);

    private int SpectrumIndex(int channel, int frequency, int frame) => (channel * Frequencies + frequency) * Frames + frame;

    private static int ReflectIndex(int index, int length)
    {
        while ((uint)index >= (uint)length)
            index = index < 0 ? -index : 2 * length - 2 - index;
        return index;
    }

    /// <summary>Transform state of one streaming session: FFmpeg real FFT contexts and buffers reused across chunks.</summary>
    internal sealed unsafe class Worker : IDisposable
    {
        private readonly ConvTdfNet net;
        private readonly Channel[] channels = new Channel[2];
        private readonly float[] spectrum;
        private readonly float[][] output;
        private readonly float[][] accumulators;
        private bool disposed;

        internal Worker(ConvTdfNet net)
        {
            this.net = net;
            spectrum = new float[4 * net.Frequencies * net.Frames];
            output = [new float[net.OutputFrames], new float[net.OutputFrames]];
            accumulators = [new float[net.envelope.Length], new float[net.envelope.Length]];
            try
            {
                for (var channel = 0; channel < 2; channel++)
                    channels[channel] = Channel.Create(net.fftSize);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Returns the [1, 4, frequencies, frames] spectrogram in a buffer the next call overwrites.</summary>
        internal float[] Stft(float[][] samples, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Parallel.For(0, 2, new ParallelOptions { CancellationToken = cancellationToken }, channel =>
            {
                var state = channels[channel];
                var frame = state.Frame;
                var bins = state.Bins;
                var source = samples[channel];
                for (var index = 0; index < net.Frames; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var start = index * Hop - net.Trim;
                    for (var sample = 0; sample < net.fftSize; sample++)
                        frame[sample] = source[ReflectIndex(start + sample, net.ChunkSize)] * net.window[sample];
                    state.Forward(state.ForwardContext, bins, frame, sizeof(float));
                    for (var frequency = 0; frequency < net.Frequencies; frequency++)
                    {
                        spectrum[net.SpectrumIndex(channel * 2, frequency, index)] = bins[frequency * 2];
                        spectrum[net.SpectrumIndex(channel * 2 + 1, frequency, index)] = bins[frequency * 2 + 1];
                    }
                }
            });
            return spectrum;
        }

        /// <summary>Returns the separated channels in buffers the next call overwrites.</summary>
        internal float[][] Istft(float[] spectrogram, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (spectrogram.Length != spectrum.Length)
                throw new ArgumentException("Unexpected MDX output dimensions.", nameof(spectrogram));
            var bins = net.fftSize / 2 + 1;
            Parallel.For(0, 2, new ParallelOptions { CancellationToken = cancellationToken }, channel =>
            {
                var state = channels[channel];
                var frame = state.Frame;
                var binBuffer = state.Bins;
                var samples = accumulators[channel];
                Array.Clear(samples);
                for (var index = 0; index < net.Frames; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var frequency = 0; frequency < net.Frequencies; frequency++)
                    {
                        binBuffer[frequency * 2] = spectrogram[net.SpectrumIndex(channel * 2, frequency, index)];
                        binBuffer[frequency * 2 + 1] = spectrogram[net.SpectrumIndex(channel * 2 + 1, frequency, index)];
                    }
                    NativeMemory.Clear(binBuffer + net.Frequencies * 2, (nuint)(bins - net.Frequencies) * 2 * sizeof(float));
                    state.Inverse(state.InverseContext, frame, binBuffer, 2 * sizeof(float));
                    var offset = index * Hop;
                    for (var sample = 0; sample < net.fftSize; sample++)
                        samples[offset + sample] += frame[sample] * net.window[sample];
                }
                var result = output[channel];
                for (var index = 0; index < result.Length; index++)
                {
                    var padded = index + 2 * net.Trim;
                    result[index] = net.envelope[padded] > 1e-8f ? samples[padded] / net.envelope[padded] : 0;
                }
            });
            return output;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            for (var channel = 0; channel < 2; channel++)
                channels[channel].Release();
        }

        private struct Channel
        {
            public AVTXContext* ForwardContext;
            public AVTXContext* InverseContext;
            public delegate* unmanaged[Cdecl]<AVTXContext*, void*, void*, nint, void> Forward;
            public delegate* unmanaged[Cdecl]<AVTXContext*, void*, void*, nint, void> Inverse;
            public float* Frame;
            public float* Bins;

            public static Channel Create(int fftSize)
            {
                var channel = new Channel();
                var forwardScale = 1f;
                var inverseScale = 1f / fftSize;
                try
                {
                    FfmpegError.ThrowIfError(av_tx_init(&channel.ForwardContext, &channel.Forward, AVTXType.AV_TX_FLOAT_RDFT,
                        0, fftSize, &forwardScale, 0), "av_tx_init");
                    FfmpegError.ThrowIfError(av_tx_init(&channel.InverseContext, &channel.Inverse, AVTXType.AV_TX_FLOAT_RDFT,
                        1, fftSize, &inverseScale, 0), "av_tx_init");
                    channel.Frame = (float*)NativeMemory.AlignedAlloc((nuint)fftSize * sizeof(float), 64);
                    channel.Bins = (float*)NativeMemory.AlignedAlloc((nuint)(fftSize / 2 + 1) * 2 * sizeof(float), 64);
                }
                catch
                {
                    channel.Release();
                    throw;
                }
                return channel;
            }

            public void Release()
            {
                fixed (AVTXContext** context = &ForwardContext)
                    av_tx_uninit(context);
                fixed (AVTXContext** context = &InverseContext)
                    av_tx_uninit(context);
                NativeMemory.AlignedFree(Frame);
                NativeMemory.AlignedFree(Bins);
                Frame = null;
                Bins = null;
            }
        }
    }
}
