using System.Runtime.Versioning;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// What a browser decode session reads. Granted files and HTTP resources are read by the worker
/// itself; anything else, such as a remote library stream, is served from the page.
/// </summary>
/// <param name="openFile">Opens other files of the media's source, such as its sidecars, from the page.</param>
[SupportedOSPlatform("browser")]
internal sealed class BrowserSessionInput(
    string path,
    Func<CancellationToken, Task<Stream>>? openSource,
    Func<string, CancellationToken, Task<Stream>>? openFile = null) : IMediaSessionInput
{
    public string Description => "media worker";

    public IStreamingMediaSourceProvider? Streaming => null;

    public MediaInput Main => Describe(path, openSource);

    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public MediaInput Sidecar(string sidecarPath) =>
        Describe(sidecarPath, openFile is null ? null : cancellationToken => openFile(sidecarPath, cancellationToken));

    public async Task<Stream> OpenFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (BrowserMediaPath.TryParse(filePath, out BrowserMediaPath parsed))
            return await BrowserFileStream.OpenAsync(parsed, cancellationToken);
        if (openFile is not null)
            return await openFile(filePath, cancellationToken);
        throw new NotSupportedException($"'{filePath}' cannot be opened in the browser.");
    }

    public Task<string> ReadSubtitleScriptAsync(IMediaEngine engine, int streamIndex, string? sidecarPath,
        CancellationToken cancellationToken) =>
        engine.ReadSubtitleScriptAsync(sidecarPath is null ? Main : Sidecar(sidecarPath), streamIndex, cancellationToken).AsTask();

    public Task<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(IMediaEngine engine, int streamIndex, string? sidecarPath,
        CancellationToken cancellationToken) =>
        engine.ReadBitmapSubtitlesAsync(sidecarPath is null ? Main : Sidecar(sidecarPath), streamIndex, sidecarPath is not null,
            cancellationToken).AsTask();

    public void CancelPendingOperations()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Granted files and HTTP resources are read by the worker directly; anything else through the page.</summary>
    internal static MediaInput Describe(string mediaPath, Func<CancellationToken, Task<Stream>>? open)
    {
        if (mediaPath.StartsWith("fsa://", StringComparison.Ordinal))
            return new BrowserFileMediaInput(mediaPath);
        if (open is not null)
            return new StreamMediaInput(mediaPath, open);
        return new UrlMediaInput(mediaPath);
    }
}
