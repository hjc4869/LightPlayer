using System;
using System.Diagnostics;
using System.IO;

namespace LightStudio.FfmpegShim
{
    public sealed class FfmpegAudioDecoder : IDisposable
    {
        private readonly FfmpegAudioReader reader;

        public FfmpegAudioDecoder(Stream stream, AudioFormat? requestedFormat = null)
        {
            reader = new FfmpegAudioReader(stream);
            if (requestedFormat.HasValue)
            {
                reader.SetResampleTarget(requestedFormat.Value.ToPcmEncodingProperties());
            }

            Format = AudioFormat.FromPcmEncodingProperties(reader.GetOutputAudioProperties());
        }

        public AudioFormat Format { get; private set; }

        public TimeSpan Duration => reader.GetActualDuration();

        public TimeSpan DecodedPosition => TimeSpan.FromTicks(reader.DecodedTicks);

        public int LastFfmpegError => reader.LastFfmpegError;

        public FfmpegMediaInfo ReadMetadata()
        {
            var metadata = reader.ReadMetadata();
            Format = AudioFormat.FromPcmEncodingProperties(reader.GetOutputAudioProperties());
            return metadata;
        }

        public TimeSpan Seek(TimeSpan position)
        {
            return TimeSpan.FromTicks(reader.Seek(position.Ticks));
        }

        public AudioDecodeResult Decode(TimeSpan duration, Stream pcmOutput)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Decode duration must be greater than zero.");
            }

            var stopwatch = Stopwatch.StartNew();
            long decodedBytes = 0;
            long decodedTicks = 0;
            var frameCount = 0;

            while (decodedTicks < duration.Ticks)
            {
                using var buffer = reader.ReadAndDecodeFrame(out var frameTicks);
                if (buffer.Length == 0)
                {
                    break;
                }

                var bytesToWrite = GetBoundedByteCount(buffer.Length, frameTicks, duration.Ticks - decodedTicks);
                if (bytesToWrite <= 0)
                {
                    break;
                }

                unsafe
                {
                    pcmOutput.Write(new ReadOnlySpan<byte>(buffer.Content, bytesToWrite));
                }

                decodedBytes += bytesToWrite;
                decodedTicks += frameTicks > 0
                    ? Math.Min(frameTicks, duration.Ticks - decodedTicks)
                    : Format.GetDuration(bytesToWrite).Ticks;
                frameCount++;
            }

            stopwatch.Stop();
            return new AudioDecodeResult(decodedBytes, frameCount, TimeSpan.FromTicks(decodedTicks), stopwatch.Elapsed, LastFfmpegError);
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
}