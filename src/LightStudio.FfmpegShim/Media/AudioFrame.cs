using System;
using System.Collections.Concurrent;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;

namespace LightStudio.FfmpegShim.Media;

/// <summary>PCM layout the audio output device was opened with.</summary>
public readonly record struct AudioStreamFormat(int SampleRate, int ChannelCount, int BitsPerSample)
{
    public int BytesPerFrame => ChannelCount * (BitsPerSample / 8);

    public int ByteRate => SampleRate * BytesPerFrame;

    public TimeSpan DurationOf(int byteCount) =>
        ByteRate > 0 ? TimeSpan.FromSeconds((double)byteCount / ByteRate) : TimeSpan.Zero;

    public override string ToString() => $"{SampleRate} Hz, {ChannelCount} ch, {BitsPerSample}-bit";
}

/// <summary>A block of decoded interleaved PCM ready to be written to the audio device.</summary>
public sealed class AudioFrame : IMediaQueueItem
{
    private readonly AudioFramePool owner;

    internal AudioFrame(AudioFramePool owner) => this.owner = owner;

    /// <summary>An unpooled, empty block that terminates a run through the frame queue.</summary>
    public static AudioFrame CreateEndOfStreamMarker()
    {
        var marker = new AudioFrame(null);
        marker.Reset(endOfStream: true);
        return marker;
    }

    public byte[] Data { get; private set; } = Array.Empty<byte>();

    public MediaResourceLease Resource { get; private set; }

    public ReadOnlyMemory<byte> Buffer => Resource is { } resource
        ? resource.TryGetMemory(out ReadOnlyMemory<byte> memory) ? memory : default
        : Data.AsMemory(0, Length);

    public static AudioFrame CreateExternal(MediaResourceLease resource, double pts, double duration, int serial)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new AudioFrame(null)
        {
            Resource = resource,
            Length = resource.ByteLength,
            Pts = pts,
            Duration = duration,
            Serial = serial,
        };
    }

    public static AudioFrame CreateOwned(byte[] data, double pts, double duration, int serial)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new AudioFrame(null) { Data = data, Length = data.Length, Pts = pts, Duration = duration, Serial = serial };
    }

    public int Length { get; internal set; }

    /// <summary>Media timeline position of the first sample, in seconds.</summary>
    public double Pts { get; internal set; }

    /// <summary>Media timeline duration represented by this block, in seconds.</summary>
    public double Duration { get; internal set; }

    public int Serial { get; set; }

    public bool IsEndOfStream { get; internal set; }

    public int SizeInBytes => Length;

    public Span<byte> Span => Data.AsSpan(0, Length);

    internal void EnsureCapacity(int bytes)
    {
        if (Data.Length < bytes)
            Data = new byte[bytes];
    }

    internal void Reset(bool endOfStream = false)
    {
        Length = 0;
        Pts = MediaTime.NoTimestamp;
        Duration = 0;
        IsEndOfStream = endOfStream;
    }

    public void Recycle()
    {
        Resource?.Dispose();
        Resource = null;
        owner?.Return(this);
    }
}

public sealed class AudioFramePool
{
    private readonly ConcurrentBag<AudioFrame> idle = new();
    private readonly int maxIdle;

    public AudioFramePool(int maxIdle = 64) => this.maxIdle = maxIdle;

    public AudioFrame Rent(int minimumBytes)
    {
        if (!idle.TryTake(out var frame))
            frame = new AudioFrame(this);

        frame.Reset();
        frame.EnsureCapacity(minimumBytes);
        return frame;
    }

    /// <summary>A pooled block of <paramref name="length"/> bytes, which the caller fills through <see cref="AudioFrame.Span"/>.</summary>
    public AudioFrame Rent(int length, double pts, double duration, int serial)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        AudioFrame frame = Rent(length);
        frame.Length = length;
        frame.Pts = pts;
        frame.Duration = duration;
        frame.Serial = serial;
        return frame;
    }

    public AudioFrame RentEndOfStream()
    {
        if (!idle.TryTake(out var frame))
            frame = new AudioFrame(this);

        frame.Reset(endOfStream: true);
        return frame;
    }

    internal void Return(AudioFrame frame)
    {
        frame.Reset();
        if (idle.Count < maxIdle)
            idle.Add(frame);
    }

    public void Clear()
    {
        while (idle.TryTake(out _))
        {
        }
    }
}
