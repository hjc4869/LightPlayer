using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim;

/// <summary>Routes libav* diagnostics into <see cref="AppLog"/>.</summary>
/// <remarks>
/// Without this the libraries write to stderr, which the packaged clients discard. Several failure
/// paths - hardware frame allocation in particular - collapse every underlying cause into a single
/// errno by the time they reach managed code, so the native line is the only description of what
/// actually went wrong.
/// </remarks>
public static unsafe partial class FfmpegLog
{
    private const int LineSize = 1024;

    private static readonly object Sync = new();
    private static bool attached;

    /// <summary>Sends libav* messages at or below <paramref name="nativeLevel"/> to the app log.</summary>
    /// <param name="nativeLevel">One of the <c>AV_LOG_*</c> constants; larger values are noisier.</param>
    public static void Attach(int nativeLevel = AV_LOG_INFO)
    {
        lock (Sync)
        {
            av_log_set_level(nativeLevel);
            if (attached)
                return;

            av_log_set_callback((delegate* unmanaged[Cdecl]<void*, int, byte*, void*, void>)&Forward);
            attached = true;
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
                return;

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
                return;

            string message = Marshal.PtrToStringUTF8((IntPtr)line)?.TrimEnd('\n', '\r', ' ');
            if (string.IsNullOrEmpty(message))
                return;

            const string Category = "FFmpeg.Native";
            switch (level)
            {
                case <= AV_LOG_FATAL:
                    AppLog.Critical(Category, message);
                    break;
                case <= AV_LOG_ERROR:
                    AppLog.Error(Category, message);
                    break;
                case <= AV_LOG_WARNING:
                    AppLog.Warning(Category, message);
                    break;
                case <= AV_LOG_INFO:
                    AppLog.Information(Category, message);
                    break;
                default:
                    AppLog.Debug(Category, message);
                    break;
            }
        }
        catch
        {
            // A logging failure must never unwind into native code.
        }
    }
}
