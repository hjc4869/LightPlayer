using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;

namespace LightMediaRenderer.Avalonia.Audio;

/// <summary>
/// Supplies the next complete PCM block. <paramref name="requestedDuration"/> is a buffering hint;
/// the returned block may be longer when the decoder cannot split a frame without losing samples.
/// </summary>
public delegate AudioOutputReadResult AudioOutputPullCallback(TimeSpan requestedDuration);

public sealed record AudioOutputError(string Message, Exception? Cause = null);

public sealed record AudioPlayoutSnapshot(AudioTimelinePosition Position, TimeSpan PlayedDuration, double ObservedAt)
{
    public static AudioPlayoutSnapshot Empty { get; } = new(AudioTimelinePosition.None, TimeSpan.Zero, 0);
}

public readonly record struct AudioOutputReadResult(
    ReadOnlyMemory<byte> Buffer,
    TimeSpan Duration,
    bool EndOfStream,
    // Allows callback-driven backends to keep their native stream alive while the queue opens the
    // next track; false means this is the final source and no handoff wait is useful.
    bool NextSourceExpected = false,
    // Media timeline position of the first sample. Producers that only need a monotonic playback
    // position can leave this null; stamping it lets the device report where playout actually is,
    // which is what A/V synchronization follows.
    TimeSpan? MediaPosition = null,
    // Seek generation this block belongs to, so a stale reading can be told from a fresh one.
    int Serial = 0,
    MediaResourceHandle? Resource = null,
    int ResourceByteLength = 0)
{
    public int ByteLength => Buffer.IsEmpty ? ResourceByteLength : Buffer.Length;

    public static AudioOutputReadResult FromBuffer(ReadOnlyMemory<byte> buffer, TimeSpan duration) =>
        new(buffer, duration, false, false);

    public static AudioOutputReadResult FromBuffer(
        ReadOnlyMemory<byte> buffer,
        TimeSpan duration,
        TimeSpan mediaPosition,
        int serial = 0) =>
        new(buffer, duration, false, false, mediaPosition, serial);

    public static AudioOutputReadResult Complete =>
        new(ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, true, false);

    public static AudioOutputReadResult CompleteWithNextSource =>
        new(ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, true, true);
}

public interface IAudioOutputDevice : IDisposable, IAsyncDisposable
{
    AudioFormat Format { get; }

    /// <summary>The shared live configuration used by this device and the settings UI.</summary>
    IAudioOutputSettings Settings { get; }

    IReadOnlyList<AudioOutputDeviceInfo> GetDevices() => Settings.GetDevices();

    IReadOnlyDictionary<string, string> GetOptions() => Settings.GetOptions();

    void SetDevice(string deviceId) => Settings.SetDevice(deviceId);

    void SetOption(string key, string value) => Settings.SetOption(key, value);

    int DeviceSampleRate { get; }

    AudioOutputState State { get; }

    float Volume { get; set; }

    TimeSpan BufferedDuration { get; }

    TimeSpan PlayedDuration { get; }

    /// <summary>
    /// Where the audio now reaching the speakers sits on the media timeline. Only meaningful when
    /// the pull callback stamps its blocks with <see cref="AudioOutputReadResult.MediaPosition"/>;
    /// otherwise this stays <see cref="AudioTimelinePosition.None"/>.
    /// </summary>
    AudioTimelinePosition PlayedMediaPosition => AudioTimelinePosition.None;

    AudioPlayoutSnapshot PlayoutSnapshot => new(PlayedMediaPosition, PlayedDuration, MediaTime.Now);

    bool SourceExhausted { get; }

    bool IsDrained { get; }

    int UnderrunCount { get; }

    event EventHandler<AudioOutputError>? BackendError;

    event EventHandler<string>? Underrun;

    /// <summary>
    /// Raised when the backend observes its output volume changing outside the app (e.g. the user
    /// moving this stream's slider in the system mixer), reporting the new 0..1 level. Backends that
    /// cannot detect external changes never raise it, so the default implementation is a no-op.
    /// </summary>
    event EventHandler<float>? VolumeChanged { add { } remove { } }

    void Start(AudioOutputPullCallback pullAudio);

    void Pause();

    void Resume();

    void Stop();

    void ResetAfterSeek();

    ValueTask StartAsync(AudioOutputPullCallback pullAudio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start(pullAudio);
        return ValueTask.CompletedTask;
    }

    ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pause();
        return ValueTask.CompletedTask;
    }

    ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Resume();
        return ValueTask.CompletedTask;
    }

    ValueTask StopAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    ValueTask ResetAfterSeekAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetAfterSeek();
        return ValueTask.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class AudioOutputDeviceFactory
{
    private static Func<AudioFormat, TimeSpan, IAudioOutputDevice>? factory;
    private static Func<AudioFormat, TimeSpan, CancellationToken, ValueTask<IAudioOutputDevice>>? asyncFactory;

    public static ISystemSampleRateProvider SystemSampleRateProvider { get; private set; } =
        new UnknownSystemSampleRateProvider();

    public static IAudioOutputSettings Settings { get; private set; } =
        DefaultAudioOutputSettings.Instance;

    public static void Register(
        Func<AudioFormat, TimeSpan, IAudioOutputDevice> audioOutputFactory,
        ISystemSampleRateProvider? systemSampleRateProvider = null,
        IAudioOutputSettings? settings = null,
        Func<AudioFormat, TimeSpan, CancellationToken, ValueTask<IAudioOutputDevice>>? asyncAudioOutputFactory = null)
    {
        ArgumentNullException.ThrowIfNull(audioOutputFactory);
        factory = audioOutputFactory;
        asyncFactory = asyncAudioOutputFactory;
        SystemSampleRateProvider = systemSampleRateProvider ?? new UnknownSystemSampleRateProvider();
        Settings = settings ?? DefaultAudioOutputSettings.Instance;
    }

    public static IAudioOutputDevice Create(AudioFormat format, TimeSpan bufferTarget) =>
        factory?.Invoke(format, bufferTarget)
        ?? throw new InvalidOperationException("The application head must register an audio output device factory before starting playback.");

    public static ValueTask<IAudioOutputDevice> CreateAsync(
        AudioFormat format, TimeSpan bufferTarget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return asyncFactory is { } create
            ? create(format, bufferTarget, cancellationToken)
            : ValueTask.FromResult(Create(format, bufferTarget));
    }
}
