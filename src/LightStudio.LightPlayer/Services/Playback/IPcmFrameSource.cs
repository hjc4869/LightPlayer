using System;
using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Services.Playback;

internal interface IPcmFrameSource : IDisposable
{
    AudioFormat Format { get; }
    string? Title { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    string? LastError { get; }
    TimeSpan Seek(TimeSpan target);
    bool TryReadFrame(TimeSpan maxDuration, out PcmFrame? frame);
}