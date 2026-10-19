using System;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;

namespace LightMediaRenderer.Avalonia.Audio;

public interface IPcmPlaybackSource : IAsyncDisposable
{
    AudioFormat Format { get; }
    string? Title { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    string? LastError { get; }
    bool UsesAudioSeparation { get; }
    bool RequiresRestartOnSeek { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Consumes ready data only. Returned storage remains owned by the source until the next read or disposal.</summary>
    bool TryRead(TimeSpan maximumMediaDuration, out AudioOutputReadResult block);

    Task<TimeSpan> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}