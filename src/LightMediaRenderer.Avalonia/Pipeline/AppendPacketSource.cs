using System;
using System.Collections.Generic;
using System.IO;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Reads an <see cref="AppendMediaSourceProvider"/>'s track buffers in decode order. Reading waits at
/// the end of a buffered range, a seek outside the buffered ranges completes once data covering it
/// arrives, and a change of codec is reported as a reconfiguration once playback reaches it.
/// </summary>
/// <remarks>
/// Packets are handed out by reference, so side data survives. A same-codec switch of setup rides on
/// the next packet as new-extradata side data for codecs that take it there; once a track has used
/// more than one setup, every video keyframe and the first packet after a seek carry their setup too,
/// so a decoder that missed the switch, or was rebuilt, picks it up.
/// </remarks>
internal sealed class AppendPacketSource : IBufferedPacketSource, IReconfigurablePacketSource
{
    public const int VideoStream = 0;
    public const int AudioStream = 1;

    /// <summary><c>AV_NOPTS_VALUE</c>.</summary>
    public const long NoTimestamp = long.MinValue;

    // Packets are released this far past the playhead, so a removal or replacement just ahead of it
    // still reaches the decoders.
    private const double ReadAheadSeconds = 3;

    // A codec change waits for the playhead to come this close, or to stall this close for a moment:
    // a decoder holds its last frames for reordering and frame threading until it gets more input, so
    // playback stops short of the change by that much.
    private const double SwitchArrivalSeconds = 0.05;
    private const double SwitchStallWindowSeconds = 1.5;
    private const double SwitchStallSeconds = 0.3;

    private readonly AppendMediaSourceProvider owner;
    private readonly Slot video = new(VideoStream);
    private readonly Slot audio = new(AudioStream);
    private MediaInfo info;
    private bool audioSelected = true;
    private bool positioned;
    private double seekTarget = double.NaN;
    private double readStart = double.NaN;
    private double playbackPosition = double.NaN;
    private double positionChangedAt;
    private double reconfigurationPosition = double.NaN;
    private bool awaitingRebind;
    private bool rebound;
    private bool detached;

    public AppendPacketSource(AppendMediaSourceProvider owner)
    {
        this.owner = owner;
        info = new MediaInfo("Browser media", "Media Source", TimeSpan.Zero, 0, true,
            Array.Empty<MediaStreamInfo>(), new Dictionary<string, string>());
    }

    private enum Readiness
    {
        Ready,
        Wait,
        End,
        Switch,
    }

    public MediaInfo Info
    {
        get
        {
            lock (owner.Sync)
                return info;
        }
    }

    public int VideoStreamIndex
    {
        get
        {
            lock (owner.Sync)
                return video.Track is not null || info.HasVideo ? VideoStream : -1;
        }
    }

    public int AudioStreamIndex
    {
        get
        {
            lock (owner.Sync)
                return audioSelected && (audio.Track is not null || info.HasAudio) ? AudioStream : -1;
        }
    }

    public bool CanSeek => true;

    public double ReconfigurationPositionSeconds
    {
        get
        {
            lock (owner.Sync)
                return reconfigurationPosition;
        }
    }

    public double BufferedSeconds
    {
        get
        {
            lock (owner.Sync)
            {
                double reference = ReferenceLocked();
                double end = double.PositiveInfinity;
                foreach (Slot slot in ActiveSlots())
                {
                    AppendTrackBuffer track = slot.Track!;
                    int range = track.FindRange(reference, track.GapToleranceSeconds);
                    if (range < 0)
                        return MediaTime.NoTimestamp;
                    end = Math.Min(end, track.Ranges[range].End);
                }

                return double.IsFinite(end) ? end : MediaTime.NoTimestamp;
            }
        }
    }

    public double HistoryStartSeconds
    {
        get
        {
            lock (owner.Sync)
            {
                double reference = ReferenceLocked();
                double start = double.NegativeInfinity;
                foreach (Slot slot in ActiveSlots())
                {
                    AppendTrackBuffer track = slot.Track!;
                    int range = track.FindRange(reference, track.GapToleranceSeconds);
                    if (range < 0)
                        return MediaTime.NoTimestamp;
                    start = Math.Max(start, track.Ranges[range].Start);
                }

                return double.IsFinite(start) ? start : MediaTime.NoTimestamp;
            }
        }
    }

    public double EstimatedBandwidthBitsPerSecond => 0;

    public CodecParameters GetCodecParameters(int streamIndex)
    {
        lock (owner.Sync)
        {
            Slot slot = streamIndex switch
            {
                VideoStream => video,
                AudioStream => audio,
                _ => throw new ArgumentOutOfRangeException(nameof(streamIndex)),
            };
            // The setup of what is being read, which is what a decoder rebuilt now has to start from.
            return (slot.Delivered ?? slot.Configured)?.Parameters
                ?? throw new ArgumentOutOfRangeException(nameof(streamIndex), "The media has no such stream.");
        }
    }

    public void SelectAudioStream(int streamIndex)
    {
        lock (owner.Sync)
            audioSelected = streamIndex == AudioStream;
    }

    public PacketReadStatus Read(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (owner.Sync)
        {
            if (detached)
                return PacketReadStatus.Fatal;
            if (owner.Failure != EndOfStreamError.None)
            {
                throw new IOException(owner.Failure == EndOfStreamError.Network
                    ? "The page could not load the media."
                    : "The page reported the media as undecodable.");
            }

            if (awaitingRebind)
            {
                if (!owner.ReadyLocked() || !owner.CanRebindLocked())
                    return PacketReadStatus.WouldBlock;
                owner.BindLocked();
            }

            if (rebound)
            {
                // A new source starts where its media starts, unless playback was sent elsewhere.
                double target = TargetLocked();
                if (!MediaTime.IsValid(target))
                    return PacketReadStatus.WouldBlock;
                rebound = false;
                Reconfigure(target);
                return PacketReadStatus.Reconfigured;
            }

            if (!positioned && !TryPositionLocked())
            {
                return owner.Ended && BeyondEndLocked(TargetLocked())
                    ? PacketReadStatus.EndOfFile
                    : PacketReadStatus.WouldBlock;
            }

            Readiness videoState = Candidate(video, out AppendFrame? videoFrame);
            AppendFrame? audioFrame = null;
            Readiness audioState = Readiness.End;
            if (audioSelected)
                audioState = Candidate(audio, out audioFrame);

            // Nothing past a codec change is delivered until playback has reached it.
            double switchAt = double.PositiveInfinity;
            if (videoState == Readiness.Switch)
                switchAt = Math.Min(switchAt, video.Track!.Seconds(videoFrame!.RangeStart));
            if (audioState == Readiness.Switch)
                switchAt = Math.Min(switchAt, audio.Track!.Seconds(audioFrame!.RangeStart));

            Slot? chosen = null;
            AppendFrame? next = null;
            double chosenTime = double.PositiveInfinity;
            if (videoState == Readiness.Ready && Before(video, videoFrame!, switchAt))
            {
                chosen = video;
                next = videoFrame;
                chosenTime = video.Track!.Seconds(videoFrame!.Dts);
            }

            if (audioState == Readiness.Ready && Before(audio, audioFrame!, switchAt) &&
                audio.Track!.Seconds(audioFrame!.Dts) < chosenTime)
            {
                chosen = audio;
                next = audioFrame;
            }

            if (chosen is not null)
            {
                Deliver(chosen, next!, packet);
                return PacketReadStatus.Ok;
            }

            if (double.IsFinite(switchAt))
            {
                if (!SwitchDueLocked(switchAt))
                    return PacketReadStatus.WouldBlock;
                Reconfigure(switchAt);
                return PacketReadStatus.Reconfigured;
            }

            return videoState == Readiness.End && audioState == Readiness.End
                ? PacketReadStatus.EndOfFile
                : PacketReadStatus.WouldBlock;
        }
    }

    public bool Seek(double positionSeconds)
    {
        lock (owner.Sync)
        {
            if (detached)
                return false;
            seekTarget = positionSeconds;
            positioned = false;
            foreach (Slot slot in new[] { video, audio })
                slot.Forget();
        }

        return true;
    }

    public double ToSeconds(int streamIndex, long timestamp)
    {
        if (timestamp == NoTimestamp)
            return MediaTime.NoTimestamp;
        lock (owner.Sync)
        {
            AppendTrackBuffer? track = streamIndex switch
            {
                VideoStream => video.Track,
                AudioStream => audio.Track,
                _ => null,
            };
            return track?.Seconds(timestamp) ?? MediaTime.NoTimestamp;
        }
    }

    public void UpdatePlaybackPosition(double positionSeconds)
    {
        if (!MediaTime.IsValid(positionSeconds))
            return;

        lock (owner.Sync)
        {
            if (detached)
                return;
            if (positionSeconds != playbackPosition)
            {
                playbackPosition = positionSeconds;
                positionChangedAt = MediaTime.Now;
            }
        }

        owner.OnPlaybackPosition(positionSeconds);
    }

    public void Dispose() => Detach();

    internal void Detach()
    {
        lock (owner.Sync)
        {
            if (detached)
                return;
            detached = true;
            video.Track = audio.Track = null;
        }

        owner.Detached(this);
    }

    /// <summary>Takes the tracks the provider bound; after a reset this is the reconfiguration.</summary>
    internal void Bind(AppendTrackBuffer? videoTrack, AppendTrackBuffer? audioTrack)
    {
        video.Track = videoTrack;
        audio.Track = audioTrack;
        if (awaitingRebind)
        {
            awaitingRebind = false;
            rebound = true;
            return;
        }

        double start = EarliestCommonStartLocked();
        video.Configured = SetupAt(video, start);
        audio.Configured = SetupAt(audio, start);
        info = owner.DescribeLocked(video.Configured, video.Track, audio.Configured);
    }

    /// <summary>Called by a repeated <c>open</c>: everything buffered is gone until new buffers are bound.</summary>
    internal void OnReset()
    {
        awaitingRebind = true;
        rebound = false;
        positioned = false;
        seekTarget = double.NaN;
        video.Track = audio.Track = null;
        video.Forget();
        audio.Forget();
    }

    /// <summary>A removal hit frames of the group of pictures being decoded, so delivery resynchronizes.</summary>
    internal void OnRemovedLocked(AppendTrackBuffer track, List<AppendFrame> removed)
    {
        if (!ReferenceEquals(video.Track, track) || !video.Delivering)
            return;
        foreach (AppendFrame frame in removed)
        {
            if (frame.Dts >= video.GopDts && frame.Dts <= video.Cursor)
            {
                video.Resync = true;
                return;
            }
        }
    }

    internal void OnTrackRemovedLocked(AppendTrackBuffer track)
    {
        if (ReferenceEquals(video.Track, track))
            video.Track = null;
        if (ReferenceEquals(audio.Track, track))
            audio.Track = null;
    }

    /// <summary>The earliest time eviction must keep: the playhead, or the group being read if earlier.</summary>
    internal double KeepFromLocked(double position)
    {
        double keep = position;
        if (!positioned && MediaTime.IsValid(seekTarget))
            keep = MediaTime.IsValid(keep) ? Math.Min(keep, seekTarget) : seekTarget;

        // Delivery runs ahead of the playhead, so it only holds back a position that is known.
        if (!MediaTime.IsValid(keep))
            return keep;
        foreach (Slot slot in ActiveSlots())
        {
            if (slot.Delivering && MediaTime.IsValid(slot.GopStart))
                keep = Math.Min(keep, slot.GopStart);
        }

        return keep;
    }

    private IEnumerable<Slot> ActiveSlots()
    {
        if (video.Track is not null)
            yield return video;
        if (audioSelected && audio.Track is not null)
            yield return audio;
    }

    private double ReferenceLocked()
    {
        if (!positioned && MediaTime.IsValid(seekTarget))
            return seekTarget;
        if (MediaTime.IsValid(playbackPosition))
            return playbackPosition;
        return MediaTime.IsValid(readStart) ? readStart : EarliestCommonStartLocked();
    }

    private double TargetLocked() => MediaTime.IsValid(seekTarget) ? seekTarget : EarliestCommonStartLocked();

    /// <summary>The first time every active track has data at, which is where reading starts unasked.</summary>
    private double EarliestCommonStartLocked()
    {
        double start = double.NaN;
        foreach (Slot slot in ActiveSlots())
        {
            IReadOnlyList<MediaTimeRange> ranges = slot.Track!.Ranges;
            if (ranges.Count == 0)
                continue;
            start = double.IsNaN(start) ? ranges[0].Start : Math.Max(start, ranges[0].Start);
        }

        return start;
    }

    private bool BeyondEndLocked(double target)
    {
        if (!MediaTime.IsValid(target))
            return true;
        foreach (Slot slot in ActiveSlots())
        {
            IReadOnlyList<MediaTimeRange> ranges = slot.Track!.Ranges;
            if (ranges.Count > 0 && ranges[^1].End > target + slot.Track.GapToleranceSeconds)
                return false;
        }

        return true;
    }

    /// <summary>Places every active track's cursor at the frame decoding has to start from for the target.</summary>
    private bool TryPositionLocked()
    {
        double target = TargetLocked();
        if (!MediaTime.IsValid(target))
            return false;

        var starts = new List<(Slot Slot, AppendFrame? Frame)>(2);
        foreach (Slot slot in ActiveSlots())
        {
            AppendFrame? start = FindStart(slot.Track!, target);
            if (start is null)
            {
                if (owner.Ended && BeyondEndLocked(target))
                {
                    starts.Add((slot, null));
                    continue;
                }

                return false;
            }

            starts.Add((slot, start));
        }

        if (starts.Count == 0)
            return false;

        foreach ((Slot slot, AppendFrame? start) in starts)
        {
            slot.Forget();
            if (start is null)
            {
                slot.Exhausted = true;
                continue;
            }

            slot.Cursor = start.Dts - 1;
            slot.GopDts = start.Dts;
            slot.GopStart = slot.Track!.Seconds(start.RangeStart);
        }

        positioned = true;
        readStart = target;
        seekTarget = double.NaN;
        return true;
    }

    /// <summary>
    /// The frame decoding starts from for <paramref name="target"/>: the last keyframe presented at or
    /// before it in the buffered range that covers it (any frame for audio), or null when none covers it.
    /// </summary>
    private static AppendFrame? FindStart(AppendTrackBuffer track, double target)
    {
        double tolerance = track.GapToleranceSeconds;
        int index = track.FindRange(target, tolerance);
        if (index < 0)
            return null;

        MediaTimeRange range = track.Ranges[index];
        double point = Math.Max(target, range.Start);
        bool needsKey = track.Kind == MediaStreamKind.Video;
        AppendFrame? best = null, first = null;
        double bestTime = double.NegativeInfinity, firstTime = double.PositiveInfinity;
        foreach (AppendFrame frame in track.Frames)
        {
            if (needsKey && !frame.IsKey)
                continue;
            double time = track.Seconds(frame.RangeStart);
            if (time < range.Start - tolerance || time >= range.End)
                continue;
            if (time <= point + 1e-9 && time > bestTime)
            {
                best = frame;
                bestTime = time;
            }

            if (time < firstTime)
            {
                first = frame;
                firstTime = time;
            }
        }

        return best ?? first;
    }

    private static AppendTrackSetup? SetupAt(Slot slot, double time)
    {
        if (slot.Track is not { } track)
            return null;
        if (MediaTime.IsValid(time) && FindStart(track, time) is { } start)
            return start.Setup;
        return track.Frames.Count > 0 ? track.Frames[0].Setup : track.LatestSetup;
    }

    private Readiness Candidate(Slot slot, out AppendFrame? frame)
    {
        frame = null;
        if (slot.Track is not { } track || slot.Exhausted)
            return Readiness.End;

        if (slot.Resync)
            Resynchronize(slot, track);

        int index = track.FirstAfter(slot.Cursor);
        IReadOnlyList<AppendFrame> frames = track.Frames;
        while (slot.NeedKey && index < frames.Count && !frames[index].IsKey)
            slot.Cursor = frames[index++].Dts;
        if (index >= frames.Count)
            return owner.Ended ? Readiness.End : Readiness.Wait;

        AppendFrame candidate = frames[index];
        double start = track.Seconds(candidate.RangeStart);
        if (slot.Delivering)
        {
            // Past the end of the range playback is in lies a gap that a page player seeks over.
            int range = track.FindRange(slot.Anchor);
            if (range >= 0 && start >= track.Ranges[range].End)
                return Readiness.Wait;
        }

        double reference = MediaTime.IsValid(playbackPosition) ? Math.Max(playbackPosition, readStart) : readStart;
        if (MediaTime.IsValid(reference) && track.Seconds(candidate.Dts) > reference + ReadAheadSeconds)
            return Readiness.Wait;

        frame = candidate;
        if (slot.Configured is { } configured && !CanContinue(configured, candidate.Setup))
            return Readiness.Switch;
        return Readiness.Ready;
    }

    /// <summary>Whether a decoder built for <paramref name="configured"/> can go on into <paramref name="next"/>.</summary>
    private static bool CanContinue(AppendTrackSetup configured, AppendTrackSetup next)
    {
        if (!configured.SameCodec(next))
            return false;

        // These either take new setup on a packet or carry it in the bitstream.
        return configured.CodecName is "h264" or "hevc" or "aac" or "av1" or "vp9" or "vp8" ||
            configured.SameExtradata(next);
    }

    private static bool TakesSetupOnPacket(AppendTrackSetup setup) =>
        setup.CodecName is "h264" or "hevc" or "aac" && setup.Parameters.Extradata.Length > 0;

    private static bool Before(Slot slot, AppendFrame frame, double switchAt) =>
        !double.IsFinite(switchAt) || slot.Track!.Seconds(frame.Dts) < switchAt;

    /// <summary>
    /// After a removal under the read position, decoding restarts at the keyframe at or before what was
    /// last delivered in the new content, or at the next keyframe when there is none.
    /// </summary>
    private void Resynchronize(Slot slot, AppendTrackBuffer track)
    {
        slot.Resync = false;
        int range = track.FindRange(slot.Anchor, track.GapToleranceSeconds);
        AppendFrame? key = null;
        if (range >= 0)
        {
            MediaTimeRange span = track.Ranges[range];
            foreach (AppendFrame frame in track.Frames)
            {
                double time = track.Seconds(frame.RangeStart);
                if (frame.IsKey && time >= span.Start - track.GapToleranceSeconds && time <= slot.Anchor + 1e-9 &&
                    (key is null || frame.RangeStart > key.RangeStart))
                {
                    key = frame;
                }
            }
        }

        if (key is not null && key.Dts != slot.GopDts && key.Dts <= slot.Cursor)
        {
            slot.Cursor = key.Dts - 1;
            return;
        }

        if (key is null || key.Dts != slot.GopDts)
            slot.NeedKey = true;
    }

    private void Deliver(Slot slot, AppendFrame frame, MediaPacket packet)
    {
        AppendTrackBuffer track = slot.Track!;
        packet.ReferenceFrom(frame.Packet);
        packet.Retime(slot.Index, frame.Pts, frame.Dts, frame.Duration);
        if (TakesSetupOnPacket(frame.Setup))
        {
            bool switched = slot.Delivered is { } previous && !previous.SameExtradata(frame.Setup);
            bool restart = slot.Delivered is null && track.HasSeveralExtradata;
            bool keyframe = frame.IsKey && track.Kind == MediaStreamKind.Video && track.HasSeveralExtradata;
            if (switched || restart || keyframe)
                packet.SetNewExtradata(frame.Setup.Parameters.Extradata);
        }

        slot.Cursor = frame.Dts;
        slot.Anchor = track.Seconds(frame.RangeStart);
        slot.Delivering = true;
        slot.Delivered = frame.Setup;
        if (frame.IsKey)
        {
            slot.GopDts = frame.Dts;
            slot.GopStart = slot.Anchor;
        }
    }

    private bool SwitchDueLocked(double switchAt)
    {
        if (!MediaTime.IsValid(playbackPosition) || playbackPosition >= switchAt - SwitchArrivalSeconds)
            return true;
        return playbackPosition >= switchAt - SwitchStallWindowSeconds &&
            MediaTime.Now - positionChangedAt >= SwitchStallSeconds;
    }

    /// <summary>Describes the setups found at <paramref name="position"/> and restarts reading there.</summary>
    private void Reconfigure(double position)
    {
        video.Configured = SetupAt(video, position) ?? video.Configured;
        audio.Configured = SetupAt(audio, position) ?? audio.Configured;
        info = owner.DescribeLocked(
            video.Track is null ? null : video.Configured,
            video.Track,
            audio.Track is null ? null : audio.Configured);
        reconfigurationPosition = position;
        seekTarget = position;
        positioned = false;
        video.Forget();
        audio.Forget();
    }

    /// <summary>Read state of one delivered stream.</summary>
    private sealed class Slot(int index)
    {
        public int Index { get; } = index;

        public AppendTrackBuffer? Track { get; set; }

        /// <summary>Setup the stream's decoder was last built for.</summary>
        public AppendTrackSetup? Configured { get; set; }

        /// <summary>Setup of the last delivered packet; null after a seek.</summary>
        public AppendTrackSetup? Delivered { get; set; }

        /// <summary>Decode time of the last delivered frame; frames after it come next.</summary>
        public long Cursor { get; set; } = long.MinValue;

        /// <summary>Decode time of the keyframe the group being delivered started with.</summary>
        public long GopDts { get; set; } = long.MinValue;

        public double GopStart { get; set; } = double.NaN;

        /// <summary>Presentation start of the last delivered frame, which places it in a range.</summary>
        public double Anchor { get; set; } = double.NaN;

        public bool Delivering { get; set; }

        public bool NeedKey { get; set; }

        public bool Resync { get; set; }

        public bool Exhausted { get; set; }

        public void Forget()
        {
            Delivered = null;
            Cursor = long.MinValue;
            GopDts = long.MinValue;
            GopStart = double.NaN;
            Anchor = double.NaN;
            Delivering = false;
            NeedKey = false;
            Resync = false;
            Exhausted = false;
        }
    }
}
