using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Where a session gets its packets from. Separating this from <see cref="MediaPlayer"/> is what
/// lets the same pipeline run over a local container and over a server that demuxes on the client's
/// behalf, which produce packets in very different ways.
/// </summary>
public interface IMediaSourceProvider : IDisposable
{
    /// <summary>How this session is being read, for the diagnostics overlay.</summary>
    string Description { get; }

    /// <summary>Opens the media itself. Called once per session.</summary>
    IPacketSource OpenMain();

    /// <summary>
    /// Opens a file that plays alongside the media -- an external sound track, a subtitle container.
    /// Providers whose main source already carries the sidecar's streams are never asked for one.
    /// </summary>
    IPacketSource OpenSidecar(string path);

    /// <summary>
    /// Reads a text subtitle stream out as one ASS script. <paramref name="sidecarPath"/> names the
    /// file the stream lives in, or is null for one of the media's own streams.
    /// </summary>
    string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default);

    /// <summary>
    /// Reads a bitmap subtitle stream into timed indexed-color compositions. <paramref name="sidecarPath"/>
    /// names the file the stream lives in, or is null for one of the media's own streams.
    /// </summary>
    BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default);

    /// <summary>Opens a plain file, for subtitle scripts that are read rather than demuxed.</summary>
    Stream OpenFile(string path);
}

/// <summary>The currently available portion of a streaming timeline and its suggested live start.</summary>
public readonly record struct StreamingTimeline(
    bool IsLive,
    double StartSeconds,
    double EndSeconds,
    double TargetDurationSeconds,
    bool CanSeekBack,
    double SuggestedStartSeconds = 0);

public enum StreamingLoadStage
{
    Connecting,
    LoadingManifest,
    ReadingStreamInformation,
}

public interface IStreamingMediaSourceProvider
{
    StreamingTimeline Timeline { get; }

    event EventHandler? TimelineChanged;

    StreamingLoadStage LoadStage { get; }

    event EventHandler? LoadStageChanged;

    /// <summary>Interrupts transport waits so the current session can stop promptly.</summary>
    void CancelPendingOperations();
}

/// <summary>
/// Reads everything through libavformat on this device, whether the bytes come off a disk or off a
/// remote file the platform makes look like one.
/// </summary>
public sealed class LocalMediaSourceProvider : IMediaSourceProvider
{
    private readonly Func<Stream> openMain;
    private readonly Func<string, Stream> openFile;
    private readonly string? name;

    public LocalMediaSourceProvider(Func<Stream> openMain, string? name = null, Func<string, Stream>? openFile = null)
    {
        this.openMain = openMain ?? throw new ArgumentNullException(nameof(openMain));
        this.name = name;
        this.openFile = openFile ?? File.OpenRead;
    }

    /// <summary>Reads a file through the supplied opener, or the local filesystem.</summary>
    public static LocalMediaSourceProvider ForPath(string path, Func<string, Stream>? openFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Func<string, Stream> opener = openFile ?? File.OpenRead;
        return new LocalMediaSourceProvider(() => opener(path), path, opener);
    }

    public string Description => "local container";

    public IPacketSource OpenMain() => Open(openMain, name);

    public IPacketSource OpenSidecar(string path) => Open(() => openFile(path), path);

    public Stream OpenFile(string path) => openFile(path);

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        // A second demuxer over the same source: the one feeding playback cannot be narrowed to the
        // subtitle stream and walked to the end under it.
        using var reader = (FfmpegDemuxer)(sidecarPath is not null
            ? OpenSidecar(sidecarPath)
            : OpenMain());
        return FfmpegSubtitleReader.Read(reader, streamIndex, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        using var reader = (FfmpegDemuxer)(sidecarPath is not null
            ? OpenSidecar(sidecarPath)
            : OpenMain());
        return FfmpegSubtitleReader.ReadBitmaps(
            reader,
            streamIndex,
            token,
            preserveContainerTimestamps: sidecarPath is not null);
    }

    public void Dispose()
    {
    }

    private static FfmpegDemuxer Open(Func<Stream> openStream, string? sourceName)
    {
        Stream stream = openStream()
            ?? throw new InvalidOperationException("The source could not be opened.");
        try
        {
            return FfmpegDemuxer.Open(stream, sourceName, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
