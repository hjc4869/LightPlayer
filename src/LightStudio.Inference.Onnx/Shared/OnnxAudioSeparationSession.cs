using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.Inference;

internal interface IMdxSeparationModel : IDisposable
{
    string ActiveDeviceId { get; }
    MdxProfile Profile { get; }
    ConvTdfNet Transform { get; }
    void AddReference();
    ValueTask<float[]> ExecuteAsync(float[] spectrum, CancellationToken cancellationToken);
}

internal sealed class OnnxAudioSeparationSession : IAudioSeparationSession
{
    private readonly IMdxSeparationModel model;
    private readonly bool residual;
    private readonly ConvTdfNet transform;
    private readonly float[] input;
    private int inputFrames;
    private long inputBase;
    private long totalFrames;
    private long nextStart;
    private bool ended;
    private bool disposed;
    private float[] output = [];
    private int outputCursor;

    internal OnnxAudioSeparationSession(IMdxSeparationModel model, bool residual)
    {
        this.model = model;
        this.residual = residual;
        transform = model.Transform;
        input = new float[transform.ChunkSize * 4];
        model.AddReference();
    }

    public int SampleRate => 44100;
    public int OutputBlockFrames => transform.OutputFrames;
    public string ActiveDeviceId => model.ActiveDeviceId;
    public bool IsFinished => ended && nextStart >= totalFrames && outputCursor >= output.Length;

    public void Append(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ended || samples.Length % 2 != 0 || samples.Length > input.Length - inputFrames * 2)
            throw new InvalidOperationException("Invalid separation input buffer state.");
        samples.CopyTo(input.AsSpan(inputFrames * 2));
        inputFrames += samples.Length / 2;
        totalFrames += samples.Length / 2;
    }

    public void CompleteInput()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ended = true;
    }

    public int Read(Span<float> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length < 2)
            return 0;
        if (outputCursor >= output.Length && !ProcessNextAsync(cancellationToken).GetAwaiter().GetResult())
            return 0;
        return CopyOutput(destination);
    }

    public async ValueTask<int> ReadAsync(Memory<float> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length < 2)
            return 0;
        if (outputCursor >= output.Length && !await ProcessNextAsync(cancellationToken).ConfigureAwait(false))
            return 0;
        return CopyOutput(destination.Span);
    }

    private int CopyOutput(Span<float> destination)
    {
        var samples = Math.Min(destination.Length / 2 * 2, output.Length - outputCursor);
        output.AsSpan(outputCursor, samples).CopyTo(destination);
        outputCursor += samples;
        return samples / 2;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        inputFrames = 0;
        inputBase = 0;
        totalFrames = 0;
        nextStart = 0;
        ended = false;
        output = [];
        outputCursor = 0;
    }

    private async ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (ended && nextStart >= totalFrames || !ended && totalFrames < nextStart + transform.OutputFrames + transform.Trim)
            return false;
        var window = new[] { new float[transform.ChunkSize], new float[transform.ChunkSize] };
        for (var index = 0; index < transform.ChunkSize; index++)
        {
            var relative = nextStart - transform.Trim + index - inputBase;
            if (relative < 0 || relative >= inputFrames)
                continue;
            window[0][index] = input[relative * 2];
            window[1][index] = input[relative * 2 + 1];
        }
        var spectrum = transform.Stft(window, cancellationToken);
        var prediction = await model.ExecuteAsync(spectrum, cancellationToken).ConfigureAwait(false);
        if (prediction.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("The separation model returned non-finite samples.");
        var separated = transform.Istft(prediction, cancellationToken);
        var frames = (int)Math.Min(transform.OutputFrames, totalFrames - nextStart);
        output = new float[frames * 2];
        outputCursor = 0;
        for (var index = 0; index < frames; index++)
            for (var channel = 0; channel < 2; channel++)
            {
                var sample = separated[channel][index] * model.Profile.Gain;
                output[index * 2 + channel] = residual ? window[channel][index + transform.Trim] - sample : sample;
            }
        nextStart += frames;
        var remove = (int)Math.Clamp(nextStart - transform.Trim - inputBase, 0, inputFrames);
        Array.Copy(input, remove * 2, input, 0, (inputFrames - remove) * 2);
        inputFrames -= remove;
        inputBase += remove;
        return frames > 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        model.Dispose();
    }
}