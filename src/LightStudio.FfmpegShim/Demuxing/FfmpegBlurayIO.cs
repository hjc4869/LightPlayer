#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Demuxing;

internal sealed unsafe partial class FfmpegBlurayIO : IDisposable
{
    private static readonly ConcurrentDictionary<nint, FfmpegBlurayIO> Sources = new();

    private readonly AVFormatContext* format;
    private readonly AVIOContext* context;
    private readonly delegate* unmanaged[Cdecl]<void*, long, int, long> originalSeek;
    private readonly delegate* unmanaged[Cdecl]<void*, byte*, int, int> originalRead;
    private bool disposed;

    public FfmpegBlurayIO(AVFormatContext* format)
    {
        ArgumentNullException.ThrowIfNull(format);
        context = format->pb;
        if (context == null || context->seek == null || context->read_packet == null)
            throw new NotSupportedException("The Blu-ray source does not expose raw seek and read callbacks.");

        this.format = format;
        originalSeek = context->seek;
        originalRead = context->read_packet;
        if (!Sources.TryAdd((nint)context->opaque, this))
            throw new InvalidOperationException("The Blu-ray source already has a seek adapter.");

        context->seek = &Seek;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        context->seek = originalSeek;
        Sources.TryRemove((nint)context->opaque, out _);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long position, int whence)
    {
        try
        {
            return Sources.TryGetValue((nint)opaque, out FfmpegBlurayIO? source)
                ? source.SeekExact(position, whence)
                : AVERROR_EXTERNAL;
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.BlurayIO", "Blu-ray byte seek failed.", exception);
            return AVERROR_EXTERNAL;
        }
    }

    private long SeekExact(long position, int whence)
    {
        long actual = originalSeek(context->opaque, position, whence);
        if (actual < 0 || whence != (int)SeekOrigin.Begin || actual == position)
            return actual;
        if (actual > position)
            return AVERROR_INVALIDDATA;

        const int bufferSize = 32768;
        byte* buffer = stackalloc byte[bufferSize];
        while (actual < position)
        {
            AVIOInterruptCB interrupt = format->interrupt_callback;
            if (interrupt.callback != null && interrupt.callback(interrupt.opaque) != 0)
                return AVERROR_EXIT;

            int requested = (int)Math.Min(position - actual, bufferSize);
            int received = originalRead(context->opaque, buffer, requested);
            if (received < 0)
                return received;
            if (received == 0)
                return AVERROR_EOF;
            actual += received;
        }

        return actual;
    }
}