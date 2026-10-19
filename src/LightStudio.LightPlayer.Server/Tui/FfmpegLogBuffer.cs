using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LightStudio.NativeBindings;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>
/// Buffers libav* diagnostics for the terminal UI to draw. Without this the libraries write to
/// stderr, which lands on top of the drawn screen and leaves the terminal unreadable.
/// </summary>
/// <remarks>
/// The callback runs on whichever thread FFmpeg is decoding on, so lines are collected here and
/// handed to the UI thread on its own schedule rather than pushed at it.
/// </remarks>
[Include("libavutil/log.h")]
internal static unsafe partial class FfmpegLogBuffer
{
    private const int LineSize = 1024;

    /// <summary>Lines kept before the oldest are dropped; one scan can produce thousands.</summary>
    private const int Capacity = 500;

    private static readonly object Sync = new();
    private static readonly Queue<string> Pending = new();
    private static bool attached;

    /// <summary>
    /// Redirects libav* messages at or below <paramref name="nativeLevel"/> into the buffer.
    /// </summary>
    /// <param name="nativeLevel">One of the <c>AV_LOG_*</c> constants; larger values are noisier.</param>
    public static void Attach(int nativeLevel = AV_LOG_WARNING)
    {
        lock (Sync)
        {
            av_log_set_level(nativeLevel);
            if (attached)
            {
                return;
            }

            av_log_set_callback((delegate* unmanaged[Cdecl]<void*, int, byte*, void*, void>)&Forward);
            attached = true;
        }
    }

    /// <summary>Removes and returns everything buffered since the last drain.</summary>
    public static IReadOnlyList<string> Drain()
    {
        lock (Sync)
        {
            if (Pending.Count == 0)
            {
                return [];
            }

            string[] drained = [.. Pending];
            Pending.Clear();
            return drained;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Pending.Clear();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Forward(void* context, int level, byte* format, void* arguments)
    {
        try
        {
            // av_vlog hands every message to the callback; the level filter lives in the callback
            // itself, which is what the default one does too.
            if (level > av_log_get_level())
            {
                return;
            }

            byte* line = stackalloc byte[LineSize];
            int printPrefix = 1;
            int written = __native_av_log_format_line2(
                context,
                level,
                format,
                arguments,
                line,
                LineSize,
                &printPrefix);
            if (written <= 0)
            {
                return;
            }

            string? message = Marshal.PtrToStringUTF8((IntPtr)line)?.TrimEnd('\n', '\r', ' ');
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            lock (Sync)
            {
                Pending.Enqueue(Severity(level) + message);
                while (Pending.Count > Capacity)
                {
                    Pending.Dequeue();
                }
            }
        }
        catch
        {
            // A logging failure must never unwind into native code.
        }
    }

    private static string Severity(int level) => level switch
    {
        <= AV_LOG_FATAL => "fatal: ",
        <= AV_LOG_ERROR => "error: ",
        <= AV_LOG_WARNING => "warning: ",
        <= AV_LOG_INFO => "info: ",
        _ => "debug: ",
    };
}
