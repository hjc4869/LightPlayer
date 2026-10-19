using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Playback policy over an <see cref="IMediaEngine"/>: which streams are decoded, how seeks, track
/// and representation changes and decoder failures are handled. The engine supplies the mechanics, in
/// this process or in a worker, so the same policy runs on every head.
/// </summary>
public sealed partial class MediaDecodeSession : IMediaDecodeSession
{
    private readonly MediaPlayerOptions options;
    private readonly IMediaEngine engine;
    private readonly bool ownsEngine;
    private readonly IMediaSessionInput input;
    private readonly bool audioOnly;
    private readonly int audioBitsPerSample;
    private readonly StatisticsCollector statistics = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycle = new();
    private readonly object trackSync = new();
    private readonly List<AudioTrack> audioTracks = new();
    private readonly List<SubtitleTrack> subtitleTracks = new();

    // Reading a subtitle stream costs a pass over the container, so a track is only read once.
    private readonly Dictionary<SubtitleTrack, AssDocument> subtitleDocuments = new();
    private readonly Dictionary<SubtitleTrack, BitmapSubtitleDocument> bitmapSubtitleDocuments = new();
    private readonly List<Task> auxiliaryOperations = new();

    // Handed out as-is, so callers can tell "the list changed" from "the list was read again".
    private IReadOnlyList<AudioTrack> audioTrackSnapshot = Array.Empty<AudioTrack>();
    private IReadOnlyList<SubtitleTrack> subtitleTrackSnapshot = Array.Empty<SubtitleTrack>();

    private MediaQueue<VideoFrame>? videoFrames;
    private MediaQueue<AudioFrame>? audioFrames;
    private IMediaDemuxer? demuxer;

    // Only set while an external track is playing: it feeds the audio decoder and the media's own
    // demuxer is left to the picture.
    private IMediaDemuxer? audioDemuxer;
    private IVideoDecoderStage? videoDecoder;
    private IAudioDecoderStage? audioDecoder;
    private VideoOutputRequest? requestedVideoOutput;
    private AudioStreamFormat audioFormat;
    private HardwareDecodeHealth health = new(false, 0, 0);
    private Task? opening;
    private Task? disposal;
    private int serial;
    private double lastKnownPosition;
    private double requestedSpeed = 1.0;
    private int videoFrameCapacity = 3;
    private volatile VideoRendition? activeVideoRendition;
    private volatile AudioTrack? activeAudioTrack;
    private volatile SubtitleTrack? activeSubtitleTrack;
    private volatile AssDocument? subtitles;
    private volatile BitmapSubtitleDocument? bitmapSubtitles;

    // Bumped by every selection, so a read that finishes after the user moved on is dropped.
    private int subtitleGeneration;
    private bool disposed;

    /// <param name="audioOnly">Decodes sound only, as music playback does.</param>
    /// <param name="audioBitsPerSample">Sample size of the decoded sound.</param>
    public MediaDecodeSession(
        MediaPlayerOptions options,
        IMediaSessionInput input,
        IMediaEngine engine,
        bool ownsEngine = false,
        bool audioOnly = false,
        int audioBitsPerSample = 16)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.ownsEngine = ownsEngine;
        this.audioOnly = audioOnly;
        this.audioBitsPerSample = audioBitsPerSample;
    }

    /// <summary>Plays through a provider with the in-process engine.</summary>
    public MediaDecodeSession(MediaPlayerOptions options, Func<IMediaSourceProvider> createProvider, bool ownsProvider = true)
        : this(options, new ProviderSessionInput(createProvider, ownsProvider), new InProcessMediaEngine(), ownsEngine: true)
    {
    }

    /// <summary>Plays through a provider with the in-process engine.</summary>
    public MediaDecodeSession(MediaPlayerOptions options, IMediaSourceProvider provider, bool ownsProvider = true)
        : this(options, new ProviderSessionInput(provider, ownsProvider), new InProcessMediaEngine(), ownsEngine: true)
    {
    }

    private sealed record HardwareDecodeHealth(bool EverDecoded, int Retries, long LastFailureTicks);

    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? AudioTracksChanged;
    public event EventHandler<AudioTrack?>? AudioTrackChanged;
    public event EventHandler? VideoRenditionsChanged;
    public event EventHandler<VideoRendition?>? VideoRenditionChanged;
    public event EventHandler? SubtitleTracksChanged;
    public event EventHandler<SubtitleTrack?>? SubtitleTrackChanged;
    public event EventHandler<AssDocument?>? SubtitlesChanged;
    public event EventHandler<BitmapSubtitleDocument?>? BitmapSubtitlesChanged;

    public MediaInfo? Info { get; private set; }
    public AudioStreamFormat AudioFormat => audioFormat;
    public int Serial => Volatile.Read(ref serial);
    public bool IsFaulted => Failure is not null;

    /// <summary>The error that stopped the session, if one did.</summary>
    public Exception? Failure { get; private set; }

    public bool HasVideo => !audioOnly && demuxer?.VideoStreamIndex >= 0;
    public bool HasAudio => audioDecoder is not null;
    public StreamingTimeline Timeline => input.Streaming?.Timeline ?? default;
    public IMediaFrameSource<VideoFrame> VideoFrames => videoFrames ?? throw new InvalidOperationException("The source is not open.");
    public IMediaFrameSource<AudioFrame> AudioFrames => audioFrames ?? throw new InvalidOperationException("The source is not open.");
    public IReadOnlyList<AudioTrack> AudioTracks => Volatile.Read(ref audioTrackSnapshot);
    public AudioTrack? ActiveAudioTrack => activeAudioTrack;
    public IReadOnlyList<VideoRendition> VideoRenditions => demuxer?.Renditions ?? Array.Empty<VideoRendition>();
    public VideoRendition? ActiveVideoRendition => activeVideoRendition;
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => Volatile.Read(ref subtitleTrackSnapshot);
    public SubtitleTrack? ActiveSubtitleTrack => activeSubtitleTrack;
    public AssDocument? Subtitles => subtitles;
    public BitmapSubtitleDocument? BitmapSubtitles => bitmapSubtitles;
    public Func<MediaDecodeReset, Task>? BeforeResetAsync { get; set; }
    public Func<MediaDecodeReset, Task>? AfterResetAsync { get; set; }

    /// <summary>The video decoder in use, for diagnostics.</summary>
    public VideoDecoderDiagnostics VideoDecoderDiagnostics => videoDecoder?.Diagnostics ?? VideoDecoderDiagnostics.None;

    /// <summary>
    /// Point the source has fetched up to, in seconds, or 0 when it does not read ahead. Only a source
    /// pulling over a network can fall behind the picture, so only one reports a frontier.
    /// </summary>
    public double BufferedSeconds =>
        demuxer?.Status is { IsBuffered: true } status && MediaTime.IsValid(status.BufferedSeconds)
            ? status.BufferedSeconds
            : 0;

    public bool CanSeek
    {
        get
        {
            if (demuxer?.CanSeek != true)
                return false;
            if (input.Streaming is not { } streaming)
                return true;
            StreamingTimeline timeline = streaming.Timeline;
            return !timeline.IsLive || timeline.CanSeekBack;
        }
    }

    public bool VideoBufferBlockedByAudio =>
        audioDemuxer is null && audioFrames?.IsFull == true &&
        demuxer?.Status is { AudioPacketQueueFull: true, VideoPacketCount: 0 };

    private string DefaultTrackName => options.DefaultTrackNameProvider?.Invoke() ?? "Default";

    private double Position => Volatile.Read(ref lastKnownPosition);

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return opening ??= OpenCoreAsync(cancellationToken);
        }
    }

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        CancellationToken token = cancellation.Token;
        await input.OpenAsync(token).ConfigureAwait(false);
        IMediaDemuxer opened = await engine.OpenDemuxerAsync(input.Main, token).ConfigureAwait(false);
        demuxer = opened;
        opened.Notified += OnDemuxerNotified;
        Info = opened.Info;
        if (!HasVideo && opened.AudioStreamIndex < 0)
            throw new InvalidDataException("The source contains no playable audio or video stream.");

        activeVideoRendition = opened.Renditions.FirstOrDefault(rendition => rendition.StreamIndex == opened.VideoStreamIndex);
        videoFrameCapacity = GetVideoFrameCapacity();
        videoFrames = new MediaQueue<VideoFrame>("video-frames", videoFrameCapacity, options.VideoFrameByteCapacity,
            frame => frame.Release(), frame => frame.AddRef(), stampSerial: false);
        audioFrames = new MediaQueue<AudioFrame>("audio-frames", options.AudioFrameCapacity, 0, frame => frame.Recycle(), stampSerial: false);

        foreach (MediaStreamInfo stream in Info.Streams)
        {
            if (stream.Kind == MediaStreamKind.Audio)
                audioTracks.Add(new AudioTrack(stream, stream.SourcePath, Info.Source, DefaultTrackName));
            else if (!audioOnly && FfmpegSubtitleReader.IsSupported(stream))
                subtitleTracks.Add(new SubtitleTrack(stream, stream.SourcePath, Info.Source, DefaultTrackName));
        }

        PublishAudioTracks();
        PublishSubtitleTracks();
        if (HasVideo)
            await AttachVideoDecoderAsync(GetVideoDecoderOptions(), token).ConfigureAwait(false);

        EnsureAudioOutput();
        if (opened.AudioStreamIndex >= 0)
        {
            await AttachAudioDecoderAsync(opened, opened.AudioStreamIndex, token).ConfigureAwait(false);
            activeAudioTrack = FindAudioTrack(opened.AudioStreamIndex, null);
        }

        token.ThrowIfCancellationRequested();
        opened.Start();
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
        VideoRenditionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AttachVideoDecoderAsync(VideoDecoderOptions decoderOptions, CancellationToken token)
    {
        IVideoDecoderStage stage = await engine.CreateVideoDecoderAsync(demuxer!, new VideoDecoderStageOptions(
            decoderOptions, options.VideoPacketCapacity, options.VideoPacketByteCapacity,
            statistics.CountDecodedVideoFrame, statistics.CountDecodeError), token).ConfigureAwait(false);
        try
        {
            stage.Faulted += OnVideoDecoderFaulted;
            stage.RequestOutput(Volatile.Read(ref requestedVideoOutput));
            stage.SetOutput(videoFrames!);
            await demuxer!.LinkAsync(stage.StreamIndex, stage, token).ConfigureAwait(false);
        }
        catch
        {
            await stage.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        videoDecoder = stage;
        health = new HardwareDecodeHealth(false, 0, 0);
    }

    private async Task AttachAudioDecoderAsync(IMediaDemuxer source, int streamIndex, CancellationToken token)
    {
        IAudioDecoderStage stage = await engine.CreateAudioDecoderAsync(source, streamIndex, new AudioDecoderStageOptions(
            audioFormat, Volatile.Read(ref requestedSpeed), options.AudioPacketCapacity, options.AudioPacketByteCapacity,
            statistics.CountDecodeError), token).ConfigureAwait(false);
        try
        {
            stage.Faulted += OnAudioDecoderFaulted;
            stage.SetOutput(audioFrames!);
            await source.LinkAsync(streamIndex, stage, token).ConfigureAwait(false);
        }
        catch
        {
            await stage.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        audioDecoder = stage;
    }

    private async Task DetachVideoDecoderAsync()
    {
        if (videoDecoder is not { } stage)
            return;
        videoDecoder = null;
        stage.Faulted -= OnVideoDecoderFaulted;
        await demuxer!.LinkAsync(stage.StreamIndex, null).ConfigureAwait(false);
        await stage.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DetachAudioDecoderAsync()
    {
        if (audioDecoder is not { } stage)
            return;
        audioDecoder = null;
        stage.Faulted -= OnAudioDecoderFaulted;
        await (audioDemuxer ?? demuxer!).LinkAsync(stage.StreamIndex, null).ConfigureAwait(false);
        await stage.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Offers what the picture is about to be drawn as, in physical output pixels, so a decoder whose
    /// device can resize may deliver frames ready to draw. Null withdraws the offer.
    /// </summary>
    public void RequestVideoOutput(VideoOutputRequest? request)
    {
        Volatile.Write(ref requestedVideoOutput, request);
        videoDecoder?.RequestOutput(request);
    }

    public void UpdatePlaybackPosition(double position)
    {
        Volatile.Write(ref lastKnownPosition, position);
        demuxer?.ReportPlaybackPosition(position);
        audioDemuxer?.ReportPlaybackPosition(position);
    }

    public void SetSpeed(double speed)
    {
        Volatile.Write(ref requestedSpeed, speed);
        audioDecoder?.SetSpeed(speed);
    }

    public Task SeekAsync(double position, CancellationToken cancellationToken = default) =>
        RunCommandAsync(() => PerformSeekAsync(position), cancellationToken);

    public Task SelectAudioTrackAsync(AudioTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!AudioTracks.Contains(track))
            throw new ArgumentException("The track does not belong to this session.", nameof(track));
        return RunCommandAsync(() => ApplyAudioTrackAsync(track), cancellationToken);
    }

    public Task SelectVideoRenditionAsync(VideoRendition rendition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rendition);
        if (!VideoRenditions.Contains(rendition))
            throw new ArgumentException("The rendition does not belong to this session.", nameof(rendition));
        return RunCommandAsync(() => ApplyVideoRenditionAsync(rendition), cancellationToken);
    }

    /// <summary>Runs one state change at a time, after opening, against the session's lifetime.</summary>
    private async Task RunCommandAsync(Func<Task> command, CancellationToken cancellationToken)
    {
        Task open;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            open = opening ?? throw new InvalidOperationException("The source is not open.");
        }

        await open.ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await commands.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await command().ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    private async Task PerformSeekAsync(double target, bool sourceAlreadyPositioned = false)
    {
        if (!CanSeek && !sourceAlreadyPositioned)
            return;

        int current = Serial;
        double previousPosition = Position;
        if (BeforeResetAsync is { } before)
            await before(new MediaDecodeReset(current, target)).ConfigureAwait(false);

        int next = current + 1;
        var request = new SeekRequest(target, next, options.AccurateSeek, PreferBuffered: audioDemuxer is null,
            sourceAlreadyPositioned);
        bool succeeded = await demuxer!.SeekAsync(request, lifetime.Token).ConfigureAwait(false);
        if (succeeded && audioDemuxer is not null &&
            !await audioDemuxer.SeekAsync(request with { PreferBuffered = false }, lifetime.Token).ConfigureAwait(false))
        {
            AppLog.Warning("Video.Audio", "The external sound track could not follow the seek.");
        }

        if (!succeeded)
        {
            if (AfterResetAsync is { } failed)
                await failed(new MediaDecodeReset(current, previousPosition, Succeeded: false)).ConfigureAwait(false);
            return;
        }

        // Frames of the previous run still in flight are dropped by the queues from here on.
        Volatile.Write(ref serial, next);
        videoFrames!.Flush(next);
        audioFrames!.Flush(next);
        Volatile.Write(ref lastKnownPosition, target);
        if (AfterResetAsync is { } after)
            await after(new MediaDecodeReset(next, target)).ConfigureAwait(false);
    }

    /// <summary>
    /// Swaps the audio decoder for one reading <paramref name="track"/> and seeks back to where playback
    /// was, which is what refills the pipeline from the new source.
    /// </summary>
    private async Task ApplyAudioTrackAsync(AudioTrack track)
    {
        double resume = Position;
        await DetachAudioDecoderAsync().ConfigureAwait(false);
        if (audioDemuxer is { } previousSidecar)
        {
            audioDemuxer = null;
            previousSidecar.Notified -= OnDemuxerNotified;
            await previousSidecar.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            EnsureAudioOutput();
            IMediaDemuxer source = demuxer!;
            if (track.IsExternal && !IsOwnedByMainSource(track.StreamIndex, track.Source))
            {
                IMediaDemuxer sidecar = await engine.OpenDemuxerAsync(input.Sidecar(track.Source!), lifetime.Token)
                    .ConfigureAwait(false);
                audioDemuxer = sidecar;
                sidecar.Notified += OnDemuxerNotified;
                source = sidecar;
            }

            await AttachAudioDecoderAsync(source, track.StreamIndex, lifetime.Token).ConfigureAwait(false);
            if (!ReferenceEquals(source, demuxer))
                source.Start();
            activeAudioTrack = track;
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            // Leaving the session silent is survivable; leaving the clock waiting on an audio device
            // that will never be fed is not.
            await DetachAudioDecoderAsync().ConfigureAwait(false);
            if (audioDemuxer is { } sidecar)
            {
                audioDemuxer = null;
                sidecar.Notified -= OnDemuxerNotified;
                await sidecar.DisposeAsync().ConfigureAwait(false);
            }

            activeAudioTrack = null;
            string message = $"Cannot play that audio track: {exception.Message}";
            AppLog.Warning("Video.Audio", message, exception);
            ErrorOccurred?.Invoke(this, message);
        }

        AudioTrackChanged?.Invoke(this, activeAudioTrack);
        await PerformSeekAsync(resume).ConfigureAwait(false);
    }

    private async Task ApplyVideoRenditionAsync(VideoRendition rendition)
    {
        if (videoDecoder is not { } previous || demuxer is not { } source)
            return;

        double resume = Position;
        int previousStreamIndex = source.VideoStreamIndex;
        int previousAudioStreamIndex = source.AudioStreamIndex;
        bool preserveAudioSelection = audioDemuxer is not null ||
            activeAudioTrack is not null &&
            activeVideoRendition?.AssociatedAudioStreamIndex != activeAudioTrack.StreamIndex;
        AudioTrack? associatedAudioTrack = !preserveAudioSelection && rendition.AssociatedAudioStreamIndex >= 0
            ? FindAudioTrack(rendition.AssociatedAudioStreamIndex, null)
            : null;
        bool switchAssociatedAudio = associatedAudioTrack is not null &&
            associatedAudioTrack.StreamIndex != previousAudioStreamIndex;
        int desiredAudioStreamIndex = switchAssociatedAudio ? associatedAudioTrack!.StreamIndex : previousAudioStreamIndex;

        await DetachVideoDecoderAsync().ConfigureAwait(false);
        try
        {
            await source.SelectRenditionAsync(rendition.StreamIndex, desiredAudioStreamIndex, lifetime.Token).ConfigureAwait(false);
            await AttachVideoDecoderAsync(GetVideoDecoderOptions(), lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            string message = $"Cannot play that video representation: {exception.Message}";
            AppLog.Warning("Video.Rendition", message, exception);
            ErrorOccurred?.Invoke(this, message);
            await source.SelectRenditionAsync(previousStreamIndex, previousAudioStreamIndex, lifetime.Token).ConfigureAwait(false);
            await AttachVideoDecoderAsync(GetVideoDecoderOptions(), lifetime.Token).ConfigureAwait(false);
            await PerformSeekAsync(resume).ConfigureAwait(false);
            return;
        }

        Info = source.Info;
        activeVideoRendition = rendition;
        if (switchAssociatedAudio)
            await ApplyAudioTrackAsync(associatedAudioTrack!).ConfigureAwait(false);
        else
            await PerformSeekAsync(resume).ConfigureAwait(false);
        VideoRenditionChanged?.Invoke(this, rendition);
    }

    private void OnDemuxerNotified(object? sender, DemuxerNotification notification)
    {
        if (notification.Kind == DemuxerNotificationKind.Faulted)
        {
            if (!lifetime.IsCancellationRequested)
                ReportError(notification.Message, notification.Exception);
            return;
        }

        if (!ReferenceEquals(sender, demuxer))
        {
            ReportError("Only the main packet source can reconfigure playback.");
            return;
        }

        Observe(RunCommandAsync(() => ReconfigureAsync(notification), CancellationToken.None));
    }

    /// <summary>Rebuilds the decoders for a source whose streams changed, then resumes where it now is.</summary>
    private async Task ReconfigureAsync(DemuxerNotification notification)
    {
        VideoDecoderOptions decoderOptions = GetVideoDecoderOptions();
        if (videoDecoder?.Diagnostics is { SoftwareFallbackOccurred: true } diagnostics)
        {
            decoderOptions = decoderOptions with
            {
                EnableHardwareDecoding = false,
                SoftwareFallbackReason = diagnostics.HardwareStatus,
            };
        }

        await DetachVideoDecoderAsync().ConfigureAwait(false);
        await DetachAudioDecoderAsync().ConfigureAwait(false);
        Info = notification.Info ?? demuxer!.Info;
        if (HasVideo)
            await AttachVideoDecoderAsync(decoderOptions, lifetime.Token).ConfigureAwait(false);
        if (activeAudioTrack is { } audio)
            await AttachAudioDecoderAsync(audioDemuxer ?? demuxer!, audio.StreamIndex, lifetime.Token).ConfigureAwait(false);
        await PerformSeekAsync(notification.Position, sourceAlreadyPositioned: true).ConfigureAwait(false);
    }

    private void OnAudioDecoderFaulted(object? sender, DecoderFault fault)
    {
        if (lifetime.IsCancellationRequested)
            return;
        if (fault.Exception is not null)
            ReportError(fault.Message, fault.Exception);
        else
            AppLog.Warning("Audio.Decoding", $"{fault.Message} The sound stops until the next seek or track change.");
    }

    private void OnVideoDecoderFaulted(object? sender, DecoderFault fault)
    {
        if (lifetime.IsCancellationRequested)
            return;
        if (fault.Exception is not null)
        {
            ReportError(fault.Message, fault.Exception);
            return;
        }

        Observe(RunCommandAsync(() => RecoverVideoDecoderAsync((IVideoDecoderStage)sender!, fault), CancellationToken.None));
    }

    /// <summary>
    /// Restarts eligible platform decoders within the session retry budget, then seeks back to the
    /// playback position so the decoder starts again on a keyframe. Compatibility failures require
    /// software; decoders with unreported hardware use also allow software after retries.
    /// </summary>
    private async Task RecoverVideoDecoderAsync(IVideoDecoderStage stage, DecoderFault fault)
    {
        if (!ReferenceEquals(stage, videoDecoder))
            return;

        HardwareDecodeHealth current = health with { EverDecoded = health.EverDecoded || fault.EverDecoded };
        health = current;
        VideoDecoderDiagnostics diagnostics = fault.Diagnostics;
        string decoder = DescribeHardwareDecoder(diagnostics);
        AppLog.Warning("Video.Decoder", $"{fault.Message} Decoder: {decoder}; serial {fault.Serial}.");
        bool recovered;
        if (!string.IsNullOrEmpty(fault.RequiredSoftwareFallbackReason))
        {
            recovered = await TrySoftwareAsync(stage, fault, fault.RequiredSoftwareFallbackReason).ConfigureAwait(false);
            if (!recovered)
                ReportError($"{fault.RequiredSoftwareFallbackReason}, and no libavcodec decoder could be opened for this video.");
        }
        else if (!fault.CanRestart)
        {
            recovered = false;
            ReportError("The video decoder stopped unexpectedly.");
        }
        else if (!current.EverDecoded && diagnostics.IsHardwareAccelerated)
        {
            recovered = await TrySoftwareAsync(stage, fault, $"{decoder} failed before it decoded a frame").ConfigureAwait(false);
            if (!recovered)
                ReportError($"Hardware video decoding ({decoder}) failed and no software decoder could be opened for this video.");
        }
        else
        {
            long now = Environment.TickCount64;
            int retries = now - current.LastFailureTicks > options.HardwareDecoderRetryWindow.TotalMilliseconds ? 0 : current.Retries;
            health = current with { Retries = ++retries, LastFailureTicks = now };
            if (retries > options.HardwareDecoderRetryLimit)
            {
                recovered = !diagnostics.IsHardwareAccelerated && await TrySoftwareAsync(stage, fault,
                    $"{diagnostics.Implementation} exhausted its decoder restart budget").ConfigureAwait(false);
                if (!recovered)
                    ReportError(
                        $"Hardware video decoding ({decoder}) failed {retries} times in a row, so playback has stopped. " +
                        "Turn off hardware decoding in Settings to play this video.");
            }
            else
            {
                recovered = await stage.RecoverAsync(VideoDecoderRecovery.Restart, string.Empty, lifetime.Token).ConfigureAwait(false) ||
                    !diagnostics.IsHardwareAccelerated && await TrySoftwareAsync(stage, fault,
                        $"{diagnostics.Implementation} could not be restarted").ConfigureAwait(false);
                if (!recovered)
                    ReportError(
                        $"Hardware video decoding ({decoder}) failed and the device could not be reopened, so playback has stopped. " +
                        "Turn off hardware decoding in Settings to play this video.");
            }
        }

        if (recovered)
            await PerformSeekAsync(Position).ConfigureAwait(false);
    }

    private Task<bool> TrySoftwareAsync(IVideoDecoderStage stage, DecoderFault fault, string reason) =>
        fault.IsSoftware ? Task.FromResult(false) : stage.RecoverAsync(VideoDecoderRecovery.Software, reason, lifetime.Token).AsTask();

    private static string DescribeHardwareDecoder(VideoDecoderDiagnostics diagnostics)
    {
        string decoder = string.IsNullOrEmpty(diagnostics.DecoderName) ? diagnostics.CodecName : diagnostics.DecoderName;
        if (string.IsNullOrEmpty(diagnostics.HardwareDeviceType))
            return string.IsNullOrEmpty(decoder) ? "unknown decoder" : decoder;
        return string.IsNullOrEmpty(decoder) ? diagnostics.HardwareDeviceType : $"{decoder} on {diagnostics.HardwareDeviceType}";
    }

    private VideoDecoderOptions GetVideoDecoderOptions()
    {
        // The pool has to cover the queue plus the frame the renderer holds and the one being
        // decoded, or buffers are freed and reallocated on every frame.
        return (options.VideoDecoderOptionsProvider?.Invoke() ?? options.VideoDecoder) with
        {
            FramePoolSize = videoFrameCapacity + 4,
            FramePoolByteCapacity = options.VideoFrameByteCapacity,
        };
    }

    /// <summary>
    /// Turns the requested buffer duration into a frame count for this source. A queue sized in frames
    /// buffers wildly different amounts of time across frame rates: five frames is 208 ms at 24 fps but
    /// 42 ms at 120 fps, which is where a high frame rate runs out of slack first.
    /// </summary>
    private int GetVideoFrameCapacity()
    {
        TimeSpan buffer = options.VideoBufferDurationProvider?.Invoke() ?? options.VideoBufferDuration;
        double frameRate = Info?.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video)?.FrameRate ?? 0;
        if (!(frameRate > 0) || !(buffer.TotalSeconds > 0))
            return options.MinimumVideoFrameCapacity;
        int frames = (int)Math.Ceiling(buffer.TotalSeconds * frameRate);
        return Math.Clamp(frames, options.MinimumVideoFrameCapacity, options.MaximumVideoFrameCapacity);
    }

    /// <summary>Rate the device is opened at: the explicit option, the host's preferred rate, or the source rate.</summary>
    private int ResolveAudioSampleRate()
    {
        if (options.AudioSampleRate > 0)
            return options.AudioSampleRate;
        int preferred = options.AudioSampleRateProvider?.Invoke() ?? 0;
        if (preferred > 0)
            return preferred;
        return Info?.DefaultAudioStream?.SampleRate ?? 48000;
    }

    private void EnsureAudioOutput()
    {
        if (audioFormat.SampleRate == 0)
            audioFormat = new AudioStreamFormat(ResolveAudioSampleRate(), options.AudioChannelCount, audioBitsPerSample);
    }

    public PlaybackStatistics GetStatistics()
    {
        statistics.Sample();
        DemuxerStatus status = demuxer?.Status ?? DemuxerStatus.Empty;
        DemuxerStatus sidecar = audioDemuxer?.Status ?? DemuxerStatus.Empty;
        double position = Position;
        return new PlaybackStatistics
        {
            Position = MediaTime.ToTimeSpan(position),
            Duration = Info?.Duration ?? TimeSpan.Zero,
            Speed = Volatile.Read(ref requestedSpeed),
            VideoPacketCount = status.VideoPacketCount,
            AudioPacketCount = status.AudioPacketCount + sidecar.AudioPacketCount,
            VideoFrameCount = videoFrames?.Count ?? 0,
            AudioFrameCount = audioFrames?.Count ?? 0,
            VideoPacketBytes = status.VideoPacketBytes,
            AudioPacketBytes = status.AudioPacketBytes + sidecar.AudioPacketBytes,
            BufferOccupancy = Math.Max(Math.Max(status.PacketOccupancy, sidecar.PacketOccupancy),
                Math.Max(videoFrames?.Occupancy ?? 0, audioFrames?.Occupancy ?? 0)),
            DecodeFps = statistics.DecodeFps,
            DecodedVideoFrames = statistics.DecodedVideoFrames,
            DecodeErrors = statistics.DecodeErrors + status.RecoverableErrors + sidecar.RecoverableErrors,
            Decoder = videoDecoder?.Diagnostics ?? VideoDecoderDiagnostics.None,
            AudioCodecName = audioDecoder?.CodecName ?? string.Empty,
            AudioOutputFormat = audioFormat.ToString(),
            MediaSource = input.Description,
            HasBufferedPacketSource = status.IsBuffered,
            SourceBufferRemaining = MediaTime.ToTimeSpan(status.IsBuffered && MediaTime.IsValid(status.BufferedSeconds)
                ? Math.Max(0, status.BufferedSeconds - position) : 0),
            SourceBufferHistory = MediaTime.ToTimeSpan(status.IsBuffered && MediaTime.IsValid(status.HistoryStartSeconds)
                ? Math.Max(0, position - status.HistoryStartSeconds) : 0),
            EstimatedBandwidthBitsPerSecond = status.EstimatedBandwidthBitsPerSecond,
        };
    }

    private void ReportError(string message, Exception? exception = null)
    {
        Failure ??= exception ?? new InvalidOperationException(message);
        statistics.CountDecodeError();
        AppLog.Error("Video.Decoding", message, exception);
        videoFrames?.Abort();
        audioFrames?.Abort();
        ErrorOccurred?.Invoke(this, message);
    }

    public void CancelPendingOperations()
    {
        lifetime.Cancel();
        input.CancelPendingOperations();
        videoDecoder?.CancelPendingOperations();
        audioDecoder?.CancelPendingOperations();
        demuxer?.CancelPendingOperations();
        audioDemuxer?.CancelPendingOperations();
        videoFrames?.Abort();
        audioFrames?.Abort();
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            disposed = true;
            return new ValueTask(disposal ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        CancelPendingOperations();
        if (opening is not null)
        {
            try
            {
                await opening.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                AppLog.Warning("Video.Decoding", "The source failed while opening.", exception);
            }
        }

        Task[] pending;
        lock (auxiliaryOperations)
            pending = auxiliaryOperations.ToArray();
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("Video.Decoding", "A source operation failed while the session was closing.", exception);
        }
        catch (OperationCanceledException)
        {
        }

        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (videoDecoder is { } video)
            {
                videoDecoder = null;
                video.Faulted -= OnVideoDecoderFaulted;
                await video.DisposeAsync().ConfigureAwait(false);
            }

            if (audioDecoder is { } audio)
            {
                audioDecoder = null;
                audio.Faulted -= OnAudioDecoderFaulted;
                await audio.DisposeAsync().ConfigureAwait(false);
            }

            foreach (IMediaDemuxer? owned in new[] { demuxer, audioDemuxer })
            {
                if (owned is null)
                    continue;
                owned.Notified -= OnDemuxerNotified;
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            demuxer = audioDemuxer = null;
            videoFrames?.Close();
            audioFrames?.Close();
            await input.DisposeAsync().ConfigureAwait(false);
            if (ownsEngine)
                await engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    private static void Observe(Task operation) => _ = ObserveAsync(operation);

    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Video.Decoding", "A playback state change failed.", exception);
        }
    }
}
