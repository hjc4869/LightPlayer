using System;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Interop;

/// <summary>FFmpeg return-code helpers shared by the demuxer and the decoders.</summary>
public static unsafe partial class FfmpegError
{
    /// <summary><c>AVERROR(EAGAIN)</c> for the current runtime platform.</summary>
    public static readonly int TryAgain = -EAGAIN;

    /// <summary><c>AVERROR(EIO)</c> for the current runtime platform.</summary>
    public static readonly int InputOutput = -EIO;

    public static readonly int EndOfFile = AVERROR_EOF;

    public static bool IsTryAgain(int code) => code == TryAgain;

    public static bool IsEndOfFile(int code) => code == EndOfFile;

    public static string Describe(int code)
    {
        byte* buffer = stackalloc byte[256];
        if (av_strerror(code, buffer, 256) < 0)
            return $"FFmpeg error {code}";
        return $"{new string((sbyte*)buffer)} ({code})";
    }

    /// <summary>Throws with a decoded FFmpeg message when <paramref name="code"/> is negative.</summary>
    public static int ThrowIfError(int code, string operation)
    {
        if (code < 0)
            throw new FfmpegException($"{operation} failed: {Describe(code)}", code);
        return code;
    }
}

public sealed class FfmpegException : Exception
{
    public FfmpegException(string message, int code) : base(message) => Code = code;

    public int Code { get; }
}
