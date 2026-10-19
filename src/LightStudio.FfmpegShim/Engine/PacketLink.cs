#nullable enable
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>How a pump hands items to its neighbors.</summary>
public enum PumpMode
{
    /// <summary>Waits on full or empty queues; each pump owns a thread.</summary>
    Blocking,

    /// <summary>Never waits on a queue; one scheduler steps every pump in turn.</summary>
    Cooperative,
}

public enum PumpResult
{
    Progressed,

    /// <summary>Nothing to do until input, output space or a command arrives.</summary>
    Idle,

    /// <summary>Stopped until a command revives it, after end of file or a fault.</summary>
    Finished,
}

/// <summary>
/// The packet queue between a demuxer and one decoder, plus what the decoder needs to know about each
/// run: where a seek asked it to start, and whether a run continues the previous one.
/// </summary>
public sealed class PacketLink
{
    private SeekTarget? target;
    private Continuation? continuation;
    private bool closed;

    public PacketLink(string name, int capacity, long byteCapacity)
    {
        Queue = new MediaQueue<MediaPacket>(name, capacity, byteCapacity, packet => packet.Recycle());
    }

    public MediaQueue<MediaPacket> Queue { get; }

    /// <summary>Set once the decoder is gone, so the demuxer drops what it would have routed here.</summary>
    public bool IsClosed => Volatile.Read(ref closed);

    /// <summary>Frames of <paramref name="serial"/> ending at or before <paramref name="position"/> are dropped.</summary>
    public void SetTarget(int serial, double position) =>
        Volatile.Write(ref target, double.IsNaN(position) ? null : new SeekTarget(serial, position));

    public double GetTarget(int serial) =>
        Volatile.Read(ref target) is { } current && current.Serial == serial ? current.Position : double.NaN;

    /// <summary>
    /// Marks run <paramref name="to"/> as carrying on from <paramref name="from"/>, for a seek served out
    /// of queued packets with no keyframe to restart from: the decoder keeps its state.
    /// </summary>
    public void MarkContinuation(int from, int to) => Volatile.Write(ref continuation, new Continuation(from, to));

    public bool Continues(int from, int to) =>
        Volatile.Read(ref continuation) is { } current && current.From == from && current.To == to;

    public void Close()
    {
        Volatile.Write(ref closed, true);
        Queue.Close();
    }

    private sealed record SeekTarget(int Serial, double Position);

    private sealed record Continuation(int From, int To);
}
