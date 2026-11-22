using System.Collections.Generic;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Why an append was refused: the <c>reason</c> of the browser link's <c>appendFailed</c>.</summary>
public enum AppendFailureReason
{
    /// <summary>The bytes are malformed or contradict what the buffer established.</summary>
    Decode,

    /// <summary>A track uses a codec or container this application does not decode.</summary>
    Unsupported,

    /// <summary>An initialization segment declares encrypted content; the session has to end.</summary>
    Encrypted,
}

/// <summary>The <c>error</c> of an <c>endOfStream</c> message.</summary>
public enum EndOfStreamError
{
    None,
    Network,
    Decode,
}

/// <summary>Outcome of declaring a buffer with <c>addSourceBuffer</c>.</summary>
public enum SourceBufferAddResult
{
    Added,

    /// <summary>The MIME type names a container or codec that is not decoded.</summary>
    Unsupported,

    /// <summary>Every declared buffer already delivered its initialization segment and playback was set up.</summary>
    TooLate,

    /// <summary>The buffer number is already in use in this session.</summary>
    DuplicateId,
}

/// <summary>A track an initialization segment declares, as <c>appended</c> lists it in <c>tracks</c>.</summary>
/// <param name="Id">The container's track number.</param>
/// <param name="Kind"><c>video</c> or <c>audio</c>.</param>
/// <param name="Codec">Codec family as <c>ready</c> names it, such as <c>avc1</c> or <c>opus</c>.</param>
/// <param name="Width">Picture width; 0 for audio.</param>
/// <param name="Height">Picture height; 0 for audio.</param>
/// <param name="SampleRate">Sample rate; 0 for video.</param>
/// <param name="Channels">Channel count; 0 for video.</param>
public sealed record AppendedTrack(int Id, string Kind, string Codec, int Width, int Height, int SampleRate, int Channels);

/// <summary>Reply to one binary append: <see cref="AppendedReply"/> or <see cref="AppendFailedReply"/>.</summary>
public abstract record AppendReply(uint Buffer, uint Sequence);

/// <summary>The <c>appended</c> message. <see cref="Tracks"/> is set after an initialization segment.</summary>
public sealed record AppendedReply(
    uint Buffer,
    uint Sequence,
    IReadOnlyList<MediaTimeRange> Buffered,
    bool Full,
    IReadOnlyList<AppendedTrack>? Tracks) : AppendReply(Buffer, Sequence);

/// <summary>The <c>appendFailed</c> message.</summary>
public sealed record AppendFailedReply(uint Buffer, uint Sequence, AppendFailureReason Reason) : AppendReply(Buffer, Sequence);

/// <summary>The <c>removed</c> message.</summary>
public sealed record RemovedReply(uint Buffer, long Request, IReadOnlyList<MediaTimeRange> Buffered, bool Full);

/// <summary>The <c>buffered</c> message, sent when eviction changes a buffer's ranges or full flag.</summary>
public sealed record BufferedReply(uint Buffer, IReadOnlyList<MediaTimeRange> Buffered, bool Full);
