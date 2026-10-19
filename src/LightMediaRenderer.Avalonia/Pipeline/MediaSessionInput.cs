using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// What a decode session reads: the media itself, files that play alongside it, and its subtitles.
/// Descriptions rather than open handles, so the engine reads the bytes in its own domain.
/// </summary>
public interface IMediaSessionInput : IAsyncDisposable
{
    /// <summary>How the media is being read, for the diagnostics overlay.</summary>
    string Description { get; }

    /// <summary>Set for a source that streams a live or growing timeline.</summary>
    IStreamingMediaSourceProvider? Streaming { get; }

    /// <summary>Prepares the input. Called once, before anything else is read.</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    MediaInput Main { get; }

    /// <summary>A file that plays alongside the media, such as an external sound track.</summary>
    MediaInput Sidecar(string path);

    /// <summary>Opens a plain file, for subtitle scripts that are read rather than demuxed.</summary>
    Task<Stream> OpenFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads a text subtitle stream of the media, or of <paramref name="sidecarPath"/>, as one ASS script.</summary>
    Task<string> ReadSubtitleScriptAsync(IMediaEngine engine, int streamIndex, string? sidecarPath, CancellationToken cancellationToken);

    /// <summary>Reads a bitmap subtitle stream of the media, or of <paramref name="sidecarPath"/>.</summary>
    Task<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        IMediaEngine engine,
        int streamIndex,
        string? sidecarPath,
        CancellationToken cancellationToken);

    /// <summary>Interrupts transport waits so the session can stop promptly.</summary>
    void CancelPendingOperations();
}

/// <summary>Reads through an <see cref="IMediaSourceProvider"/>, in the in-process engine.</summary>
public sealed class ProviderSessionInput : IMediaSessionInput
{
    private readonly Func<IMediaSourceProvider> createProvider;
    private readonly bool ownsProvider;
    private IMediaSourceProvider? provider;

    public ProviderSessionInput(Func<IMediaSourceProvider> createProvider, bool ownsProvider = true)
    {
        this.createProvider = createProvider ?? throw new ArgumentNullException(nameof(createProvider));
        this.ownsProvider = ownsProvider;
    }

    public ProviderSessionInput(IMediaSourceProvider provider, bool ownsProvider = true)
        : this(() => provider, ownsProvider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    private IMediaSourceProvider Provider => provider ?? throw new InvalidOperationException("The input is not open.");

    public string Description => provider?.Description ?? string.Empty;

    public IStreamingMediaSourceProvider? Streaming => provider as IStreamingMediaSourceProvider;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        // Providers may block while they set up their transport.
        provider ??= await Task.Run(createProvider, cancellationToken).ConfigureAwait(false);
    }

    public MediaInput Main => new PacketSourceMediaInput(Description, Provider.OpenMain);

    public MediaInput Sidecar(string path)
    {
        IMediaSourceProvider owner = Provider;
        return new PacketSourceMediaInput(path, () => owner.OpenSidecar(path));
    }

    public Task<Stream> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        IMediaSourceProvider owner = Provider;
        return Task.Run(() => owner.OpenFile(path), cancellationToken);
    }

    public Task<string> ReadSubtitleScriptAsync(
        IMediaEngine engine,
        int streamIndex,
        string? sidecarPath,
        CancellationToken cancellationToken)
    {
        IMediaSourceProvider owner = Provider;
        return Task.Run(() => owner.ReadSubtitleScript(streamIndex, sidecarPath, cancellationToken), cancellationToken);
    }

    public Task<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(
        IMediaEngine engine,
        int streamIndex,
        string? sidecarPath,
        CancellationToken cancellationToken)
    {
        IMediaSourceProvider owner = Provider;
        return Task.Run(() => owner.ReadBitmapSubtitles(streamIndex, sidecarPath, cancellationToken), cancellationToken);
    }

    public void CancelPendingOperations() => Streaming?.CancelPendingOperations();

    public ValueTask DisposeAsync()
    {
        if (ownsProvider)
            provider?.Dispose();
        return ValueTask.CompletedTask;
    }
}
