using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal interface IAudioOutputDevice : IDisposable
{
    AudioFormat Format { get; }

    AudioOutputState State { get; }

    float Volume { get; set; }

    TimeSpan BufferedDuration { get; }

    TimeSpan PlayedDuration { get; }

    int QueuedBufferCount { get; }

    int UnderrunCount { get; }

    event EventHandler<string>? BackendError;

    event EventHandler<string>? Underrun;

    void Start();

    void Pause();

    void Resume();

    void Stop();

    void Flush();

    void ResetAfterSeek();

    void QueueBuffer(ReadOnlySpan<byte> pcmBuffer);

    void Update();
}