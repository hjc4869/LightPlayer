using System.Runtime.Versioning;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserImageDecoder
{
    internal static async Task<VideoFrame?> DecodePhotoAsync(string path, Func<CancellationToken, Task<Stream>>? openSource,
        IProgress<Size>? displaySize, CancellationToken cancellationToken, PhotoPreviewRequest? preview = null, bool? renderRaw = null)
    {
        BrowserPhoto? opened = await BrowserMediaOperations.OpenPhotoAsync(
            BrowserSessionInput.Describe(path, openSource), cancellationToken, preview, renderRaw, displaySize);
        if (opened is null)
            return null;
        var owner = new BrowserPhotoLifetime(opened);
        VideoFrame? frame = null;
        try
        {
            WirePhoto photo = owner.Photo.Description;
            WirePhotoImage image = photo.Base;
            if (preview is null)
                displaySize?.Report(image.Rotation % 180 == 0 ? new Size(image.Width * image.PixelAspect, image.Height) :
                    new Size(image.Height, image.Width * image.PixelAspect));
            var gain = WirePhotoFrames.HasGainMap(photo);
            var profile = WirePhotoFrames.BaseProfile(photo);
            frame = CreatePicture(owner, image, profile, gain, false);
            if (photo.AnimationBytes > 0)
                frame.PhotoAnimation = new BrowserPhotoAnimationSource(owner, profile);
            frame.IsEmbeddedRawPreview = photo.IsEmbeddedRawPreview;
            frame.PhotoPeakLuminance = photo.PeakLuminance;
            if (gain)
            {
                VideoFrame samples = CreatePicture(owner, photo.Gain!, WirePhotoFrames.GainSampleProfile, true, true);
                try { frame.GainMap = WirePhotoFrames.CreateGainMap(photo, profile, samples); }
                finally { samples.Release(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = frame;
            frame = null;
            return result;
        }
        finally { frame?.Release(); await owner.DisposeAsync(); }
    }

    // A picture the worker decoded while opening the photo, or one it keeps as tiles decoded on request.
    private static VideoFrame CreatePicture(BrowserPhotoLifetime owner, WirePhotoImage image, PhotoColorProfile? profile, bool linear, bool gain)
    {
        VideoFrame frame;
        if (image.Tiles is { } tiles)
        {
            if (tiles.Count is < 1 or > 65536)
                throw new InvalidDataException("Invalid photo tile grid.");
            frame = VideoFrame.CreateHosted(image.Width, image.Height, VideoPixelFormat.Bgra32);
            frame.Rotation = image.Rotation;
            frame.MirrorHorizontally = image.Mirror;
            frame.PixelAspectRatio = image.PixelAspect;
            frame.PhotoTiles = new BrowserPhotoTileSource(owner, image, profile, linear, gain);
        }
        else
            frame = BrowserMediaFrames.CreateVideo(owner.Photo.TakeFrame(image.Frame));
        frame.HasAlpha = image.HasAlpha;
        if (profile is not null) WirePhotoFrames.ApplyProfile(frame, profile, linear);
        return frame;
    }
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserPhotoLifetime(BrowserPhoto photo) : IAsyncDisposable
{
    private int references = 1;
    internal BrowserPhoto Photo => photo;
    internal BrowserPhotoLifetime AddRef() { Interlocked.Increment(ref references); return this; }
    public ValueTask DisposeAsync() => Interlocked.Decrement(ref references) == 0 ? photo.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// A photo the shared media worker holds open: the pictures it decoded at once, and the encoded tiles
/// of larger pictures, which only that worker can decode.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserPhoto(MediaWorkerConnection worker, int slot, WirePhoto description, BrowserFrameInfo[] frames) : IAsyncDisposable
{
    private readonly BrowserFrameInfo?[] frames = frames;
    private int disposed;
    private int nextTileOperation;

    public WirePhoto Description { get; } = description;

    /// <summary>Hands over a picture decoded while opening; the caller releases it from then on.</summary>
    public BrowserFrameInfo TakeFrame(int index)
    {
        BrowserFrameInfo frame = (uint)index < (uint)frames.Length ? frames[index] ?? throw new InvalidOperationException(
            "The photo picture was already taken.") : throw new InvalidDataException("The media worker sent no such photo picture.");
        frames[index] = null;
        return frame;
    }

    public Task<BrowserFrameInfo[]> DecodeTilesAsync(bool gain, ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken) =>
        BackgroundMediaJobs.RunOnAsync(slot, async (current, _) =>
        {
            if (!ReferenceEquals(current, worker) || Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(BrowserPhoto), "The photo was closed.");
            cancellationToken.ThrowIfCancellationRequested();
            int operation = nextTileOperation = nextTileOperation == int.MaxValue ? 1 : nextTileOperation + 1;
            var pending = current.CallWithFramesAsync("photoTiles", new WirePhotoTiles(Description.Session, gain, requests.ToArray())
                { Operation = operation }, EngineWireContext.Default.WirePhotoTiles, EngineWireContext.Default.Boolean);
            using var registration = cancellationToken.Register(() => current.CancelPhotoTiles(Description.Session, gain, operation));
            try { return (await pending).Frames; }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Photo decoding was canceled.", exception, cancellationToken);
            }
        }, cancellationToken, static decoded =>
        {
            foreach (BrowserFrameInfo frame in decoded)
                MediaWorkerConnection.DropFrame(frame);
        });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (BrowserFrameInfo? frame in frames)
        {
            if (frame is not null)
                MediaWorkerConnection.DropFrame(frame);
        }

        if (Description.Session == 0 || worker.Failure is not null)
            return;
        await BackgroundMediaJobs.RunOnAsync(slot, (current, _) => ReferenceEquals(current, worker)
            ? current.CallAsync("photoClose", Description.Session, EngineWireContext.Default.Int32, EngineWireContext.Default.Boolean)
            : Task.FromResult(false), CancellationToken.None);
    }
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserPhotoTileSource(BrowserPhotoLifetime owner, WirePhotoImage image, PhotoColorProfile? profile, bool linear, bool gain) : IPhotoTileSource, IAsyncDisposable
{
    private readonly BrowserPhotoLifetime retainedOwner = owner.AddRef();
    private readonly IReadOnlyList<PhotoTileBounds> tiles = image.Tiles ?? [];
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycle = new();
    private Task? disposal;
    public int Count => tiles.Count;
    public long EncodedBytes => image.EncodedBytes;
    public bool HasScaledOverview => image.HasScaledOverview;
    public int PreferredBatchSize => image.CanUseWebCodecs ? 32 : 4;
    internal Task WhenReleased => closed.Task;
    public PhotoTileBounds GetBounds(int index) => index == -1 && HasScaledOverview
        ? new PhotoTileBounds(0, 0, image.Width, image.Height) : tiles[index];

    public async ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Length > 32) throw new ArgumentOutOfRangeException(nameof(requests));
        foreach (var request in requests.Span)
        {
            if ((uint)request.Index >= (uint)Count && !(request.Index == -1 && HasScaledOverview))
                throw new ArgumentOutOfRangeException(nameof(requests));
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(cancellation.Token);
        var frames = new VideoFrame[requests.Length];
        BrowserFrameInfo[] decoded = [];
        int imported = 0;
        try
        {
            decoded = await retainedOwner.Photo.DecodeTilesAsync(gain, requests, cancellation.Token);
            if (decoded.Length != frames.Length) throw new InvalidDataException("The media worker sent the wrong number of photo tiles.");
            for (; imported < frames.Length; imported++)
            {
                frames[imported] = BrowserMediaFrames.CreateVideo(decoded[imported]);
                if (profile is not null) WirePhotoFrames.ApplyProfile(frames[imported], profile, linear);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            return frames;
        }
        catch
        {
            foreach (var frame in frames) frame?.Release();
            for (int index = imported; index < decoded.Length; index++) MediaWorkerConnection.DropFrame(decoded[index]);
            throw;
        }
        finally { gate.Release(); }
    }

    public void Dispose() => _ = ObserveDisposalAsync();
    private async Task ObserveDisposalAsync()
    {
        try { await DisposeAsync(); }
        catch (Exception exception) { AppLog.Error("Web.Photo", "Could not close the photo tile source.", exception); }
    }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new ValueTask(disposal ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync();
        try { await retainedOwner.DisposeAsync(); closed.TrySetResult(); }
        catch (Exception exception) { closed.TrySetException(exception); throw; }
        finally { gate.Release(); }
    }
}
