using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal sealed class FfmpegPcmFrameSource : IDisposable
{
    private readonly FfmpegAudioReader reader;

    public FfmpegPcmFrameSource(string mediaFile, TimeSpan startPosition, AudioFormat? outputFormat = null)
    {
        MediaFile = Path.GetFullPath(mediaFile);
        reader = new FfmpegAudioReader(File.OpenRead(MediaFile));
        reader.SetResampleTarget(outputFormat?.ToPcmEncodingProperties() ?? new PcmEncodingProperties
        {
            ChannelCount = 2,
            BitsPerSample = 16
        });
        Metadata = reader.ReadMetadata();
        Format = AudioFormat.FromPcmEncodingProperties(reader.GetOutputAudioProperties());
        if (startPosition > TimeSpan.Zero)
        {
            Seek(startPosition);
        }
    }

    public string MediaFile { get; }

    public AudioFormat Format { get; }

    public FfmpegMediaInfo Metadata { get; }

    public TimeSpan Duration => reader.GetActualDuration();

    public TimeSpan Position => TimeSpan.FromTicks(reader.DecodedTicks);

    public int LastFfmpegError => reader.LastFfmpegError;

    public TimeSpan Seek(TimeSpan position)
    {
        return TimeSpan.FromTicks(reader.Seek(position.Ticks));
    }

    public PcmFrame? ReadFrame(TimeSpan maxDuration)
    {
        if (maxDuration <= TimeSpan.Zero)
        {
            return null;
        }

        using var buffer = reader.ReadAndDecodeFrame(out var frameTicks);
        if (buffer.Length == 0)
        {
            return null;
        }

        var bytesToCopy = GetBoundedByteCount(buffer.Length, frameTicks, maxDuration.Ticks);
        if (bytesToCopy <= 0)
        {
            return null;
        }

        var data = new byte[bytesToCopy];
        unsafe
        {
            new ReadOnlySpan<byte>(buffer.Content, bytesToCopy).CopyTo(data);
        }

        var duration = frameTicks > 0
            ? TimeSpan.FromTicks(Math.Min(frameTicks, maxDuration.Ticks))
            : Format.GetDuration(bytesToCopy);
        return new PcmFrame(data, duration);
    }

    public void Dispose()
    {
        reader.Dispose();
    }

    private int GetBoundedByteCount(int bufferLength, int frameTicks, long remainingTicks)
    {
        if (frameTicks <= 0 || frameTicks <= remainingTicks)
        {
            return bufferLength;
        }

        var boundedBytes = checked((int)(bufferLength * remainingTicks / frameTicks));
        return Format.AlignByteCount(boundedBytes);
    }
}

internal sealed record PcmFrame(byte[] Data, TimeSpan Duration);