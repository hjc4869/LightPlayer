using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Video.Pipeline;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

/// <summary>
/// Reads a video through a server that demuxes it, so only the tracks being played cross the
/// network.
/// </summary>
/// <remarks>
/// The manifest carries the video's own streams and those of every sidecar the server could open.
/// Sidecar files still use the platform reader; the main video always stays on the server packet
/// stream so a transport failure cannot silently turn into a full-file download and local demux.
/// </remarks>
internal sealed class RemoteVideoSourceProvider(
    RemoteApiClient client,
    string path,
    IMultiMediaPlatformIO platform,
    RemoteTranscodeIntent? intent) : IMediaSourceProvider
{
    private readonly LocalMediaSourceProvider fallback = MediaSourceProviders.CreateLocal(path, platform);
    private readonly CancellationTokenSource lifetime = new();
    private RemoteVideoPacketSource? session;
    private string description = "server packet stream (HTTP fetch)";
    private bool disposed;

    public string Description => description;

    public IPacketSource OpenMain()
    {
        return OpenMainAsync(lifetime.Token).GetAwaiter().GetResult();
    }

    public async Task<IPacketSource> OpenMainAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        session = await OpenNegotiatedAsync(client, path, intent, linked.Token).ConfigureAwait(false);
        description = session.TransportDescription;
        return session;
    }

    /// <summary>
    /// Describes the video's current source, applies <paramref name="intent"/> to it, and opens the packet
    /// stream on those terms.
    /// </summary>
    internal static async Task<RemoteVideoPacketSource> OpenNegotiatedAsync(
        RemoteApiClient client,
        string path,
        RemoteTranscodeIntent? intent,
        CancellationToken cancellationToken)
    {
        RemoteStreamManifest video = await client
            .PostAsync<RemotePathRequest, RemoteStreamManifest>(
                RemoteStreamDefaults.DescribePath, new RemotePathRequest(path), cancellationToken)
            .ConfigureAwait(false);
        RemoteTranscodeRequest? transcode = RemoteTranscoding.BuildRequest(
            video,
            video.Transcoding,
            intent?.Settings ?? RemoteTranscodeSettings.Default,
            intent?.Override ?? RemoteTranscodeOverride.None);
        return await RemoteVideoPacketSource
            .CreateAsync(client, path, transcode, cancellationToken, sourceRevision: video.SourceRevision)
            .ConfigureAwait(false);
    }

    public IPacketSource OpenSidecar(string sidecarPath) => fallback.OpenSidecar(sidecarPath);

    public Stream OpenFile(string filePath)
    {
        return OpenFileAsync(filePath, lifetime.Token).GetAwaiter().GetResult();
    }

    public Task<Stream> OpenFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        platform.OpenMediaFileAsync(filePath, cancellationToken);

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        // The server extracts the script, so this end never has to read the file for itself.
        if (sidecarPath is null && session is not null)
        {
            return session.ReadSubtitleScript(streamIndex, token);
        }

        return fallback.ReadSubtitleScript(streamIndex, sidecarPath, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        // The server sends the track's encoded packets, which are decoded here.
        if (sidecarPath is null && session is not null)
        {
            BitmapSubtitlePackets packets = session.ReadSubtitlePacketsAsync(streamIndex, token).GetAwaiter().GetResult();
            return FfmpegSubtitleReader.ReadBitmaps(packets, token);
        }

        return fallback.ReadBitmapSubtitles(streamIndex, sidecarPath, token);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        // The packet source belongs to the player once it has been handed over, and disposing it is
        // what drops the connection the server is streaming down.
        lifetime.Cancel();
        session = null;
        fallback.Dispose();
        lifetime.Dispose();
    }
}
