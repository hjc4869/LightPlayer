using System;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>A closed-open span of the presentation timeline, in seconds.</summary>
public readonly record struct MediaTimeRange(double Start, double End);

/// <summary>One initialization segment's description of a track, in the track's own time base.</summary>
internal sealed class AppendTrackSetup
{
    private static int nextGeneration;

    public AppendTrackSetup(CodecParameters parameters, int trackId)
    {
        Parameters = parameters;
        TrackId = trackId;
        Generation = System.Threading.Interlocked.Increment(ref nextGeneration);
    }

    public CodecParameters Parameters { get; }

    public int TrackId { get; }

    public int Generation { get; }

    public string CodecName => Parameters.CodecName;

    public bool SameCodec(AppendTrackSetup other) => string.Equals(CodecName, other.CodecName, StringComparison.Ordinal);

    public bool SameExtradata(AppendTrackSetup other) =>
        ReferenceEquals(this, other) || Parameters.Extradata.AsSpan().SequenceEqual(other.Parameters.Extradata);
}

/// <summary>A coded frame held by reference, with its timing on the Media Source timeline.</summary>
internal sealed class AppendFrame
{
    public required MediaPacket Packet { get; init; }

    public required AppendTrackSetup Setup { get; init; }

    /// <summary>Presentation time of the packet as the decoder sees it, in track ticks.</summary>
    public long Pts { get; set; }

    public long Dts { get; set; }

    public long Duration { get; set; }

    /// <summary>Ticks at the start the append window trimmed; the frame's range starts this much later.</summary>
    public long Trim { get; set; }

    public bool IsKey { get; init; }

    /// <summary>The duration was estimated and is replaced once the next frame arrives.</summary>
    public bool EstimatedDuration { get; set; }

    public int Size { get; init; }

    public long RangeStart => Pts + Trim;

    public long RangeEnd => Pts + Math.Max(Duration, Trim + 1);
}

/// <summary>
/// The coded frames of one track of one buffer, ordered by decode time, with buffered ranges derived
/// from presentation times. Not thread-safe: the owning provider serializes access.
/// </summary>
internal sealed class AppendTrackBuffer
{
    private const double MaximumGapToleranceSeconds = 1.0;

    private readonly List<AppendFrame> frames = new();
    private readonly List<MediaTimeRange> ranges = new();
    private bool rangesDirty;
    private double longestFrameSeconds;

    public AppendTrackBuffer(AppendSourceBuffer owner, MediaStreamKind kind, int ordinal, AppendTrackSetup setup)
    {
        Owner = owner;
        Kind = kind;
        Ordinal = ordinal;
        TimeBaseNum = setup.Parameters.TimeBaseNum;
        TimeBaseDen = setup.Parameters.TimeBaseDen;
        LatestSetup = setup;
    }

    public AppendSourceBuffer Owner { get; }

    public MediaStreamKind Kind { get; }

    /// <summary>Position among the buffer's tracks of the same kind, which is how later initialization segments match it.</summary>
    public int Ordinal { get; }

    /// <summary>Stream index the packet source delivers this track as, or -1.</summary>
    public int StreamIndex { get; set; } = -1;

    public int TimeBaseNum { get; }

    public int TimeBaseDen { get; }

    public AppendTrackSetup LatestSetup { get; set; }

    /// <summary>Set once frames of two setups with different codec extradata have been held.</summary>
    public bool HasSeveralExtradata { get; private set; }

    public IReadOnlyList<AppendFrame> Frames => frames;

    public long Bytes { get; private set; }

    // Coded frame processing state of the Media Source algorithm.
    public long? LastDecodeTimestamp { get; set; }

    public long LastFrameDuration { get; set; }

    public bool NeedRandomAccessPoint { get; set; } = true;

    /// <summary>Largest frame duration seen, which stands in for a frame whose duration is unknown.</summary>
    public long TypicalDuration { get; private set; }

    /// <summary>
    /// Widest gap that buffered ranges bridge and the packet source plays across: two of the longest
    /// frames seen, so at least one frame.
    /// </summary>
    public double GapToleranceSeconds => Math.Min(MaximumGapToleranceSeconds, Math.Max(2 * longestFrameSeconds, 0.002));

    public double Seconds(long ticks) => ticks * (double)TimeBaseNum / TimeBaseDen;

    public long Ticks(double seconds) => (long)Math.Round(seconds * TimeBaseDen / TimeBaseNum);

    public void NoteSetup(AppendTrackSetup setup)
    {
        if (!setup.SameExtradata(LatestSetup))
            HasSeveralExtradata = true;
        LatestSetup = setup;
    }

    public void NoteDuration(long duration)
    {
        if (duration <= 0)
            return;
        TypicalDuration = Math.Max(TypicalDuration, duration);
        longestFrameSeconds = Math.Max(longestFrameSeconds, Seconds(duration));
    }

    /// <summary>Index of the first frame whose decode time is greater than <paramref name="dts"/>.</summary>
    public int FirstAfter(long dts)
    {
        int low = 0, high = frames.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (frames[middle].Dts <= dts)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>Adds frames already processed for the timeline, keeping decode order.</summary>
    public void Insert(List<AppendFrame> added)
    {
        if (added.Count == 0)
            return;

        foreach (AppendFrame frame in added)
        {
            int index = frames.Count > 0 && frames[^1].Dts <= frame.Dts ? frames.Count : FirstAfter(frame.Dts);
            frames.Insert(index, frame);
            Bytes += frame.Size;
            if (!frame.Setup.SameExtradata(LatestSetup))
                HasSeveralExtradata = true;
            RefinePreviousDuration(index);
        }

        rangesDirty = true;
    }

    /// <summary>
    /// Replaces the estimated duration of the frame presented just before <paramref name="index"/> now
    /// that the next presentation time is known.
    /// </summary>
    private void RefinePreviousDuration(int index)
    {
        if (index == 0)
            return;
        AppendFrame previous = frames[index - 1];
        AppendFrame current = frames[index];
        if (!previous.EstimatedDuration || current.Pts <= previous.Pts)
            return;

        long derived = current.Pts - previous.Pts;
        if (TypicalDuration > 0 && derived > 4 * TypicalDuration)
            return;
        previous.Duration = derived;
        previous.EstimatedDuration = false;
        previous.Packet.Retime(previous.Packet.StreamIndex, previous.Pts, previous.Dts, derived);
    }

    /// <summary>
    /// Removes frames presented in [<paramref name="start"/>, <paramref name="end"/>) seconds and,
    /// for video, the frames after them in decode order up to the next keyframe, which depend on them.
    /// </summary>
    public void RemovePresented(double start, double end, List<AppendFrame> removed)
    {
        if (end <= start)
            return;
        RemovePresented(
            double.IsNegativeInfinity(start) ? long.MinValue : Ticks(start),
            double.IsPositiveInfinity(end) ? long.MaxValue : Ticks(end),
            removed);
    }

    /// <summary>Like <see cref="RemovePresented(double, double, List{AppendFrame})"/>, in track ticks.</summary>
    public void RemovePresented(long first, long last, List<AppendFrame> removed)
    {
        if (frames.Count == 0 || last <= first)
            return;

        bool dependent = false;
        int write = 0;
        for (int read = 0; read < frames.Count; read++)
        {
            AppendFrame frame = frames[read];
            long presented = frame.RangeStart;
            bool inside = presented >= first && presented < last;
            if (inside || (dependent && !frame.IsKey))
            {
                dependent = Kind == MediaStreamKind.Video;
                Bytes -= frame.Size;
                removed.Add(frame);
                continue;
            }

            if (frame.IsKey)
                dependent = false;
            frames[write++] = frame;
        }

        if (write != frames.Count)
        {
            frames.RemoveRange(write, frames.Count - write);
            rangesDirty = true;
        }
    }

    /// <summary>Removes every frame whose decode time is before <paramref name="dts"/>.</summary>
    public void RemoveBefore(long dts, List<AppendFrame> removed)
    {
        int count = FirstAfter(dts - 1);
        if (count == 0)
            return;
        for (int index = 0; index < count; index++)
        {
            Bytes -= frames[index].Size;
            removed.Add(frames[index]);
        }

        frames.RemoveRange(0, count);
        rangesDirty = true;
    }

    public void Clear(List<AppendFrame> removed)
    {
        removed.AddRange(frames);
        frames.Clear();
        Bytes = 0;
        rangesDirty = true;
    }

    /// <summary>Buffered ranges, bridging gaps up to <see cref="GapToleranceSeconds"/>.</summary>
    public IReadOnlyList<MediaTimeRange> Ranges
    {
        get
        {
            if (rangesDirty)
                RebuildRanges();
            return ranges;
        }
    }

    /// <summary>Index into <see cref="Ranges"/> of the range containing <paramref name="seconds"/>, or -1.</summary>
    public int FindRange(double seconds, double slack = 0)
    {
        IReadOnlyList<MediaTimeRange> current = Ranges;
        for (int index = 0; index < current.Count; index++)
        {
            if (seconds >= current[index].Start - slack && seconds < current[index].End)
                return index;
        }

        return -1;
    }

    private void RebuildRanges()
    {
        rangesDirty = false;
        ranges.Clear();
        if (frames.Count == 0)
            return;

        // Decode order is presentation order up to reordering, so one pass gathers the spans and a
        // sort of the few that result merges any that reordering left apart.
        double tolerance = GapToleranceSeconds;
        var spans = new List<MediaTimeRange>();
        double spanStart = double.NaN, spanEnd = double.NaN;
        foreach (AppendFrame frame in frames)
        {
            double start = Seconds(frame.RangeStart);
            double end = Seconds(frame.RangeEnd);
            if (!double.IsNaN(spanStart) && start <= spanEnd + tolerance && end >= spanStart - tolerance)
            {
                spanStart = Math.Min(spanStart, start);
                spanEnd = Math.Max(spanEnd, end);
                continue;
            }

            if (!double.IsNaN(spanStart))
                spans.Add(new MediaTimeRange(spanStart, spanEnd));
            spanStart = start;
            spanEnd = end;
        }

        spans.Add(new MediaTimeRange(spanStart, spanEnd));
        spans.Sort((left, right) => left.Start.CompareTo(right.Start));
        foreach (MediaTimeRange span in spans)
        {
            if (ranges.Count > 0 && span.Start <= ranges[^1].End + tolerance)
                ranges[^1] = new MediaTimeRange(ranges[^1].Start, Math.Max(ranges[^1].End, span.End));
            else
                ranges.Add(span);
        }
    }
}
