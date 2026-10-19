using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

public sealed record MediaDecodeReset(int Serial, double Position, bool Succeeded = true);

public interface IMediaDecodeSession : IAsyncDisposable
{
    MediaInfo? Info { get; }
    AudioStreamFormat AudioFormat { get; }
    int Serial { get; }
    bool HasVideo { get; }
    bool HasAudio { get; }
    bool IsFaulted { get; }
    bool CanSeek { get; }
    double BufferedSeconds { get; }
    StreamingTimeline Timeline { get; }
    bool VideoBufferBlockedByAudio { get; }
    IMediaFrameSource<VideoFrame> VideoFrames { get; }
    IMediaFrameSource<AudioFrame> AudioFrames { get; }
    IReadOnlyList<AudioTrack> AudioTracks { get; }
    AudioTrack? ActiveAudioTrack { get; }
    IReadOnlyList<VideoRendition> VideoRenditions { get; }
    VideoRendition? ActiveVideoRendition { get; }
    IReadOnlyList<SubtitleTrack> SubtitleTracks { get; }
    SubtitleTrack? ActiveSubtitleTrack { get; }
    AssDocument? Subtitles { get; }
    BitmapSubtitleDocument? BitmapSubtitles { get; }

    event EventHandler<string>? ErrorOccurred;
    event EventHandler? AudioTracksChanged;
    event EventHandler<AudioTrack?>? AudioTrackChanged;
    event EventHandler? VideoRenditionsChanged;
    event EventHandler<VideoRendition?>? VideoRenditionChanged;
    event EventHandler? SubtitleTracksChanged;
    event EventHandler<SubtitleTrack?>? SubtitleTrackChanged;
    event EventHandler<AssDocument?>? SubtitlesChanged;
    event EventHandler<BitmapSubtitleDocument?>? BitmapSubtitlesChanged;

    Func<MediaDecodeReset, Task>? BeforeResetAsync { get; set; }
    Func<MediaDecodeReset, Task>? AfterResetAsync { get; set; }

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(double position, CancellationToken cancellationToken = default);
    Task SelectAudioTrackAsync(AudioTrack track, CancellationToken cancellationToken = default);
    Task SelectVideoRenditionAsync(VideoRendition rendition, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioTrack>> AddExternalAudioAsync(string path);
    Task<IReadOnlyList<SubtitleTrack>> AddExternalSubtitleAsync(string path);
    Task<IReadOnlyList<SubtitleTrack>> AddSubtitleFileAsync(string path, byte[] content);
    void SelectSubtitleTrack(SubtitleTrack? track);
    void UpdatePlaybackPosition(double position);
    void SetSpeed(double speed);
    void RequestVideoOutput(VideoOutputRequest? request);
    PlaybackStatistics GetStatistics();
    void CancelPendingOperations();
}