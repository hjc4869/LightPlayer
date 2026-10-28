using System;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

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
            fftSize is < 2048 or > 32768 || fftSize % 2 != 0)
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

    internal float[] Stft(float[][] samples, CancellationToken cancellationToken)
    {
        var output = new float[4 * Frequencies * Frames];
        Parallel.For(0, 2, new ParallelOptions { CancellationToken = cancellationToken }, channel =>
        {
            var spectrum = new Complex32[fftSize];
            for (var frame = 0; frame < Frames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = frame * Hop - Trim;
                for (var index = 0; index < fftSize; index++)
                    spectrum[index] = new Complex32(samples[channel][ReflectIndex(start + index, ChunkSize)] * window[index], 0);
                Fourier.Forward(spectrum, FourierOptions.Matlab);
                for (var frequency = 0; frequency < Frequencies; frequency++)
                {
                    output[SpectrumIndex(channel * 2, frequency, frame)] = spectrum[frequency].Real;
                    output[SpectrumIndex(channel * 2 + 1, frequency, frame)] = spectrum[frequency].Imaginary;
                }
            }
        });
        return output;
    }

    internal float[][] Istft(float[] spectrogram, CancellationToken cancellationToken)
    {
        if (spectrogram.Length != 4 * Frequencies * Frames)
            throw new ArgumentException("Unexpected MDX output dimensions.", nameof(spectrogram));
        var output = new[] { new float[OutputFrames], new float[OutputFrames] };
        Parallel.For(0, 2, new ParallelOptions { CancellationToken = cancellationToken }, channel =>
        {
            var samples = new float[envelope.Length];
            var spectrum = new Complex32[fftSize];
            for (var frame = 0; frame < Frames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(spectrum);
                for (var frequency = 0; frequency < Frequencies; frequency++)
                    spectrum[frequency] = new Complex32(
                        spectrogram[SpectrumIndex(channel * 2, frequency, frame)],
                        spectrogram[SpectrumIndex(channel * 2 + 1, frequency, frame)]);
                for (var frequency = fftSize / 2 + 1; frequency < fftSize; frequency++)
                {
                    var mirror = spectrum[fftSize - frequency];
                    spectrum[frequency] = new Complex32(mirror.Real, -mirror.Imaginary);
                }
                Fourier.Inverse(spectrum, FourierOptions.Matlab);
                for (var index = 0; index < fftSize; index++)
                    samples[frame * Hop + index] += spectrum[index].Real * window[index];
            }
            for (var index = 0; index < OutputFrames; index++)
            {
                var padded = index + 2 * Trim;
                output[channel][index] = envelope[padded] > 1e-8f ? samples[padded] / envelope[padded] : 0;
            }
        });
        return output;
    }

    private int SpectrumIndex(int channel, int frequency, int frame) => (channel * Frequencies + frequency) * Frames + frame;

    private static int ReflectIndex(int index, int length)
    {
        while ((uint)index >= (uint)length)
            index = index < 0 ? -index : 2 * length - 2 - index;
        return index;
    }
}