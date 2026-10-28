using System.Runtime.Versioning;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserPhoto
{
    internal Task<int> OpenAnimationAsync() => BackgroundMediaJobs.RunOnAsync(slot, (current, _) =>
    {
        ObjectDisposedException.ThrowIf(!ReferenceEquals(current, worker) || Volatile.Read(ref disposed) != 0, this);
        return current.CallAsync("photoAnimationOpen", Description.Session,
            EngineWireContext.Default.Int32, EngineWireContext.Default.Int32);
    }, CancellationToken.None);

    internal Task<(WirePhotoAnimationResult Value, BrowserFrameInfo[] Frames)> ReadAnimationAsync(int playback,
        CancellationToken cancellationToken) => BackgroundMediaJobs.RunOnAsync(slot, (current, _) =>
    {
        ObjectDisposedException.ThrowIf(!ReferenceEquals(current, worker) || Volatile.Read(ref disposed) != 0, this);
        return current.CallWithFramesAsync("photoAnimationRead", new WirePhotoAnimation(Description.Session, playback),
            EngineWireContext.Default.WirePhotoAnimation, EngineWireContext.Default.WirePhotoAnimationResult);
    }, cancellationToken, static result =>
    {
        foreach (BrowserFrameInfo frame in result.Frames)
            MediaWorkerConnection.DropFrame(frame);
    });

    internal Task CloseAnimationAsync(int playback) => BackgroundMediaJobs.RunOnAsync(slot, (current, _) =>
        ReferenceEquals(current, worker) && worker.Failure is null
            ? current.CallAsync("photoAnimationClose", new WirePhotoAnimation(Description.Session, playback),
                EngineWireContext.Default.WirePhotoAnimation, EngineWireContext.Default.Boolean)
            : Task.FromResult(false), CancellationToken.None);
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserPhotoAnimationSource(BrowserPhotoLifetime owner, PhotoColorProfile? profile) : IPhotoAnimationSource
{
    private readonly BrowserPhotoLifetime retainedOwner = owner.AddRef();
    private int disposed;
    public long EncodedBytes => retainedOwner.Photo.Description.AnimationBytes;

    public IPhotoAnimation Open()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new BrowserPhotoAnimation(retainedOwner.AddRef(), profile);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try { await retainedOwner.DisposeAsync(); }
        catch (Exception exception) { AppLog.Error("Web.Photo", "Could not release image animation.", exception); }
    }
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserPhotoAnimation(BrowserPhotoLifetime owner, PhotoColorProfile? profile) : IPhotoAnimation
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycle = new();
    private int playback;
    private Task? closing;

    public async ValueTask<VideoFrame?> ReadNextAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(cancellation.Token);
        BrowserFrameInfo[] decoded = [];
        VideoFrame? frame = null;
        bool imported = false;
        try
        {
            if (playback == 0)
                playback = await owner.Photo.OpenAnimationAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await owner.Photo.ReadAnimationAsync(playback, cancellation.Token);
            decoded = result.Frames;
            if (result.Value.Image is not { } image)
            {
                if (decoded.Length != 0)
                    throw new InvalidDataException("The worker returned frames after the animation ended.");
                return null;
            }
            if (decoded.Length != 1 || image.Frame != 0)
                throw new InvalidDataException("The worker returned an invalid animation frame.");
            frame = BrowserMediaFrames.CreateVideo(decoded[0]);
            imported = true;
            if (profile is not null)
                WirePhotoFrames.ApplyProfile(frame, profile, linear: false);
            cancellation.Token.ThrowIfCancellationRequested();
            VideoFrame resultFrame = frame;
            frame = null;
            return resultFrame;
        }
        finally
        {
            frame?.Release();
            if (!imported)
                foreach (BrowserFrameInfo image in decoded)
                    MediaWorkerConnection.DropFrame(image);
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
            return new ValueTask(closing ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync();
        try
        {
            try
            {
                if (playback != 0)
                    await owner.Photo.CloseAnimationAsync(playback);
            }
            finally { await owner.DisposeAsync(); }
        }
        finally { gate.Release(); }
    }
}