using LightStudio.FfmpegShim;
using OpenTK.Audio.OpenAL;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal sealed class OpenAlAudioOutputDevice : IAudioOutputDevice
{
    private readonly ALDevice device;
    private readonly ALContext context;
    private readonly int source;
    private readonly ALFormat alFormat;
    private readonly Dictionary<int, int> queuedBuffers = new();
    private bool disposed;
    private long bufferedBytes;
    private long playedBytes;
    private float volume = 1.0f;

    public OpenAlAudioOutputDevice(AudioFormat format)
    {
        Format = format;
        alFormat = ToAlFormat(format);

        try
        {
            device = ALC.OpenDevice(null);
            if (device == ALDevice.Null)
            {
                throw new InvalidOperationException("OpenAL could not open the default playback device.");
            }

            context = ALC.CreateContext(device, new[] { 0 });
            if (context == ALContext.Null)
            {
                var error = ALC.GetError(device);
                ALC.CloseDevice(device);
                throw new InvalidOperationException($"OpenAL could not create a playback context: {error}");
            }

            if (!ALC.MakeContextCurrent(context))
            {
                ALC.DestroyContext(context);
                ALC.CloseDevice(device);
                throw new InvalidOperationException("OpenAL could not make the playback context current.");
            }

            source = AL.GenSource();
            CheckAl("generate source");
            Volume = 1.0f;
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("OpenAL native library was not found. Install OpenAL Soft, such as libopenal1/openal-soft on Linux, OpenAL.framework or openal-soft on macOS, or OpenAL32.dll/openal-soft on Windows.", ex);
        }
    }

    public AudioFormat Format { get; }

    public AudioOutputState State { get; private set; } = AudioOutputState.Stopped;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0.0f, 1.0f);
            AL.Source(source, ALSourcef.Gain, volume);
            CheckAl("set source gain");
        }
    }

    public TimeSpan BufferedDuration => Format.GetDuration(bufferedBytes);

    public TimeSpan PlayedDuration
    {
        get
        {
            var baseDuration = Format.GetDuration(playedBytes);
            if (State != AudioOutputState.Playing)
            {
                return baseDuration;
            }

            var offsetSeconds = AL.GetSource(source, ALSourcef.SecOffset);
            return baseDuration + TimeSpan.FromSeconds(Math.Max(0, offsetSeconds));
        }
    }

    public int QueuedBufferCount => queuedBuffers.Count;

    public int UnderrunCount { get; private set; }

    public event EventHandler<string>? BackendError;

    public event EventHandler<string>? Underrun;

    public void Start()
    {
        if (QueuedBufferCount == 0)
        {
            State = AudioOutputState.Playing;
            return;
        }

        AL.SourcePlay(source);
        CheckAl("start source");
        State = AudioOutputState.Playing;
    }

    public void Pause()
    {
        if (State != AudioOutputState.Playing)
        {
            return;
        }

        AL.SourcePause(source);
        CheckAl("pause source");
        State = AudioOutputState.Paused;
    }

    public void Resume()
    {
        if (State != AudioOutputState.Paused)
        {
            return;
        }

        AL.SourcePlay(source);
        CheckAl("resume source");
        State = AudioOutputState.Playing;
    }

    public void Stop()
    {
        AL.SourceStop(source);
        CheckAl("stop source");
        State = AudioOutputState.Stopped;
        Flush();
    }

    public void Flush()
    {
        AL.SourceStop(source);
        CheckAl("stop source before flush");
        UnqueueAllBuffers();
        bufferedBytes = 0;
    }

    public void ResetAfterSeek()
    {
        Flush();
        playedBytes = 0;
        State = AudioOutputState.Stopped;
    }

    public void QueueBuffer(ReadOnlySpan<byte> pcmBuffer)
    {
        if (pcmBuffer.Length == 0)
        {
            return;
        }

        var buffer = AL.GenBuffer();
        CheckAl("generate buffer");
        AL.BufferData(buffer, alFormat, pcmBuffer, checked((int)Format.SampleRate));
        CheckAl("fill buffer");
        AL.SourceQueueBuffer(source, buffer);
        CheckAl("queue buffer");

        queuedBuffers[buffer] = pcmBuffer.Length;
        bufferedBytes += pcmBuffer.Length;

        if (State == AudioOutputState.Playing && GetSourceState() != ALSourceState.Playing)
        {
            AL.SourcePlay(source);
            CheckAl("restart source after queue");
        }
    }

    public void Update()
    {
        ReclaimProcessedBuffers();

        if (State == AudioOutputState.Playing && QueuedBufferCount > 0 && GetSourceState() == ALSourceState.Stopped)
        {
            UnderrunCount++;
            var message = $"OpenAL source stopped while {QueuedBufferCount} buffer(s) were still queued.";
            Underrun?.Invoke(this, message);
            AL.SourcePlay(source);
            CheckAl("restart source after underrun");
        }

        var error = AL.GetError();
        if (error != ALError.NoError)
        {
            BackendError?.Invoke(this, $"OpenAL backend error: {error} ({AL.GetErrorString(error)})");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        AL.DeleteSource(source);
        ALC.MakeContextCurrent(ALContext.Null);
        ALC.DestroyContext(context);
        ALC.CloseDevice(device);
        disposed = true;
    }

    private void ReclaimProcessedBuffers()
    {
        var processed = AL.GetSource(source, ALGetSourcei.BuffersProcessed);
        CheckAl("get processed buffer count");
        for (var i = 0; i < processed; i++)
        {
            var buffer = AL.SourceUnqueueBuffer(source);
            CheckAl("unqueue processed buffer");
            if (queuedBuffers.Remove(buffer, out var bytes))
            {
                bufferedBytes -= bytes;
                playedBytes += bytes;
            }
            AL.DeleteBuffer(buffer);
            CheckAl("delete processed buffer");
        }
    }

    private void UnqueueAllBuffers()
    {
        var queued = AL.GetSource(source, ALGetSourcei.BuffersQueued);
        CheckAl("get queued buffer count");
        for (var i = 0; i < queued; i++)
        {
            var buffer = AL.SourceUnqueueBuffer(source);
            CheckAl("unqueue buffer");
            queuedBuffers.Remove(buffer);
            AL.DeleteBuffer(buffer);
            CheckAl("delete buffer");
        }
        queuedBuffers.Clear();
    }

    private ALSourceState GetSourceState()
    {
        var state = (ALSourceState)AL.GetSource(source, ALGetSourcei.SourceState);
        CheckAl("get source state");
        return state;
    }

    private static ALFormat ToAlFormat(AudioFormat format)
    {
        return (format.ChannelCount, format.BitsPerSample) switch
        {
            (1, 8) => ALFormat.Mono8,
            (1, 16) => ALFormat.Mono16,
            (2, 8) => ALFormat.Stereo8,
            (2, 16) => ALFormat.Stereo16,
            _ => throw new NotSupportedException($"OpenAL playback supports mono/stereo 8-bit or 16-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.")
        };
    }

    private static void CheckAl(string operation)
    {
        var error = AL.GetError();
        if (error != ALError.NoError)
        {
            throw new InvalidOperationException($"OpenAL {operation} failed: {error} ({AL.GetErrorString(error)})");
        }
    }
}