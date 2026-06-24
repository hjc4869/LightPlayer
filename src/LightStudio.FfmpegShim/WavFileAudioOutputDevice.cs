using System;
using System.IO;
using System.Text;

namespace LightStudio.FfmpegShim
{
    public sealed class WavFileAudioOutputDevice : Stream
    {
        private const int HeaderSize = 44;
        private readonly Stream output;
        private readonly bool leaveOpen;
        private bool finalized;
        private long dataBytes;

        public WavFileAudioOutputDevice(Stream output, AudioFormat format, bool leaveOpen = false)
        {
            if (!output.CanWrite)
            {
                throw new ArgumentException("Output stream must be writable.", nameof(output));
            }

            if (!output.CanSeek)
            {
                throw new ArgumentException("Output stream must be seekable so the WAV header can be finalized.", nameof(output));
            }

            this.output = output;
            this.leaveOpen = leaveOpen;
            Format = format;
            WriteHeader(0);
        }

        public AudioFormat Format { get; }

        public long DataBytes => dataBytes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => output.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => output.Position;
            set => throw new NotSupportedException();
        }

        public static WavFileAudioOutputDevice Create(string path, AudioFormat format)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
            return new WavFileAudioOutputDevice(File.Create(fullPath), format);
        }

        public override void Flush()
        {
            output.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            output.Write(buffer, offset, count);
            dataBytes += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            output.Write(buffer);
            dataBytes += buffer.Length;
        }

        public void FinalizeWaveFile()
        {
            if (finalized)
            {
                return;
            }

            var position = output.Position;
            output.Position = 0;
            WriteHeader(dataBytes);
            output.Position = position;
            output.Flush();
            finalized = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FinalizeWaveFile();
                if (!leaveOpen)
                {
                    output.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void WriteHeader(long dataLength)
        {
            if (dataLength > uint.MaxValue - HeaderSize)
            {
                throw new NotSupportedException("WAV output is limited to 4 GiB.");
            }

            using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)(36 + dataLength));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)Format.ChannelCount);
            writer.Write(Format.SampleRate);
            writer.Write((uint)Format.ByteRate);
            writer.Write((ushort)Format.BlockAlign);
            writer.Write((ushort)Format.BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)dataLength);
        }
    }
}