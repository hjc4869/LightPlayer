using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

public interface IAudioSeparationInference
{
    Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture);
    IInferenceModelDownloads CreateDownloads(string modelDirectory,
        Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install);
    Task ValidateModelAsync(string path, CancellationToken cancellationToken);
    IAudioSeparationSession CreateSession(string path, string deviceId, bool residual, CancellationToken cancellationToken);
    Task<IAudioSeparationSession> CreateSessionAsync(string path, string deviceId, bool residual,
        CancellationToken cancellationToken) => Task.FromResult(CreateSession(path, deviceId, residual, cancellationToken));
    void ReleaseCachedSession() { }
}

/// <summary>Streams interleaved stereo float samples at <see cref="SampleRate"/>.</summary>
public interface IAudioSeparationSession : IDisposable
{
    int SampleRate { get; }
    int OutputBlockFrames { get; }
    string ActiveDeviceId { get; }
    bool IsFinished { get; }
    void Append(ReadOnlySpan<float> samples);
    void CompleteInput();
    int Read(Span<float> destination, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<float> destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Read(destination.Span, cancellationToken));
    void Reset();
}

public enum AudioSeparationTrack
{
    Original,
    First,
    Second,
}