using System;

namespace LightStudio.FfmpegShim
{
    public sealed record AudioDecodeResult(
        long DecodedBytes,
        int FrameCount,
        TimeSpan DecodedDuration,
        TimeSpan Elapsed,
        int LastFfmpegError);
}