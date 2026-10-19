using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Opens one or more ordinary HTTP media files through managed range requests.</summary>
public sealed class HttpMediaSourceProvider : IMediaSourceProvider, IStreamingMediaSourceProvider
{
    private readonly Uri[] sources;
    private readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private Uri[] additionalSources = [];
    private Uri? mainSource;
    private StreamingTimeline timeline;
    private StreamingLoadStage loadStage = StreamingLoadStage.Connecting;
    private bool disposed;

    public HttpMediaSourceProvider(IReadOnlyList<Uri> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("At least one HTTP media URL is required.", nameof(sources));

        this.sources = new Uri[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            Uri source = sources[index] ?? throw new ArgumentException("A media URL cannot be null.", nameof(sources));
            if (!IsHttp(source))
                throw new ArgumentException("Every media URL must use HTTP or HTTPS.", nameof(sources));
            this.sources[index] = source;
        }
    }

    public string Description => "HTTP media stream";

    public IReadOnlyList<Uri> AdditionalSources => additionalSources;

    public StreamingTimeline Timeline => timeline;

    public event EventHandler? TimelineChanged;

    public StreamingLoadStage LoadStage => loadStage;

    public event EventHandler? LoadStageChanged;

    public IPacketSource OpenMain()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lifetime.Token.ThrowIfCancellationRequested();

        IPacketSource? fallback = null;
        var fallbackIndex = -1;
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                IPacketSource candidate = OpenSource(sources[index]);
                if (candidate.VideoStreamIndex >= 0)
                {
                    fallback?.Dispose();
                    SetMainSource(index, candidate);
                    return candidate;
                }

                if (fallback is null)
                {
                    fallback = candidate;
                    fallbackIndex = index;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            if (fallback is null)
                throw new InvalidDataException("The HTTP sources contain no playable media.");

            SetMainSource(fallbackIndex, fallback);
            IPacketSource result = fallback;
            fallback = null;
            return result;
        }
        finally
        {
            fallback?.Dispose();
        }
    }

    public IPacketSource OpenSidecar(string path)
    {
        Uri source = FindSource(path);
        return OpenSource(source);
    }

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        using FfmpegDemuxer reader = OpenDemuxer(sidecarPath is not null
            ? FindSource(sidecarPath)
            : mainSource ?? throw new InvalidOperationException("The main HTTP source is not open."));
        return FfmpegSubtitleReader.Read(reader, streamIndex, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        using FfmpegDemuxer reader = OpenDemuxer(sidecarPath is not null
            ? FindSource(sidecarPath)
            : mainSource ?? throw new InvalidOperationException("The main HTTP source is not open."));
        return FfmpegSubtitleReader.ReadBitmaps(
            reader,
            streamIndex,
            token,
            preserveContainerTimestamps: sidecarPath is not null);
    }

    public Stream OpenFile(string path) => HttpMediaStream.Open(client, FindSource(path), lifetime.Token);

    public void CancelPendingOperations()
    {
        if (!disposed)
            lifetime.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifetime.Cancel();
        client.Dispose();
        lifetime.Dispose();
    }

    private IPacketSource OpenSource(Uri source)
        => new PacketHistorySource(OpenDemuxer(source));

    private FfmpegDemuxer OpenDemuxer(Uri source)
    {
        SetLoadStage(StreamingLoadStage.Connecting);
        Stream stream = HttpMediaStream.Open(client, source, lifetime.Token);
        try
        {
            SetLoadStage(StreamingLoadStage.ReadingStreamInformation);
            return FfmpegDemuxer.Open(stream, source.AbsoluteUri, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private Uri FindSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Uri.TryCreate(path, UriKind.Absolute, out Uri? requested) || !IsHttp(requested))
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", nameof(path));

        return sources.FirstOrDefault(source => source.Equals(requested))
            ?? throw new ArgumentException("The URL does not belong to this media session.", nameof(path));
    }

    private void SetMainSource(int mainIndex, IPacketSource source)
    {
        mainSource = sources[mainIndex];
        additionalSources = sources.Where((_, index) => index != mainIndex).ToArray();
        timeline = new StreamingTimeline(
            IsLive: false,
            StartSeconds: 0,
            EndSeconds: source.Info.Duration.TotalSeconds,
            TargetDurationSeconds: 0,
            CanSeekBack: source.CanSeek);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLoadStage(StreamingLoadStage value)
    {
        if (loadStage == value)
            return;
        loadStage = value;
        LoadStageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}