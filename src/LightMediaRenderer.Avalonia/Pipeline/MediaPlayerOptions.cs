using System;
using System.IO;
using LightStudio.FfmpegShim.Decoding;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Tuning knobs for one playback session.</summary>
public sealed record MediaPlayerOptions
{
    public static MediaPlayerOptions Default { get; } = new();

    public Func<string, IMediaSourceProvider>? SourceProviderFactory { get; init; }

    public Func<string, Stream> OpenFile { get; init; } = File.OpenRead;

    public Func<VideoDecoderOptions>? VideoDecoderOptionsProvider { get; init; }

    public Func<TimeSpan>? VideoBufferDurationProvider { get; init; }

    public Func<int>? AudioSampleRateProvider { get; init; }

    public Func<string>? DefaultTrackNameProvider { get; init; }

    /// <summary>Compressed video packets held between the demuxer and the video decoder.</summary>
    public int VideoPacketCapacity { get; init; } = 512;

    public long VideoPacketByteCapacity { get; init; } = 32L * 1024 * 1024;

    public int AudioPacketCapacity { get; init; } = 512;

    public long AudioPacketByteCapacity { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// How far ahead of the clock decoded video is kept. The frame count follows from the source's
    /// frame rate, so the same setting buffers the same amount of time at 24 fps and at 120 fps.
    /// </summary>
    public TimeSpan VideoBufferDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Bounds on the derived frame count. The lower bound keeps a queue that can still absorb
    /// jitter when the frame rate is unknown or very low; the upper bound is a backstop for a
    /// source claiming an absurd rate.
    /// </summary>
    public int MinimumVideoFrameCapacity { get; init; } = 3;

    public int MaximumVideoFrameCapacity { get; init; } = 64;

    /// <summary>
    /// Ceiling on those frames in bytes, which is what actually bounds them: five 8K frames are
    /// 660 MB where five 1080p frames are 40 MB. At least one frame is always admitted.
    /// </summary>
    public long VideoFrameByteCapacity { get; init; } = 384L * 1024 * 1024;

    public int AudioFrameCapacity { get; init; } = 32;

    /// <summary>Requested device buffer. Larger is safer against underruns, smaller is more responsive.</summary>
    public TimeSpan AudioDeviceBuffer { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Channel count requested from the audio device.</summary>
    public int AudioChannelCount { get; init; } = 2;

    /// <summary>0 uses the source sample rate, which avoids one resampling stage.</summary>
    public int AudioSampleRate { get; init; }

    public VideoDecoderOptions VideoDecoder { get; init; } = VideoDecoderOptions.Default;

    public VideoSyncOptions Sync { get; init; } = VideoSyncOptions.Default;

    /// <summary>
    /// Decode and discard from the keyframe up to the requested position instead of starting
    /// playback at the keyframe. Costs the decode time of one group of pictures but makes a seek
    /// land where the user asked; long-GOP sources are otherwise off by several seconds.
    /// </summary>
    public bool AccurateSeek { get; init; } = true;

    /// <summary>
    /// How long the pipeline must be unable to progress before <c>MediaPlayer.StallChanged</c>
    /// announces it. Keeps the millisecond-scale gaps of ordinary playback from flashing a
    /// buffering indicator; recovery is always reported immediately.
    /// </summary>
    public TimeSpan StallReportDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How often a hardware decoder that was working may be reopened after failing before playback
    /// gives up on it. Frequent seeking knocks some devices over transiently, and reopening costs
    /// one seek where dropping to software costs the rest of the film.
    /// </summary>
    public int HardwareDecoderRetryLimit { get; init; } = 3;

    /// <summary>
    /// Hardware failures further apart than this are counted as separate incidents rather than
    /// against the same retry budget: an hour of playback with one glitch in it is not a decoder
    /// that has stopped coping.
    /// </summary>
    public TimeSpan HardwareDecoderRetryWindow { get; init; } = TimeSpan.FromSeconds(3);
}
