using System;
using System.IO;

namespace LightStudio.FfmpegShim.Core;

/// <summary>A read-only window of a seekable stream. The underlying stream remains owned by the caller.</summary>
public sealed class SubStream(Stream inner, long offset, long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int bufferOffset, int count) => Read(buffer.AsSpan(bufferOffset, count));
    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, length - position);
        if (count <= 0)
            return 0;
        inner.Position = offset + position;
        int read = inner.Read(buffer[..count]);
        position += read;
        return read;
    }

    public override long Seek(long target, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => target,
        SeekOrigin.Current => position + target,
        SeekOrigin.End => length + target,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
}