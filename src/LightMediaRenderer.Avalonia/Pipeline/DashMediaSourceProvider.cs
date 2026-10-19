#nullable enable
using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Resolves a Dynamic Adaptive Streaming over HTTP session through the managed transport.</summary>
public sealed class DashMediaSourceProvider : IMediaSourceProvider, IStreamingMediaSourceProvider
{
    private readonly HttpMediaResourceResolver resolver;
    private readonly string manifestUrl;
    private StreamingLoadStage loadStage = StreamingLoadStage.Connecting;
    private bool disposed;

    public DashMediaSourceProvider(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", nameof(manifestUrl));
        }

        this.manifestUrl = uri.AbsoluteUri;
        resolver = new HttpMediaResourceResolver();
    }

    public string Description => "Dynamic Adaptive Streaming over HTTP";

    public StreamingTimeline Timeline => resolver.Timeline;

    public event EventHandler? TimelineChanged
    {
        add => resolver.TimelineChanged += value;
        remove => resolver.TimelineChanged -= value;
    }

    public StreamingLoadStage LoadStage => loadStage;

    public event EventHandler? LoadStageChanged;

    public void CancelPendingOperations() => resolver.CancelPendingOperations();

    public IPacketSource OpenMain()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SetLoadStage(StreamingLoadStage.LoadingManifest);
        MediaResource root = resolver.OpenDashManifest(manifestUrl);
        SetLoadStage(StreamingLoadStage.ReadingStreamInformation);
        return FfmpegDemuxer.OpenDash(root, resolver);
    }

    private void SetLoadStage(StreamingLoadStage value)
    {
        if (loadStage == value)
            return;

        loadStage = value;
        LoadStageChanged?.Invoke(this, EventArgs.Empty);
    }

    public IPacketSource OpenSidecar(string path) =>
        throw new NotSupportedException("External sidecars are not available for a DASH link.");

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        if (sidecarPath is not null)
            throw new NotSupportedException("External sidecars are not available for a DASH link.");

        using var reader = (FfmpegDemuxer)OpenMain();
        return FfmpegSubtitleReader.Read(reader, streamIndex, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        if (sidecarPath is not null)
            throw new NotSupportedException("External sidecars are not available for a DASH link.");

        using var reader = (FfmpegDemuxer)OpenMain();
        return FfmpegSubtitleReader.ReadBitmaps(reader, streamIndex, token);
    }

    public Stream OpenFile(string path) =>
        throw new NotSupportedException("External files are not available for a DASH link.");

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        resolver.Dispose();
    }
}