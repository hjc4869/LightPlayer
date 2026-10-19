using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LightStudio.FfmpegShim;

/// <summary>Owns a browser source client whose native callbacks can be passed directly to FFmpeg.</summary>
/// <remarks>Dispose only after all readers have stopped. Native storage remains owned until transport close is acknowledged.</remarks>
[SupportedOSPlatform("browser")]
public sealed unsafe class SourceBridgeContext : Stream
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public IntPtr Handle;
        public IntPtr Read;
        public IntPtr Seek;
    }

    private Callbacks callbacks;
    private readonly long length;

    public SourceBridgeContext(int source, long length, int windowSize, int slotCount, int prefetchWindows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(prefetchWindows);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(prefetchWindows, slotCount);
        this.length = length;
        int result = Create(source, length, windowSize, slotCount, prefetchWindows, out callbacks);
        if (result != 0)
            throw new OutOfMemoryException($"Could not allocate the source windows ({result}).");
    }

    internal IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(callbacks.Handle == IntPtr.Zero, this);
            return callbacks.Handle;
        }
    }

    internal IntPtr ReadCallback => callbacks.Read;
    internal IntPtr SeekCallback => callbacks.Seek;

    public static int PendingCloses => GetPendingCloses();

    public static void ReleaseClosed(int source) => Release(source);

    public override bool CanRead => callbacks.Handle != IntPtr.Zero;
    public override bool CanSeek => CanRead;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        IntPtr handle = Handle;
        fixed (byte* address = buffer)
        {
            int count = ReadNative(handle, address, buffer.Length);
            if (count >= 0)
                return count;
            ThrowIfReadFailed();
            throw new IOException($"The native source read failed ({count}).");
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin is not (SeekOrigin.Begin or SeekOrigin.Current or SeekOrigin.End))
            throw new ArgumentOutOfRangeException(nameof(origin));
        long position = SeekNative(Handle, offset, (int)origin);
        return position >= 0 ? position : throw new IOException("Cannot seek the source to the requested position.");
    }

    internal void ResetReadFailure() => ResetError(Handle);

    internal void ThrowIfReadFailed()
    {
        int error = GetError(Handle, out IntPtr text);
        if (error == 0)
            return;
        string message = Marshal.PtrToStringUTF8(text) ?? "The native source read failed.";
        throw error switch
        {
            2 => new OperationCanceledException(message),
            3 => new TimeoutException(message),
            4 => new InvalidOperationException(message),
            _ => new IOException(message),
        };
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (callbacks.Handle != IntPtr.Zero)
        {
            DisposeNative(callbacks.Handle);
            callbacks = default;
        }
        base.Dispose(disposing);
    }

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_create")]
    private static extern int Create(int source, long length, int windowSize, int slotCount, int prefetchWindows, out Callbacks callbacks);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_read")]
    private static extern int ReadNative(IntPtr handle, byte* buffer, int count);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_seek")]
    private static extern long SeekNative(IntPtr handle, long offset, int origin);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_error")]
    private static extern int GetError(IntPtr handle, out IntPtr message);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_reset_error")]
    private static extern void ResetError(IntPtr handle);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_dispose")]
    private static extern void DisposeNative(IntPtr handle);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_release_closed")]
    private static extern void Release(int source);

    [DllImport("source-bridge", EntryPoint = "lightplayer_source_pending_closes")]
    private static extern int GetPendingCloses();
}