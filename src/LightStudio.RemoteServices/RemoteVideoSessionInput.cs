using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

/// <summary>
/// Reads a video through the server's packet stream for an engine that opens sources without blocking, such
/// as the browser's media worker. The server extracts text subtitles of the streams it delivers and sends bitmap
/// subtitles as packets for the engine to decode; sidecar files
/// are read through the platform's files.
/// </summary>
internal sealed class RemoteVideoSessionInput(
    RemoteApiClient client,
    string path,
    IMultiMediaPlatformIO platform,
    RemoteTranscodeIntent? intent) : IMediaSessionInput
{
    private RemoteVideoPacketSource? session;
    private string description = "server packet stream";

    public string Description => description;

    public IStreamingMediaSourceProvider? Streaming => null;

    public MediaInput Main => new AsyncPacketSourceMediaInput(path, OpenMainAsync);

    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public MediaInput Sidecar(string sidecarPath) =>
        new StreamMediaInput(sidecarPath, cancellationToken => platform.OpenMediaFileAsync(sidecarPath, cancellationToken));

    public Task<Stream> OpenFileAsync(string filePath, CancellationToken cancellationToken) =>
        platform.OpenMediaFileAsync(filePath, cancellationToken);

    public Task<string> ReadSubtitleScriptAsync(
        IMediaEngine engine,
        int streamIndex,
        string? sidecarPath,
        CancellationToken cancellationToken)
    {
        if (sidecarPath is null && session is not null)
            return session.ReadSubtitleScriptAsync(streamIndex, cancellationToken);

        return engine.ReadSubtitleScriptAsync(Sidecar(sidecarPath ?? path), streamIndex, cancellationToken).AsTask();
    }

    public async Task<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        IMediaEngine engine,
        int streamIndex,
        string? sidecarPath,
        CancellationToken cancellationToken)
    {
        // The server sends the track's encoded packets, which the engine decodes.
        if (sidecarPath is null && session is not null)
        {
            BitmapSubtitlePackets packets = await session.ReadSubtitlePacketsAsync(streamIndex, cancellationToken).ConfigureAwait(false);
            return await engine.DecodeBitmapSubtitlesAsync(packets, cancellationToken).ConfigureAwait(false);
        }

        return await engine.ReadBitmapSubtitlesAsync(Sidecar(sidecarPath ?? path), streamIndex, sidecarPath is not null, cancellationToken)
            .ConfigureAwait(false);
    }

    public void CancelPendingOperations()
    {
    }

    public ValueTask DisposeAsync()
    {
        session = null;
        return ValueTask.CompletedTask;
    }

    private async Task<IPacketSource> OpenMainAsync(CancellationToken cancellationToken)
    {
        RemoteVideoPacketSource source = await RemoteVideoSourceProvider
            .OpenNegotiatedAsync(client, path, intent, cancellationToken)
            .ConfigureAwait(false);
        session = source;
        description = source.TransportDescription;
        return source;
    }
}
