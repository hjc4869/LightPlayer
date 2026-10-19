#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>
/// One memory and execution domain that demuxes and decodes. Components created by the same engine
/// link to each other by reference, so packets never leave the engine; only frames reach the caller.
/// </summary>
public interface IMediaEngine : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Opens a source. The demuxer reads nothing until it is started.</summary>
    ValueTask<IMediaDemuxer> OpenDemuxerAsync(MediaInput input, CancellationToken cancellationToken = default);

    /// <summary>Creates a decoder for the video stream <paramref name="demuxer"/> currently delivers.</summary>
    ValueTask<IVideoDecoderStage> CreateVideoDecoderAsync(
        IMediaDemuxer demuxer,
        VideoDecoderStageOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<IAudioDecoderStage> CreateAudioDecoderAsync(
        IMediaDemuxer demuxer,
        int streamIndex,
        AudioDecoderStageOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a text subtitle stream of <paramref name="input"/> out as one ASS script.</summary>
    ValueTask<string> ReadSubtitleScriptAsync(MediaInput input, int streamIndex, CancellationToken cancellationToken = default);

    /// <summary>Reads a bitmap subtitle stream of <paramref name="input"/> into timed compositions.</summary>
    ValueTask<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        MediaInput input,
        int streamIndex,
        bool preserveContainerTimestamps,
        CancellationToken cancellationToken = default);

    /// <summary>Decodes bitmap subtitle packets that arrived without their container into timed compositions.</summary>
    ValueTask<BitmapSubtitleDocument> DecodeBitmapSubtitlesAsync(
        BitmapSubtitlePackets packets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes where bytes come from without opening them, so the engine can read them in its own
/// domain. Engines accept the kinds they know and reject the rest.
/// </summary>
public abstract record MediaInput(string Name);

/// <summary>A seekable stream the caller opens on request.</summary>
public sealed record StreamMediaInput(string Name, Func<CancellationToken, Task<Stream>> Open) : MediaInput(Name);

/// <summary>A complete file already in memory.</summary>
public sealed record MemoryMediaInput(string Name, ReadOnlyMemory<byte> Content) : MediaInput(Name);

/// <summary>A packet source built by a provider in the engine's own process.</summary>
public sealed record PacketSourceMediaInput(string Name, Func<IPacketSource> Open) : MediaInput(Name);

/// <summary>
/// A packet source the caller opens without blocking, such as a server's packet stream. An engine in another
/// memory domain requires an <see cref="IRelayablePacketSource"/>.
/// </summary>
public sealed record AsyncPacketSourceMediaInput(string Name, Func<CancellationToken, Task<IPacketSource>> Open) : MediaInput(Name);

/// <summary>Seeks in <see cref="IMediaDemuxer.SeekAsync"/>.</summary>
/// <param name="Position">Target on the playback timeline, in seconds.</param>
/// <param name="Serial">Run number the frames after the seek carry; only ever increases.</param>
/// <param name="DiscardBeforePosition">Drops decoded frames before the target instead of showing the keyframe.</param>
/// <param name="PreferBuffered">Allows cutting into packets already fetched instead of moving the source.</param>
/// <param name="SourceAlreadyPositioned">The source is already at the target after a reconfiguration.</param>
public sealed record SeekRequest(
    double Position,
    int Serial,
    bool DiscardBeforePosition,
    bool PreferBuffered = false,
    bool SourceAlreadyPositioned = false);

/// <summary>A snapshot of a demuxer's read state; reading it costs nothing.</summary>
public sealed record DemuxerStatus
{
    public static readonly DemuxerStatus Empty = new();

    public bool EndOfFile { get; init; }
    public bool IsBuffered { get; init; }
    public double BufferedSeconds { get; init; } = double.NaN;
    public double HistoryStartSeconds { get; init; } = double.NaN;
    public double EstimatedBandwidthBitsPerSecond { get; init; }
    public int VideoPacketCount { get; init; }
    public int AudioPacketCount { get; init; }
    public long VideoPacketBytes { get; init; }
    public long AudioPacketBytes { get; init; }
    public bool AudioPacketQueueFull { get; init; }
    public double PacketOccupancy { get; init; }
    public int RecoverableErrors { get; init; }
}

public enum DemuxerNotificationKind
{
    /// <summary>The source changed its streams; decoders must be rebuilt before reading resumes.</summary>
    Reconfigured,

    /// <summary>The source failed and stopped reading.</summary>
    Faulted,
}

public sealed record DemuxerNotification(
    DemuxerNotificationKind Kind,
    string Message = "",
    MediaInfo? Info = null,
    double Position = double.NaN,
    Exception? Exception = null);

/// <summary>
/// Reads packets and hands them to the decoders linked to its streams. Every method is safe to call
/// from the caller's context; the work itself happens in the engine.
/// </summary>
public interface IMediaDemuxer : IAsyncDisposable
{
    MediaInfo Info { get; }
    int VideoStreamIndex { get; }
    int AudioStreamIndex { get; }
    bool CanSeek { get; }
    IReadOnlyList<VideoRendition> Renditions { get; }
    DemuxerStatus Status { get; }

    /// <summary>Raised from the engine; handlers must not block.</summary>
    event EventHandler<DemuxerNotification>? Notified;

    /// <summary>Routes a stream to <paramref name="decoder"/>, or stops reading it when null.</summary>
    ValueTask LinkAsync(int streamIndex, IMediaDecoderStage? decoder, CancellationToken cancellationToken = default);

    /// <summary>Starts or resumes reading after a reconfiguration.</summary>
    void Start();

    /// <summary>Repositions the source and the linked decoders. False when the source could not move.</summary>
    ValueTask<bool> SeekAsync(SeekRequest request, CancellationToken cancellationToken = default);

    /// <summary>Switches the delivered video stream and its associated audio stream.</summary>
    ValueTask SelectRenditionAsync(int videoStreamIndex, int audioStreamIndex, CancellationToken cancellationToken = default);

    /// <summary>Reports the presented position so a buffering source can trim its history.</summary>
    void ReportPlaybackPosition(double seconds);

    /// <summary>Interrupts transport waits for a prompt shutdown.</summary>
    void CancelPendingOperations();
}

/// <summary>Takes decoded frames, usually the player's frame queue.</summary>
public interface IFrameProcessor<T> where T : class, IMediaQueueItem
{
    /// <summary>Run the processor accepts; frames of older runs are dropped and newer ones wait.</summary>
    int Serial { get; }

    bool TryWrite(T item);

    /// <summary>Blocks while full. False when the wait was interrupted, leaving the item with the caller.</summary>
    bool Enqueue(T item, CancellationToken cancellationToken, bool keepOnAbort = false);
}

public sealed record DecoderFault(
    string Message,
    int Serial,
    VideoDecoderDiagnostics Diagnostics,
    string RequiredSoftwareFallbackReason,
    bool CanRestart,
    bool EverDecoded,
    bool IsSoftware,
    Exception? Exception = null);

public enum VideoDecoderRecovery
{
    /// <summary>Reopens the decoder on the same device.</summary>
    Restart,

    /// <summary>Switches to libavcodec without hardware acceleration.</summary>
    Software,
}

public interface IMediaDecoderStage : IAsyncDisposable
{
    int StreamIndex { get; }
    string CodecName { get; }

    /// <summary>Raised from the engine when the decoder stopped; handlers must not block.</summary>
    event EventHandler<DecoderFault>? Faulted;

    void CancelPendingOperations();
}

public sealed record VideoDecoderStageOptions(
    VideoDecoderOptions Decoder,
    int PacketCapacity,
    long PacketByteCapacity,
    Action? FrameDecoded = null,
    Action? DecodeError = null);

public sealed record AudioDecoderStageOptions(
    AudioStreamFormat Format,
    double Speed,
    int PacketCapacity,
    long PacketByteCapacity,
    Action? DecodeError = null);

public interface IVideoDecoderStage : IMediaDecoderStage
{
    VideoDecoderDiagnostics Diagnostics { get; }

    /// <summary>Connects the decoder's output; decoding starts once it has one.</summary>
    void SetOutput(IFrameProcessor<VideoFrame> output);

    void RequestOutput(VideoOutputRequest? request);

    /// <summary>Replaces a faulted decoder. The caller then seeks so the replacement starts on a keyframe.</summary>
    ValueTask<bool> RecoverAsync(VideoDecoderRecovery recovery, string reason, CancellationToken cancellationToken = default);
}

public interface IAudioDecoderStage : IMediaDecoderStage
{
    AudioStreamFormat OutputFormat { get; }

    void SetOutput(IFrameProcessor<AudioFrame> output);

    /// <summary>Changes the playback rate; time-stretched so the pitch does not move.</summary>
    void SetSpeed(double speed);
}
