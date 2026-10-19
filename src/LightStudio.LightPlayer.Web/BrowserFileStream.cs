using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Seekable, read-only <see cref="Stream"/> over a file inside a folder the user picked with the
/// browser's file picker.
/// </summary>
/// <remarks>
/// Reads await the module owner and retain one bounded block. Synchronous reads require a cached
/// range; concurrent seeks are rejected instead of waiting for an asynchronous read.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class BrowserFileStream : Stream
{
    private const int BlockSize = 64 * 1024;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly int fileId;
    private readonly long length;
    private byte[]? block;
    private long blockOffset = -1;
    private int blockLength;
    private long position;
    private bool disposed;

    private BrowserFileStream(int fileId, long length)
    {
        this.fileId = fileId;
        this.length = length;
    }

    /// <summary>Opens a picked local file, or throws when the page may no longer read it.</summary>
    public static async Task<BrowserFileStream> OpenAsync(BrowserMediaPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = await BrowserFileSystem.OpenAsync(path).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"The browser cannot read '{path}'.", path.ToString());
        if (cancellationToken.IsCancellationRequested)
        {
            await BrowserFileSystem.CloseAsync(handle.Id).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new BrowserFileStream(handle.Id, handle.Length);
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Interlocked.Read(ref position);
        }

        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var acquired = gate.WaitAsync(0);
        if (!acquired.IsCompletedSuccessfully || !acquired.Result)
            throw new InvalidOperationException("The browser file is already being read.");
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int remaining = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            if (remaining == 0)
                return 0;
            if (blockOffset < 0 || position < blockOffset || position >= blockOffset + blockLength)
                throw new NotSupportedException("Browser file cache misses require ReadAsync.");
            int copied = CopyFromBlock(position, buffer[..remaining]);
            position += copied;
            return copied;
        }
        finally
        {
            gate.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int remaining = (int)Math.Min(Math.Min(buffer.Length, BlockSize), Math.Max(0, length - position));
            if (remaining == 0)
                return 0;
            await EnsureBlockAsync(position).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int copied = CopyFromBlock(position, buffer.Span[..remaining]);
            position += copied;
            return copied;
        }
        finally
        {
            gate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var acquired = gate.WaitAsync(0);
        if (!acquired.IsCompletedSuccessfully || !acquired.Result)
            throw new InvalidOperationException("Cannot seek during an asynchronous browser read.");
        try
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
                throw new IOException("Cannot seek before the start of the file.");
            position = target;
            return target;
        }
        finally
        {
            gate.Release();
        }

    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
                disposed = true;
                await BrowserFileSystem.CloseAsync(fileId).ConfigureAwait(false);
                block = null;
            }
        }
        finally { gate.Release(); }
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            BrowserFileSystem.Close(fileId);
        }

        base.Dispose(disposing);
    }

    private async Task EnsureBlockAsync(long offset)
    {
        var start = offset / BlockSize * BlockSize;
        if (blockOffset == start && offset - start < blockLength)
        {
            return;
        }

        block ??= new byte[BlockSize];
        var wanted = (int)Math.Min(BlockSize, length - start);
        blockOffset = -1;
        blockLength = await BrowserFileSystem.ReadAsync(fileId, start, block, wanted).ConfigureAwait(false);
        if (blockLength <= 0)
        {
            throw new EndOfStreamException($"The browser returned no data at offset {start}.");
        }

        blockOffset = start;
    }

    private int CopyFromBlock(long offset, Span<byte> destination)
    {
        var available = (int)(blockOffset + blockLength - offset);
        var count = Math.Min(available, destination.Length);
        block!.AsSpan((int)(offset - blockOffset), count).CopyTo(destination);
        return count;
    }
}
