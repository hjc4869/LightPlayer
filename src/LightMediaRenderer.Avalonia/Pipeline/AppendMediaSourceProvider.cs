using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Plays media pushed into it the way a page feeds a Media Source: buffers declared by MIME type and
/// filled with initialization segments and media segments in any chunking and order. Every member
/// mirrors one session message of the browser link and is safe to call from any thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OpenMain"/> blocks until every declared buffer has delivered an initialization segment.
/// Packets are then read through an <see cref="IBufferedPacketSource"/> that waits at the end of a
/// buffered range, and a seek outside the buffered ranges completes once data covering it arrives.
/// The first video track is stream 0 and the first audio track stream 1.
/// </para>
/// <para>
/// Buffered media is kept within a byte budget. Each buffer gets a share of it: buffers carrying video
/// weigh ten times as much as audio-only ones. Over its share, a buffer loses whole groups of pictures
/// behind the playhead, and is full while that does not suffice.
/// </para>
/// </remarks>
public sealed class AppendMediaSourceProvider : IMediaSourceProvider, IStreamingMediaSourceProvider
{
    private const int VideoBufferWeight = 10;
    private const int AudioBufferWeight = 1;
    private const double EvictionStepSeconds = 0.25;

    /// <summary>
    /// Media this far behind the playhead stays. The page's element trails the player by a report,
    /// and stays at a seek's target for a moment after the player has started playing from it.
    /// </summary>
    private const double EvictionGuardSeconds = 1;

    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<AppendSourceBuffer> buffers = new();
    private readonly MediaPacketPool pool;
    private AppendPacketSource? source;
    private bool bound;
    private TrackKinds boundKinds;
    private TrackKinds? resetKinds;
    private double duration = double.NaN;
    private bool live;
    private double seekableStart;
    private double seekableEnd;
    private bool ended;
    private EndOfStreamError failure;
    private double playbackPosition = double.NaN;
    private double lastEvictionPosition = double.NaN;
    private StreamingTimeline timeline;
    private StreamingLoadStage loadStage = StreamingLoadStage.Connecting;
    private string title = "Browser media";
    private bool disposed;

    /// <param name="budgetBytes">Byte budget for buffered media; <see cref="DefaultBudgetBytes"/> when 0.</param>
    public AppendMediaSourceProvider(long budgetBytes = 0)
        : this(budgetBytes, new MediaPacketPool())
    {
    }

    private AppendMediaSourceProvider(long budgetBytes, MediaPacketPool pool)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        BudgetBytes = budgetBytes > 0 ? budgetBytes : DefaultBudgetBytes;
        this.pool = pool;
        timeline = BuildTimelineLocked();
    }

    [Flags]
    private enum TrackKinds
    {
        None = 0,
        Video = 1,
        Audio = 2,
    }

    /// <summary>
    /// Budget for one session: a sixteenth of the memory the runtime can see, between 64 MiB and 256 MiB.
    /// </summary>
    public static long DefaultBudgetBytes =>
        Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16, 64L * 1024 * 1024, 256L * 1024 * 1024);

    /// <summary>Byte budget for buffered media, as <c>ready</c> reports it.</summary>
    public long BudgetBytes { get; }

    /// <summary>Name the media is shown under; the page's title.</summary>
    public string Title
    {
        get
        {
            lock (sync)
                return title;
        }
        set
        {
            lock (sync)
                title = string.IsNullOrWhiteSpace(value) ? "Browser media" : value;
        }
    }

    public string Description => "browser Media Source";

    public StreamingTimeline Timeline
    {
        get
        {
            lock (sync)
                return timeline;
        }
    }

    public event EventHandler? TimelineChanged;

    public StreamingLoadStage LoadStage
    {
        get
        {
            lock (sync)
                return loadStage;
        }
    }

    public event EventHandler? LoadStageChanged;

    /// <summary>
    /// Raised when eviction changes a buffer's ranges or full flag outside the reply to that buffer's
    /// own append: the <c>buffered</c> message. Raised on the thread that caused it, outside any lock.
    /// </summary>
    public event EventHandler<BufferedReply>? BufferedChanged;

    /// <summary>Whether every declared buffer has delivered an initialization segment.</summary>
    public bool IsReady
    {
        get
        {
            lock (sync)
                return ReadyLocked();
        }
    }

    /// <summary>Whether <c>endOfStream</c> marked the media complete, with no error and no append since.</summary>
    public bool IsEnded
    {
        get
        {
            lock (sync)
                return ended && failure == EndOfStreamError.None;
        }
    }

    /// <summary>The timeline length the page set: NaN while unset, <see cref="double.PositiveInfinity"/> when unbounded.</summary>
    public double Duration
    {
        get
        {
            lock (sync)
                return duration;
        }
    }

    /// <summary>
    /// What the page's element reports as buffered: the intersection of every buffer's ranges, each
    /// buffer's last range reaching the highest end once the media is complete.
    /// </summary>
    public IReadOnlyList<MediaTimeRange> Buffered
    {
        get
        {
            lock (sync)
            {
                var each = new List<List<MediaTimeRange>>(buffers.Count);
                double highest = double.NegativeInfinity;
                foreach (AppendSourceBuffer buffer in buffers)
                {
                    var ranges = new List<MediaTimeRange>(BufferedLocked(buffer));
                    if (ranges.Count == 0)
                        return Array.Empty<MediaTimeRange>();
                    highest = Math.Max(highest, ranges[^1].End);
                    each.Add(ranges);
                }

                List<MediaTimeRange>? result = null;
                foreach (List<MediaTimeRange> ranges in each)
                {
                    if (ended)
                        ranges[^1] = new MediaTimeRange(ranges[^1].Start, Math.Max(ranges[^1].End, highest));
                    result = result is null ? ranges : Intersect(result, ranges);
                }

                return result ?? (IReadOnlyList<MediaTimeRange>)Array.Empty<MediaTimeRange>();
            }
        }
    }

    /// <summary>Tracks the buffers' current initialization segments declare, in buffer order.</summary>
    public IReadOnlyList<AppendedTrack> Tracks
    {
        get
        {
            lock (sync)
                return buffers.SelectMany(buffer => buffer.DescribeTracks()).ToArray();
        }
    }

    /// <summary>
    /// Whether the buffers declared after <see cref="Reset"/> carry other kinds of tracks than playback
    /// was set up with. Playback then waits, and has to be opened again over <see cref="Transfer"/>.
    /// </summary>
    public bool RequiresReopen
    {
        get
        {
            lock (sync)
                return !bound && ReadyLocked() && !CanRebindLocked();
        }
    }

    internal object Sync => sync;

    internal bool Ended => ended;

    internal EndOfStreamError Failure => failure;

    internal bool Bound => bound;

    /// <summary>
    /// Starts the source again for a repeated <c>open</c>: every buffer and everything buffered is
    /// dropped. A packet source in use waits for the new buffers and then reports a reconfiguration.
    /// </summary>
    public void Reset()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (AppendSourceBuffer buffer in buffers)
                DropBufferLocked(buffer);
            buffers.Clear();
            if (bound)
                resetKinds = boundKinds;
            bound = false;
            ended = false;
            failure = EndOfStreamError.None;
            duration = double.NaN;
            live = false;
            seekableStart = seekableEnd = 0;
            source?.OnReset();
            SetLoadStageLocked(StreamingLoadStage.Connecting);
            UpdateTimelineLocked();
            Monitor.PulseAll(sync);
        }

        RaiseTimeline();
    }

    /// <summary>
    /// Moves every buffer and the timeline into a new provider and leaves this one empty, for playback
    /// opened again once <see cref="RequiresReopen"/> holds.
    /// </summary>
    public AppendMediaSourceProvider Transfer()
    {
        var next = new AppendMediaSourceProvider(BudgetBytes, pool);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            next.buffers.AddRange(buffers);
            buffers.Clear();
            next.title = title;
            next.duration = duration;
            next.live = live;
            next.seekableStart = seekableStart;
            next.seekableEnd = seekableEnd;
            next.ended = ended;
            next.failure = failure;
            next.timeline = next.BuildTimelineLocked();
            next.loadStage = StreamingLoadStage.ReadingStreamInformation;
            Monitor.PulseAll(sync);
        }

        return next;
    }

    /// <summary>Declares a buffer: <c>addSourceBuffer</c>.</summary>
    public SourceBufferAddResult AddSourceBuffer(uint buffer, string mimeType)
    {
        if (!AppendSourceCapabilities.TryParse(mimeType, out SegmentContainer container))
            return SourceBufferAddResult.Unsupported;

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffers.Any(existing => existing.Id == buffer))
                return SourceBufferAddResult.DuplicateId;
            if (bound)
                return SourceBufferAddResult.TooLate;

            buffers.Add(new AppendSourceBuffer(buffer, mimeType, container));
            SetLoadStageLocked(StreamingLoadStage.ReadingStreamInformation);
            Monitor.PulseAll(sync);
        }

        RaiseLoadStage();
        return SourceBufferAddResult.Added;
    }

    /// <summary>Drops a buffer and everything it holds: <c>removeSourceBuffer</c>.</summary>
    public void RemoveSourceBuffer(uint buffer)
    {
        lock (sync)
        {
            AppendSourceBuffer target = FindLocked(buffer);
            DropBufferLocked(target);
            buffers.Remove(target);
            Monitor.PulseAll(sync);
        }
    }

    /// <summary>
    /// Switches a buffer to another MIME type: <c>changeType</c>. An initialization segment has to come
    /// next. False when the type is not accepted, which leaves the buffer as it was.
    /// </summary>
    public bool ChangeType(uint buffer, string mimeType)
    {
        if (!AppendSourceCapabilities.TryParse(mimeType, out SegmentContainer container))
            return false;

        AppendSourceBuffer target;
        lock (sync)
            target = FindLocked(buffer);

        lock (target.Gate)
        {
            target.Splitter.ChangeContainer(container);
            lock (sync)
            {
                target.MimeType = mimeType;
                ResetProcessingLocked(target);
            }
        }

        return true;
    }

    /// <summary>
    /// Sets a buffer's timestamp offset and append window: <c>configure</c>. Null leaves a value as it
    /// is; an unbounded window end is <see cref="double.PositiveInfinity"/>.
    /// </summary>
    public void Configure(uint buffer, double? timestampOffset, double? appendWindowStart, double? appendWindowEnd)
    {
        lock (sync)
        {
            AppendSourceBuffer target = FindLocked(buffer);
            if (timestampOffset is { } offset && double.IsFinite(offset))
                target.TimestampOffset = offset;
            if (appendWindowStart is { } start && !double.IsNaN(start))
                target.WindowStart = start;
            if (appendWindowEnd is { } end && !double.IsNaN(end))
                target.WindowEnd = end;
        }
    }

    /// <summary>
    /// Takes one <c>appendBuffer</c> call's bytes and answers with <c>appended</c> or <c>appendFailed</c>.
    /// Bytes that complete no initialization segment or media unit are only kept, without parsing.
    /// </summary>
    public AppendReply Append(uint buffer, uint sequence, ReadOnlySpan<byte> data)
    {
        AppendSourceBuffer target;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            target = FindLocked(buffer);
            ended = false;
        }

        var changes = new List<BufferedReply>();
        AppendReply reply;
        lock (target.Gate)
            reply = AppendCore(target, sequence, data, changes);
        Raise(changes);
        return reply;
    }

    /// <summary>Removes a presentation range from a buffer: <c>remove</c>. An unbounded end is <see cref="double.PositiveInfinity"/>.</summary>
    public RemovedReply Remove(uint buffer, long request, double start, double end)
    {
        lock (sync)
        {
            AppendSourceBuffer target = FindLocked(buffer);
            ended = false;
            var removed = new List<AppendFrame>();
            foreach (AppendTrackBuffer track in target.Tracks)
            {
                removed.Clear();
                track.RemovePresented(start, end, removed);
                ReleaseRemovedLocked(track, removed);
            }

            target.Full = target.Bytes > ShareLocked(target);
            IReadOnlyList<MediaTimeRange> ranges = BufferedLocked(target);
            target.Report(ranges);
            return new RemovedReply(buffer, request, ranges, target.Full);
        }
    }

    /// <summary>Drops a buffer's partial bytes, keeping its initialization segment: <c>abort</c>.</summary>
    public void Abort(uint buffer)
    {
        AppendSourceBuffer target;
        lock (sync)
            target = FindLocked(buffer);

        lock (target.Gate)
        {
            target.Splitter.Reset();
            lock (sync)
                ResetProcessingLocked(target);
        }
    }

    /// <summary>Sets the timeline length: <c>duration</c>. An unbounded one is <see cref="double.PositiveInfinity"/>.</summary>
    public void SetDuration(double value)
    {
        lock (sync)
        {
            duration = value;
            UpdateTimelineLocked();
        }

        RaiseTimeline();
    }

    /// <summary>
    /// Marks the media complete: <c>endOfStream</c>. With an error, reading fails instead, which ends
    /// playback with that error. An append or removal afterwards reopens the source.
    /// </summary>
    public void EndOfStream(EndOfStreamError error)
    {
        AppendSourceBuffer[] current;
        lock (sync)
            current = buffers.ToArray();

        var changes = new List<BufferedReply>();
        if (error == EndOfStreamError.None)
        {
            // Only the end of the stream delimits an unknown-size WebM Cluster.
            foreach (AppendSourceBuffer target in current)
            {
                lock (target.Gate)
                {
                    var units = new List<SplitUnit>();
                    try
                    {
                        target.Splitter.Finish(units);
                        ProcessUnits(target, units, changes, out _);
                    }
                    catch (InvalidDataException exception)
                    {
                        AppLog.Warning("Browser.Source", $"Buffer {target.Id} ended inside a media segment.", exception);
                    }
                    finally
                    {
                        foreach (SplitUnit unit in units)
                            unit.Release();
                    }
                }
            }
        }

        lock (sync)
        {
            ended = true;
            failure = error;
            if (error == EndOfStreamError.None)
            {
                double end = 0;
                foreach (AppendSourceBuffer target in buffers)
                {
                    IReadOnlyList<MediaTimeRange> ranges = BufferedLocked(target);
                    if (ranges.Count > 0)
                        end = Math.Max(end, ranges[^1].End);
                }

                if (end > 0 && !live)
                    duration = end;
            }

            UpdateTimelineLocked();
            CollectChangesLocked(null, changes);
        }

        Raise(changes);
        RaiseTimeline();
    }

    /// <summary>Sets whether the media is live and its seekable window: <c>timeline</c>.</summary>
    public void SetTimeline(bool isLive, double seekableStartSeconds, double seekableEndSeconds)
    {
        lock (sync)
        {
            live = isLive;
            seekableStart = double.IsFinite(seekableStartSeconds) ? seekableStartSeconds : 0;
            seekableEnd = double.IsFinite(seekableEndSeconds) ? seekableEndSeconds : seekableStart;
            UpdateTimelineLocked();
        }

        RaiseTimeline();
    }

    /// <summary>Current buffered ranges of a buffer and whether it is full.</summary>
    public BufferedReply GetBuffered(uint buffer)
    {
        lock (sync)
        {
            AppendSourceBuffer target = FindLocked(buffer);
            return new BufferedReply(buffer, BufferedLocked(target), target.Full);
        }
    }

    public IPacketSource OpenMain()
    {
        AppendPacketSource opened;
        lock (sync)
        {
            while (!ReadyLocked())
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                lifetime.Token.ThrowIfCancellationRequested();
                Monitor.Wait(sync, 100);
            }

            source?.Detach();
            opened = new AppendPacketSource(this);
            source = opened;
            BindLocked();
        }

        RaiseLoadStage();
        return opened;
    }

    public IPacketSource OpenSidecar(string path) =>
        throw new NotSupportedException("Browser media has no sidecar files.");

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default) =>
        throw new NotSupportedException("Browser media carries no subtitle streams.");

    public BitmapSubtitleDocument ReadBitmapSubtitles(int streamIndex, string? sidecarPath, CancellationToken token = default) =>
        throw new NotSupportedException("Browser media carries no subtitle streams.");

    public Stream OpenFile(string path) =>
        throw new NotSupportedException("Browser media has no files.");

    public void CancelPendingOperations()
    {
        lock (sync)
        {
            if (!disposed)
                lifetime.Cancel();
            Monitor.PulseAll(sync);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            lifetime.Cancel();
            source?.Detach();
            source = null;
            foreach (AppendSourceBuffer buffer in buffers)
                DropBufferLocked(buffer);
            buffers.Clear();
            Monitor.PulseAll(sync);
        }

        pool.Clear();
        lifetime.Dispose();
    }

    /// <summary>Whether every declared buffer has an initialization segment, so playback can be set up.</summary>
    internal bool ReadyLocked() => buffers.Count > 0 && buffers.All(buffer => buffer.Tracks.Count > 0);

    /// <summary>Whether a packet source set up before <see cref="Reset"/> can take the new buffers.</summary>
    internal bool CanRebindLocked() => resetKinds is not { } kinds || kinds == KindsLocked();

    private TrackKinds KindsLocked()
    {
        TrackKinds kinds = TrackKinds.None;
        foreach (AppendSourceBuffer buffer in buffers)
        {
            foreach (AppendTrackBuffer track in buffer.Tracks)
                kinds |= track.Kind == MediaStreamKind.Video ? TrackKinds.Video : TrackKinds.Audio;
        }

        return kinds;
    }

    /// <summary>Gives the first video and the first audio track their stream indices and describes them.</summary>
    internal void BindLocked()
    {
        AppendTrackBuffer? video = null, audio = null;
        foreach (AppendSourceBuffer buffer in buffers)
        {
            foreach (AppendTrackBuffer track in buffer.Tracks)
            {
                track.StreamIndex = -1;
                if (track.Kind == MediaStreamKind.Video && video is null)
                    video = track;
                else if (track.Kind == MediaStreamKind.Audio && audio is null)
                    audio = track;
            }
        }

        if (video is not null)
            video.StreamIndex = AppendPacketSource.VideoStream;
        if (audio is not null)
            audio.StreamIndex = AppendPacketSource.AudioStream;
        bound = true;
        boundKinds = KindsLocked();
        resetKinds = null;
        source?.Bind(video, audio);
        SetLoadStageLocked(StreamingLoadStage.ReadingStreamInformation);
    }

    /// <summary>Describes the bound tracks with the setups decoders are built from.</summary>
    internal MediaInfo DescribeLocked(AppendTrackSetup? video, AppendTrackBuffer? videoTrack, AppendTrackSetup? audio)
    {
        var streams = new List<MediaStreamInfo>(2);
        MediaStreamInfo? videoInfo = null, audioInfo = null;
        if (video is not null)
        {
            CodecParameters parameters = video.Parameters;
            double frameRate = parameters.FrameRate;
            if (!(frameRate > 0) && videoTrack is { TypicalDuration: > 0 })
                frameRate = 1 / videoTrack.Seconds(videoTrack.TypicalDuration);
            videoInfo = new MediaStreamInfo(AppendPacketSource.VideoStream, MediaStreamKind.Video, parameters.CodecName, null!, null!, true)
            {
                Width = parameters.Width,
                Height = parameters.Height,
                PixelAspectRatio = parameters.PixelAspectRatio,
                Rotation = parameters.Rotation,
                MirrorHorizontally = parameters.MirrorHorizontally,
                FrameRate = frameRate,
                PixelFormat = parameters.PixelFormat,
            };
            streams.Add(videoInfo);
        }

        if (audio is not null)
        {
            CodecParameters parameters = audio.Parameters;
            audioInfo = new MediaStreamInfo(AppendPacketSource.AudioStream, MediaStreamKind.Audio, parameters.CodecName, null!, null!, true)
            {
                SampleRate = parameters.SampleRate,
                ChannelCount = parameters.ChannelCount,
                SampleFormat = parameters.SampleFormat,
            };
            streams.Add(audioInfo);
        }

        string format = buffers.Count > 0 && buffers.All(buffer => buffer.Splitter.Container == SegmentContainer.WebM)
            ? "Media Source (WebM)"
            : buffers.All(buffer => buffer.Splitter.Container == SegmentContainer.Mp4)
                ? "Media Source (fragmented MP4)"
                : "Media Source";
        // The length is left to the timeline: the page can change it after playback is set up.
        return new MediaInfo(title, format, TimeSpan.Zero, 0, true, streams, new Dictionary<string, string>())
        {
            DefaultVideoStream = videoInfo,
            DefaultAudioStream = audioInfo,
        };
    }

    /// <summary>Records where playback is, which is what eviction keeps data after.</summary>
    internal void OnPlaybackPosition(double position)
    {
        List<BufferedReply>? changes = null;
        lock (sync)
        {
            playbackPosition = position;
            if (double.IsNaN(lastEvictionPosition) || Math.Abs(position - lastEvictionPosition) >= EvictionStepSeconds)
            {
                bool over = buffers.Any(buffer => buffer.Full || buffer.Bytes > ShareLocked(buffer));
                if (over)
                {
                    lastEvictionPosition = position;
                    changes = new List<BufferedReply>();
                    EvictLocked();
                    CollectChangesLocked(null, changes);
                }
            }
        }

        if (changes is not null)
            Raise(changes);
    }

    internal void Detached(AppendPacketSource detached)
    {
        lock (sync)
        {
            if (ReferenceEquals(source, detached))
                source = null;
        }
    }

    private AppendReply AppendCore(AppendSourceBuffer target, uint sequence, ReadOnlySpan<byte> data, List<BufferedReply> changes)
    {
        var units = new List<SplitUnit>();
        try
        {
            try
            {
                target.Splitter.Push(data, units);
            }
            catch (InvalidDataException exception)
            {
                AppLog.Warning("Browser.Source", $"Buffer {target.Id} received a malformed byte stream.", exception);
                target.Splitter.Reset();
                return new AppendFailedReply(target.Id, sequence, AppendFailureReason.Decode);
            }

            if (units.Count == 0)
            {
                lock (sync)
                    return new AppendedReply(target.Id, sequence, target.ReportedRanges, target.Full, null);
            }

            AppendFailureReason? failed = ProcessUnits(target, units, changes, out IReadOnlyList<AppendedTrack>? tracks);
            if (failed is { } reason)
            {
                target.Splitter.Reset();
                return new AppendFailedReply(target.Id, sequence, reason);
            }

            lock (sync)
            {
                IReadOnlyList<MediaTimeRange> ranges = BufferedLocked(target);
                target.Report(ranges);
                return new AppendedReply(target.Id, sequence, ranges, target.Full, tracks);
            }
        }
        finally
        {
            foreach (SplitUnit unit in units)
                unit.Release();
        }
    }

    /// <summary>Parses and applies complete units in order. Runs under the buffer's gate.</summary>
    private AppendFailureReason? ProcessUnits(
        AppendSourceBuffer target,
        List<SplitUnit> units,
        List<BufferedReply> changes,
        out IReadOnlyList<AppendedTrack>? tracks)
    {
        tracks = null;
        var packets = new List<MediaPacket>();
        foreach (SplitUnit unit in units)
        {
            if (unit.IsInitialization)
            {
                if (unit.Encrypted)
                    return AppendFailureReason.Encrypted;

                SegmentContainer container = target.Splitter.Container;
                if (target.Initialization is { } current && container == target.InitializationContainer &&
                    unit.Bytes.SequenceEqual(current))
                {
                    lock (sync)
                        tracks = target.DescribeTracks();
                    continue;
                }

                IReadOnlyList<SegmentTrack> declared;
                try
                {
                    declared = SegmentParser.ReadTracks(container, unit.Bytes);
                }
                catch (InvalidDataException exception)
                {
                    AppLog.Warning("Browser.Source", $"Buffer {target.Id} sent an unreadable initialization segment.", exception);
                    return AppendFailureReason.Decode;
                }

                lock (sync)
                {
                    if (target.Removed)
                        return null;
                    AppendFailureReason? refused = ApplyInitializationLocked(target, unit.Data, container, declared);
                    if (refused is not null)
                        return refused;
                    tracks = target.DescribeTracks();
                    Monitor.PulseAll(sync);
                }

                continue;
            }

            if (target.Initialization is null)
                return AppendFailureReason.Decode;

            try
            {
                SegmentParser.ReadPackets(target.InitializationContainer, target.Initialization, unit.Bytes, pool, packets);
            }
            catch (InvalidDataException exception)
            {
                AppLog.Warning("Browser.Source", $"Buffer {target.Id} sent an unreadable media segment.", exception);
                return AppendFailureReason.Decode;
            }

            lock (sync)
            {
                if (target.Removed)
                {
                    foreach (MediaPacket packet in packets)
                        packet.Recycle();
                    packets.Clear();
                    return null;
                }

                ApplyPacketsLocked(target, packets);
                packets.Clear();
            }
        }

        lock (sync)
        {
            if (target.Removed)
                return null;
            EvictLocked();
            CollectChangesLocked(target, changes);
            ExtendDurationLocked(BufferedLocked(target));
        }

        return null;
    }

    private AppendFailureReason? ApplyInitializationLocked(
        AppendSourceBuffer target,
        byte[] bytes,
        SegmentContainer container,
        IReadOnlyList<SegmentTrack> declared)
    {
        var media = declared
            .Where(track => track.Parameters.Kind is MediaStreamKind.Video or MediaStreamKind.Audio)
            .ToList();
        if (media.Count == 0)
            return AppendFailureReason.Decode;

        foreach (SegmentTrack track in media)
        {
            string? family = AppendSourceCapabilities.FamilyOfDecoder(track.Parameters.CodecName);
            if (family is null || !AppendSourceCapabilities.Accepts(container, family))
            {
                AppLog.Warning("Browser.Source", $"Buffer {target.Id} declares unsupported codec '{track.Parameters.CodecName}'.");
                return AppendFailureReason.Unsupported;
            }
        }

        bool first = target.Tracks.Count == 0;
        if (!first)
        {
            foreach (MediaStreamKind kind in new[] { MediaStreamKind.Video, MediaStreamKind.Audio })
            {
                if (media.Count(track => track.Parameters.Kind == kind) != target.Tracks.Count(track => track.Kind == kind))
                {
                    AppLog.Warning("Browser.Source", $"Buffer {target.Id} changed its number of {kind} tracks.");
                    return AppendFailureReason.Decode;
                }
            }
        }

        var bindings = new InitializationBinding?[declared.Count == 0 ? 0 : declared.Max(track => track.StreamIndex) + 1];
        var ordinals = new Dictionary<MediaStreamKind, int>();
        foreach (SegmentTrack declaredTrack in media)
        {
            MediaStreamKind kind = declaredTrack.Parameters.Kind;
            int ordinal = ordinals.GetValueOrDefault(kind);
            ordinals[kind] = ordinal + 1;

            AppendTrackBuffer track;
            AppendTrackSetup setup;
            if (first)
            {
                setup = new AppendTrackSetup(declaredTrack.Parameters with { InitialPadding = 0 }, declaredTrack.TrackId);
                track = new AppendTrackBuffer(target, kind, ordinal, setup);
                target.Tracks.Add(track);
            }
            else
            {
                track = target.Tracks.First(existing => existing.Kind == kind && existing.Ordinal == ordinal);
                setup = new AppendTrackSetup(
                    declaredTrack.Parameters with
                    {
                        InitialPadding = 0,
                        TimeBaseNum = track.TimeBaseNum,
                        TimeBaseDen = track.TimeBaseDen,
                    },
                    declaredTrack.TrackId);
                track.NoteSetup(setup);
            }

            bindings[declaredTrack.StreamIndex] = new InitializationBinding(
                track,
                setup,
                declaredTrack.Parameters.TimeBaseNum,
                declaredTrack.Parameters.TimeBaseDen);
        }

        target.Initialization = bytes;
        target.InitializationContainer = container;
        target.Bindings = bindings;
        return null;
    }

    /// <summary>
    /// The Media Source coded frame processing for one unit's packets: onto the track time base, through
    /// the timestamp offset and append window, past frames that cannot start decoding, and over what
    /// they overlap.
    /// </summary>
    private void ApplyPacketsLocked(AppendSourceBuffer target, List<MediaPacket> packets)
    {
        var byTrack = new Dictionary<AppendTrackBuffer, List<AppendFrame>>();
        foreach (MediaPacket packet in packets)
        {
            int index = packet.StreamIndex;
            InitializationBinding? found = index >= 0 && index < target.Bindings.Length ? target.Bindings[index] : null;
            long pts = packet.Pts, dts = packet.Dts;
            if (found is not { } binding || (pts == AppendPacketSource.NoTimestamp && dts == AppendPacketSource.NoTimestamp))
            {
                packet.Recycle();
                continue;
            }

            if (pts == AppendPacketSource.NoTimestamp)
                pts = dts;
            if (dts == AppendPacketSource.NoTimestamp)
                dts = pts;

            AppendTrackBuffer track = binding.Track;
            long offset = track.Ticks(target.TimestampOffset);
            pts = Rescale(pts, binding, track) + offset;
            dts = Rescale(dts, binding, track) + offset;
            long length = Math.Max(0, Rescale(packet.Duration, binding, track));
            packet.Retime(packet.StreamIndex, pts, dts, length);
            if (!byTrack.TryGetValue(track, out List<AppendFrame>? frames))
                byTrack[track] = frames = new List<AppendFrame>();
            frames.Add(new AppendFrame
            {
                Packet = packet,
                Setup = binding.Setup,
                Pts = pts,
                Dts = dts,
                Duration = length,
                IsKey = packet.IsKeyFrame,
                Size = packet.SizeInBytes,
            });
        }

        var removed = new List<AppendFrame>();
        foreach ((AppendTrackBuffer track, List<AppendFrame> frames) in byTrack)
        {
            DeriveDurations(track, frames);
            List<AppendFrame> accepted = ProcessFramesLocked(target, track, frames);
            if (accepted.Count == 0)
                continue;

            long first = long.MaxValue, last = long.MinValue;
            foreach (AppendFrame frame in accepted)
            {
                first = Math.Min(first, frame.RangeStart);
                last = Math.Max(last, frame.EstimatedDuration ? frame.RangeStart + 1 : frame.RangeEnd);
                track.NoteDuration(frame.EstimatedDuration ? 0 : frame.Duration);
            }

            removed.Clear();
            track.RemovePresented(first, last, removed);
            ReleaseRemovedLocked(track, removed);
            track.Insert(accepted);
        }
    }

    /// <summary>Gives frames without a duration the distance to the next presentation time; the last is estimated.</summary>
    private static void DeriveDurations(AppendTrackBuffer track, List<AppendFrame> frames)
    {
        if (!frames.Exists(frame => frame.Duration <= 0))
            return;

        var presented = frames.OrderBy(frame => frame.Pts).ToList();
        long known = frames.Max(frame => frame.Duration);
        for (int index = 0; index < presented.Count; index++)
        {
            AppendFrame frame = presented[index];
            if (frame.Duration > 0)
                continue;

            if (index + 1 < presented.Count && presented[index + 1].Pts > frame.Pts)
            {
                frame.Duration = presented[index + 1].Pts - frame.Pts;
            }
            else
            {
                frame.Duration = Math.Max(track.TypicalDuration, known);
                frame.EstimatedDuration = true;
            }

            frame.Packet.Retime(frame.Packet.StreamIndex, frame.Pts, frame.Dts, frame.Duration);
        }
    }

    private List<AppendFrame> ProcessFramesLocked(AppendSourceBuffer target, AppendTrackBuffer track, List<AppendFrame> frames)
    {
        var accepted = new List<AppendFrame>(frames.Count);
        foreach (AppendFrame frame in frames)
        {
            // A jump back or a gap of more than two frames is a discontinuity: decoding has to restart
            // at a keyframe on every track of the buffer.
            if (track.LastDecodeTimestamp is { } last &&
                (frame.Dts < last || (track.LastFrameDuration > 0 && frame.Dts - last > 2 * track.LastFrameDuration)))
            {
                foreach (AppendTrackBuffer each in target.Tracks)
                {
                    each.LastDecodeTimestamp = null;
                    each.LastFrameDuration = 0;
                    each.NeedRandomAccessPoint = true;
                }
            }

            double start = track.Seconds(frame.Pts);
            double end = track.Seconds(frame.Pts + frame.Duration);
            if (start < target.WindowStart)
            {
                // Audio straddling the window start is trimmed rather than dropped: this is what removes
                // priming at the start of a track.
                int sampleRate = frame.Setup.Parameters.SampleRate;
                if (track.Kind == MediaStreamKind.Audio && frame.Duration > 0 && end > target.WindowStart && sampleRate > 0)
                {
                    frame.Trim = track.Ticks(target.WindowStart) - frame.Pts;
                    frame.Packet.SetLeadingSkipSamples((int)Math.Round((target.WindowStart - start) * sampleRate));
                }
                else
                {
                    track.NeedRandomAccessPoint = true;
                    frame.Packet.Recycle();
                    continue;
                }
            }

            if (end > target.WindowEnd)
            {
                track.NeedRandomAccessPoint = true;
                frame.Packet.Recycle();
                continue;
            }

            if (track.NeedRandomAccessPoint)
            {
                if (!frame.IsKey)
                {
                    frame.Packet.Recycle();
                    continue;
                }

                track.NeedRandomAccessPoint = false;
            }

            track.LastDecodeTimestamp = frame.Dts;
            track.LastFrameDuration = frame.Duration;
            accepted.Add(frame);
        }

        return accepted;
    }

    private static long Rescale(long value, InitializationBinding binding, AppendTrackBuffer track)
    {
        if (binding.TimeBaseNum == track.TimeBaseNum && binding.TimeBaseDen == track.TimeBaseDen)
            return value;

        Int128 numerator = (Int128)value * binding.TimeBaseNum * track.TimeBaseDen;
        Int128 denominator = (Int128)binding.TimeBaseDen * track.TimeBaseNum;
        Int128 half = denominator / 2;
        return (long)(numerator >= 0 ? (numerator + half) / denominator : (numerator - half) / denominator);
    }

    /// <summary>
    /// Brings every buffer within its share by dropping whole groups of pictures that end before the
    /// playhead, oldest first, and updates full flags.
    /// </summary>
    private void EvictLocked()
    {
        double keep = (source?.KeepFromLocked(playbackPosition) ?? playbackPosition) - EvictionGuardSeconds;
        var removed = new List<AppendFrame>();
        foreach (AppendSourceBuffer buffer in buffers)
        {
            long share = ShareLocked(buffer);
            if (buffer.Bytes > share && double.IsFinite(keep))
                EvictBufferLocked(buffer, share, keep, removed);
            buffer.Full = buffer.Bytes > share;
        }
    }

    private void EvictBufferLocked(AppendSourceBuffer buffer, long share, double keep, List<AppendFrame> removed)
    {
        AppendTrackBuffer? primary = buffer.Tracks.FirstOrDefault(track => track.Kind == MediaStreamKind.Video)
            ?? buffer.Tracks.FirstOrDefault();
        if (primary is null || primary.Frames.Count == 0 || primary.Bytes == 0)
            return;

        // The boundary is a keyframe presented before the playhead; everything decoded before it goes.
        long needed = buffer.Bytes - share;
        double scale = buffer.Bytes / (double)primary.Bytes;
        long freed = 0;
        AppendFrame? boundary = null;
        IReadOnlyList<AppendFrame> frames = primary.Frames;
        for (int index = 0; index < frames.Count; index++)
        {
            AppendFrame frame = frames[index];
            if (index > 0 && frame.IsKey)
            {
                if (primary.Seconds(frame.RangeStart) > keep)
                    break;
                boundary = frame;
                if (freed * scale >= needed)
                    break;
            }

            freed += frame.Size;
        }

        if (boundary is null)
            return;

        removed.Clear();
        primary.RemoveBefore(boundary.Dts, removed);
        ReleaseRemovedLocked(primary, removed);
        double cut = primary.Seconds(boundary.RangeStart);
        foreach (AppendTrackBuffer track in buffer.Tracks)
        {
            if (ReferenceEquals(track, primary))
                continue;
            removed.Clear();
            track.RemovePresented(double.NegativeInfinity, cut, removed);
            ReleaseRemovedLocked(track, removed);
        }
    }

    private long ShareLocked(AppendSourceBuffer buffer)
    {
        bool anyVideo = buffers.Any(each => each.HasVideo);
        long Weight(AppendSourceBuffer each) => !anyVideo || each.HasVideo || each.Tracks.Count == 0 ? VideoBufferWeight : AudioBufferWeight;
        long total = buffers.Sum(Weight);
        return total == 0 ? BudgetBytes : BudgetBytes * Weight(buffer) / total;
    }

    /// <summary>Records <c>buffered</c> messages for buffers whose ranges or full flag changed.</summary>
    private void CollectChangesLocked(AppendSourceBuffer? except, List<BufferedReply> changes)
    {
        foreach (AppendSourceBuffer buffer in buffers)
        {
            if (ReferenceEquals(buffer, except) || buffer.Tracks.Count == 0)
                continue;
            IReadOnlyList<MediaTimeRange> ranges = BufferedLocked(buffer);
            if (buffer.ReportedFull == buffer.Full && buffer.ReportedRanges.SequenceEqual(ranges))
                continue;
            buffer.Report(ranges);
            changes.Add(new BufferedReply(buffer.Id, ranges, buffer.Full));
        }
    }

    /// <summary>
    /// A buffer's ranges: the intersection of its tracks' ranges. After the end of the stream every
    /// track's last range reaches the latest end among them.
    /// </summary>
    private IReadOnlyList<MediaTimeRange> BufferedLocked(AppendSourceBuffer buffer)
    {
        if (buffer.Tracks.Count == 0)
            return Array.Empty<MediaTimeRange>();

        double highest = double.NegativeInfinity;
        if (ended)
        {
            foreach (AppendTrackBuffer track in buffer.Tracks)
            {
                if (track.Ranges.Count > 0)
                    highest = Math.Max(highest, track.Ranges[^1].End);
            }
        }

        List<MediaTimeRange>? result = null;
        foreach (AppendTrackBuffer track in buffer.Tracks)
        {
            var ranges = new List<MediaTimeRange>(track.Ranges);
            if (ended && ranges.Count > 0)
                ranges[^1] = new MediaTimeRange(ranges[^1].Start, Math.Max(ranges[^1].End, highest));
            result = result is null ? ranges : Intersect(result, ranges);
        }

        return result ?? (IReadOnlyList<MediaTimeRange>)Array.Empty<MediaTimeRange>();
    }

    private static List<MediaTimeRange> Intersect(List<MediaTimeRange> left, List<MediaTimeRange> right)
    {
        var result = new List<MediaTimeRange>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count)
        {
            double start = Math.Max(left[i].Start, right[j].Start);
            double end = Math.Min(left[i].End, right[j].End);
            if (end > start)
                result.Add(new MediaTimeRange(start, end));
            if (left[i].End < right[j].End)
                i++;
            else
                j++;
        }

        return result;
    }

    /// <summary>Media appended past the duration lengthens it, as in a Media Source.</summary>
    private void ExtendDurationLocked(IReadOnlyList<MediaTimeRange> ranges)
    {
        if (live || !double.IsFinite(duration))
            return;
        if (ranges.Count > 0 && ranges[^1].End > duration)
        {
            duration = ranges[^1].End;
            UpdateTimelineLocked();
        }
    }

    private void ReleaseRemovedLocked(AppendTrackBuffer track, List<AppendFrame> removed)
    {
        if (removed.Count == 0)
            return;
        source?.OnRemovedLocked(track, removed);
        foreach (AppendFrame frame in removed)
            frame.Packet.Recycle();
        removed.Clear();
    }

    private void DropBufferLocked(AppendSourceBuffer buffer)
    {
        buffer.Removed = true;
        var removed = new List<AppendFrame>();
        foreach (AppendTrackBuffer track in buffer.Tracks)
        {
            track.Clear(removed);
            source?.OnTrackRemovedLocked(track);
            foreach (AppendFrame frame in removed)
                frame.Packet.Recycle();
            removed.Clear();
        }
    }

    private static void ResetProcessingLocked(AppendSourceBuffer buffer)
    {
        foreach (AppendTrackBuffer track in buffer.Tracks)
        {
            track.LastDecodeTimestamp = null;
            track.LastFrameDuration = 0;
            track.NeedRandomAccessPoint = true;
        }
    }

    private AppendSourceBuffer FindLocked(uint buffer) =>
        buffers.FirstOrDefault(existing => existing.Id == buffer)
        ?? throw new ArgumentException($"No buffer {buffer} is declared in this session.", nameof(buffer));

    private void UpdateTimelineLocked()
    {
        StreamingTimeline next = BuildTimelineLocked();
        if (next == timeline)
            return;
        timeline = next;
        timelineChanged = true;
    }

    private StreamingTimeline BuildTimelineLocked() => live
        ? new StreamingTimeline(true, seekableStart, seekableEnd, 0, seekableEnd > seekableStart, seekableEnd)
        : new StreamingTimeline(false, 0, double.IsFinite(duration) && duration > 0 ? duration : 0, 0, true);

    private bool timelineChanged;
    private bool loadStageChanged;

    private void SetLoadStageLocked(StreamingLoadStage value)
    {
        if (loadStage == value)
            return;
        loadStage = value;
        loadStageChanged = true;
    }

    private void RaiseTimeline()
    {
        bool raise;
        lock (sync)
        {
            raise = timelineChanged;
            timelineChanged = false;
        }

        if (raise)
            TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseLoadStage()
    {
        bool raise;
        lock (sync)
        {
            raise = loadStageChanged;
            loadStageChanged = false;
        }

        if (raise)
            LoadStageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Raise(List<BufferedReply> changes)
    {
        RaiseTimeline();
        foreach (BufferedReply change in changes)
            BufferedChanged?.Invoke(this, change);
    }
}

/// <summary>Where one stream of a buffer's initialization segment goes, and the time base its packets are in.</summary>
internal readonly record struct InitializationBinding(AppendTrackBuffer Track, AppendTrackSetup Setup, int TimeBaseNum, int TimeBaseDen);

/// <summary>One declared buffer: its byte stream, its tracks and its timestamp mapping.</summary>
internal sealed class AppendSourceBuffer
{
    public AppendSourceBuffer(uint id, string mimeType, SegmentContainer container)
    {
        Id = id;
        MimeType = mimeType;
        Splitter = new AppendSegmentSplitter(container);
    }

    public uint Id { get; }

    public string MimeType { get; set; }

    /// <summary>Serializes splitting and parsing; taken before the provider's lock, never inside it.</summary>
    public object Gate { get; } = new();

    public AppendSegmentSplitter Splitter { get; }

    public byte[]? Initialization { get; set; }

    public SegmentContainer InitializationContainer { get; set; }

    public InitializationBinding?[] Bindings { get; set; } = [];

    public List<AppendTrackBuffer> Tracks { get; } = new();

    public double TimestampOffset { get; set; }

    public double WindowStart { get; set; }

    public double WindowEnd { get; set; } = double.PositiveInfinity;

    public bool Full { get; set; }

    public bool Removed { get; set; }

    public IReadOnlyList<MediaTimeRange> ReportedRanges { get; private set; } = Array.Empty<MediaTimeRange>();

    public bool ReportedFull { get; private set; }

    public long Bytes => Tracks.Sum(track => track.Bytes);

    public bool HasVideo => Tracks.Exists(track => track.Kind == MediaStreamKind.Video);

    public void Report(IReadOnlyList<MediaTimeRange> ranges)
    {
        ReportedRanges = ranges;
        ReportedFull = Full;
    }

    public IReadOnlyList<AppendedTrack> DescribeTracks()
    {
        var tracks = new List<AppendedTrack>();
        foreach (InitializationBinding? entry in Bindings)
        {
            if (entry is not { } binding)
                continue;
            CodecParameters parameters = binding.Setup.Parameters;
            string family = AppendSourceCapabilities.FamilyOfDecoder(parameters.CodecName) ?? parameters.CodecName;
            tracks.Add(binding.Track.Kind == MediaStreamKind.Video
                ? new AppendedTrack(binding.Setup.TrackId, "video", family, parameters.Width, parameters.Height, 0, 0)
                : new AppendedTrack(binding.Setup.TrackId, "audio", family, 0, 0, parameters.SampleRate, parameters.ChannelCount));
        }

        return tracks;
    }
}
