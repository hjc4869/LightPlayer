using System;
using System.IO;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// Pulls interleaved PCM out of one media file for the queue player. Demuxer and decoder are
/// driven synchronously from the caller's thread; this type owns no worker threads and no queue
/// beyond the single frame a seek may leave partially consumed.
/// </summary>
internal sealed class FfmpegPcmFrameSource : IPcmFrameSource
{
    private const int MaxConsecutiveFailures = 32;

    private readonly FfmpegDemuxer demuxer;
    private readonly FfmpegAudioDecoder decoder;
    private readonly MediaPacketPool packetPool = new(1);
    private readonly MediaPacket packet;
    private readonly int audioStreamIndex;

    private AudioFrame? pending;
    private int pendingOffset;
    private bool sentEndOfStream;
    private bool endOfStream;
    private double position;
    private string? lastError;

    public FfmpegPcmFrameSource(Stream mediaFile, TimeSpan startPosition, AudioFormat? outputFormat = null)
    {
        // The caller owns the stream for the whole track, so the demuxer must not close it.
        demuxer = FfmpegDemuxer.Open(mediaFile, name: null, leaveOpen: true);
        try
        {
            audioStreamIndex = demuxer.AudioStreamIndex;
            if (audioStreamIndex < 0)
            {
                throw new InvalidOperationException("The media file contains no audio stream.");
            }

            MediaStreamInfo? audio = demuxer.Info.DefaultAudioStream;
            Format = ResolveOutputFormat(outputFormat, audio);
            Duration = demuxer.Info.Duration > TimeSpan.Zero
                ? demuxer.Info.Duration
                : audio?.Duration ?? TimeSpan.Zero;
            Title = demuxer.Info.Metadata.TryGetValue("title", out var title) ? title : audio?.Title;

            decoder = new FfmpegAudioDecoder(
                demuxer,
                new AudioStreamFormat((int)Format.SampleRate, (int)Format.ChannelCount, (int)Format.BitsPerSample));
            decoder.Configure(decoder.OutputFormat, 1.0);
            packet = packetPool.Rent();

            if (startPosition > TimeSpan.Zero)
            {
                Seek(startPosition);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public AudioFormat Format { get; }

    /// <summary>Container title, falling back to the audio stream's own title.</summary>
    public string? Title { get; }

    public TimeSpan Duration { get; }

    public TimeSpan Position => position > 0 ? TimeSpan.FromSeconds(position) : TimeSpan.Zero;

    /// <summary>Last decode or demux failure, for diagnostics only; null while healthy.</summary>
    public string? LastError => lastError;

    public TimeSpan Seek(TimeSpan target)
    {
        double seconds = Math.Max(0, target.TotalSeconds);
        DropPending();

        if (!demuxer.Seek(seconds))
        {
            lastError = "seek failed";
            return Position;
        }

        decoder.Flush();
        sentEndOfStream = false;
        endOfStream = false;
        position = seconds;

        // libavformat lands on the packet at or before the target, so the first frames decoded
        // after a seek still belong to the past. Drop them and trim the one that straddles the
        // target, otherwise every seek would replay up to a full keyframe interval.
        while (true)
        {
            AudioFrame? frame = DecodeFrame();
            if (frame is null)
            {
                return Position;
            }

            if (!MediaTime.IsValid(frame.Pts))
            {
                pending = frame;
                pendingOffset = 0;
                return Position;
            }

            if (frame.Pts + frame.Duration <= seconds)
            {
                frame.Recycle();
                continue;
            }

            int skip = 0;
            if (frame.Pts < seconds)
            {
                skip = Format.AlignByteCount((int)((seconds - frame.Pts) * Format.ByteRate));
                skip = Math.Min(skip, frame.Length);
            }

            pending = frame;
            pendingOffset = skip;
            position = frame.Pts + Format.GetDuration(skip).TotalSeconds;
            return Position;
        }
    }

    public bool TryReadFrame(TimeSpan maxDuration, out PcmFrame? frame)
    {
        frame = ReadFrame(maxDuration);
        return true;
    }

    public PcmFrame? ReadFrame(TimeSpan maxDuration)
    {
        if (maxDuration <= TimeSpan.Zero)
        {
            return null;
        }

        AudioFrame? frame;
        int offset;
        if (pending is not null)
        {
            frame = pending;
            offset = pendingOffset;
            pending = null;
            pendingOffset = 0;
        }
        else
        {
            frame = DecodeFrame();
            offset = 0;
        }

        if (frame is null)
        {
            return null;
        }

        try
        {
            int available = frame.Length - offset;
            if (available <= 0)
            {
                return null;
            }

            TimeSpan frameDuration = Format.GetDuration(available);
            int bytesToCopy = GetBoundedByteCount(available, frameDuration, maxDuration);
            if (bytesToCopy <= 0)
            {
                return null;
            }

            AdvancePosition(frame.Pts, offset, bytesToCopy);

            var ready = new PcmFrame(
                frame.Data.AsMemory(offset, bytesToCopy),
                bytesToCopy == available ? frameDuration : Format.GetDuration(bytesToCopy),
                frame.Recycle);
            frame = null;
            return ready;
        }
        finally
        {
            frame?.Recycle();
        }
    }

    public void Dispose()
    {
        DropPending();
        decoder?.Dispose();
        if (packet is not null)
        {
            packetPool.Return(packet);
        }

        packetPool.Clear();
        demuxer?.Dispose();
    }

    /// <summary>
    /// Preserves the queue player's rate policy: an explicit request wins, otherwise the track
    /// keeps its own sample rate so the device is opened to match the file.
    /// </summary>
    private static AudioFormat ResolveOutputFormat(AudioFormat? requested, MediaStreamInfo? audio)
    {
        uint bits = requested?.BitsPerSample ?? 24;
        bits = bits <= 16 ? 16u : 24u;

        uint rate = requested?.SampleRate ?? 0;
        if (rate == 0)
        {
            rate = (uint)Math.Max(0, audio?.SampleRate ?? 0);
        }

        if (rate == 0)
        {
            rate = 48000;
        }

        // Every output backend accepts mono or stereo only; downmixing is the resampler's job.
        return new AudioFormat(rate, 2, bits);
    }

    private AudioFrame? DecodeFrame()
    {
        if (endOfStream)
        {
            return null;
        }

        int failures = 0;
        while (true)
        {
            switch (decoder.Receive(out AudioFrame frame))
            {
                case DecoderStatus.Ok:
                    return frame;

                case DecoderStatus.NeedMoreInput:
                    if (SendNextPacket())
                    {
                        continue;
                    }

                    endOfStream = true;
                    return null;

                case DecoderStatus.Recovered when ++failures <= MaxConsecutiveFailures:
                    continue;

                case DecoderStatus.Recovered:
                    lastError = "too many consecutive decode errors";
                    endOfStream = true;
                    return null;

                default:
                    endOfStream = true;
                    return null;
            }
        }
    }

    private bool SendNextPacket()
    {
        if (sentEndOfStream)
        {
            return false;
        }

        int failures = 0;
        while (true)
        {
            packet.Reset();
            PacketReadStatus read = demuxer.Read(packet);
            if (read == PacketReadStatus.Ok)
            {
                if (packet.StreamIndex != audioStreamIndex)
                {
                    continue;
                }

                return decoder.Send(packet) != DecoderStatus.Fatal;
            }

            if (read == PacketReadStatus.Recoverable && ++failures <= MaxConsecutiveFailures)
            {
                continue;
            }

            if (read != PacketReadStatus.EndOfFile)
            {
                lastError = $"packet read failed ({read})";
            }

            // Drain: the decoder still holds frames that belong before the end of the file.
            sentEndOfStream = true;
            packet.Reset();
            packet.MarkEndOfStream(audioStreamIndex);
            decoder.Send(packet);
            return true;
        }
    }

    private void AdvancePosition(double framePts, int offset, int bytes)
    {
        position = MediaTime.IsValid(framePts)
            ? framePts + Format.GetDuration(offset + bytes).TotalSeconds
            : position + Format.GetDuration(bytes).TotalSeconds;
    }

    private void DropPending()
    {
        pending?.Recycle();
        pending = null;
        pendingOffset = 0;
    }

    private int GetBoundedByteCount(int available, TimeSpan frameDuration, TimeSpan maxDuration)
    {
        if (frameDuration <= TimeSpan.Zero || frameDuration <= maxDuration)
        {
            return available;
        }

        var boundedBytes = checked((int)(available * maxDuration.Ticks / frameDuration.Ticks));
        return Format.AlignByteCount(boundedBytes);
    }
}

internal sealed class PcmFrame(ReadOnlyMemory<byte> data, TimeSpan duration, Action? release = null) : IDisposable
{
    private Action? release = release;

    public ReadOnlyMemory<byte> Data { get; private set; } = data;
    public TimeSpan Duration { get; private set; } = duration;

    public void Trim(int length, TimeSpan duration)
    {
        Data = Data[..length];
        Duration = duration;
    }

    public void Dispose() => System.Threading.Interlocked.Exchange(ref release, null)?.Invoke();
}
