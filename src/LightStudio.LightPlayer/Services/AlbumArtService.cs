using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Loads album/track artwork through the active media platform, which can extract an embedded
/// cover or fall back to a folder/cover image next to the file. Extractions
/// are throttled so a large library does not saturate the thread pool.
/// </summary>
public sealed class AlbumArtService
{
    /// <summary>Shorter-side pixel size of thumbnails requested without a display measurement.</summary>
    internal const int DefaultThumbnailPixelSize = 300;

    public static FastImageCache ThumbnailCache { get; } = new();

    /// <summary>
    /// Extraction is file I/O plus a single decode, so extra parallelism buys little while each
    /// load in flight holds the whole encoded cover -- frequently several megabytes, and large
    /// enough to land on the large object heap, which is never compacted.
    /// </summary>
    private static readonly int MaxConcurrentLoads = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);

    private readonly LifoThrottle throttle;
    private readonly IClientMediaResolver mediaResolver;

    public AlbumArtService(IClientMediaResolver? mediaResolver = null, int? maxConcurrentLoads = null)
    {
        int concurrency = maxConcurrentLoads ?? MaxConcurrentLoads;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        throttle = new(concurrency);
        this.mediaResolver = mediaResolver
            ?? new CapturedPlatformMediaResolver(LightStudio.MediaLibraryCore.IO.IMultiMediaPlatformIO.Instance);
    }

    public Task<Bitmap?> GetHighQualityTrackArtAsync(string? filePath, CancellationToken cancellationToken = default)
        => LoadAlbumArtAsync(LocalMedia(filePath), cancellationToken);

    public Task<Bitmap?> GetHighQualityTrackArtAsync(
        MediaReference media,
        CancellationToken cancellationToken = default) =>
        LoadAlbumArtAsync(media, cancellationToken);

    /// <summary>
    /// Loads a track's full-size artwork together with a blurred backdrop of it, both decoded from one read of
    /// the encoded picture.
    /// </summary>
    internal Task<ArtworkWithBackdrop?> GetHighQualityTrackArtWithBackdropAsync(
        MediaReference media,
        int backdropWidth,
        float backdropBlurSigma,
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            media,
            async (reference, token) =>
            {
                var content = await LoadArtworkContentAsync(reference, token).ConfigureAwait(false);
                if (content is null || token.IsCancellationRequested)
                {
                    return null;
                }

                var cover = await ImageDecoding.DecodeBitmapAsync(content, cancellationToken: token).ConfigureAwait(false);
                Bitmap? backdrop = null;
                try
                {
                    backdrop = await ImageDecoding.DecodeBackdropAsync(content, backdropWidth, backdropBlurSigma, token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    AppLog.Warning("Artwork.Load", "The artwork backdrop could not be decoded.", exception);
                }
                catch
                {
                    cover?.Dispose();
                    throw;
                }

                return cover is null && backdrop is null ? null : new ArtworkWithBackdrop(cover, backdrop);
            },
            1,
            cancellationToken);

    public Task<FastImageSource?> GetLowQualityTrackArtAsync(string? filePath, CancellationToken cancellationToken = default)
        => LoadThumbnailAsync(LocalMedia(filePath), DefaultThumbnailPixelSize, 1, cancellationToken);

    public Task<FastImageSource?> GetLowQualityTrackArtAsync(
        MediaReference media,
        CancellationToken cancellationToken = default) =>
        LoadThumbnailAsync(media, DefaultThumbnailPixelSize, 1, cancellationToken);

    /// <summary>Loads a thumbnail whose shorter side covers the given number of device pixels.</summary>
    internal Task<FastImageSource?> GetLowQualityTrackArtAsync(
        MediaReference media,
        int fifoWindowSize,
        int pixelSize,
        CancellationToken cancellationToken,
        FastImageCache? cachePool = null) =>
        LoadThumbnailAsync(media, pixelSize, fifoWindowSize, cancellationToken, cachePool);

    /// <summary>
    /// Extracts a track's encoded artwork for OS media controls, falling back to a
    /// conventional cover image beside the media file.
    /// </summary>
    public Task<byte[]?> GetTrackArtContentAsync(string? filePath)
    {
        return string.IsNullOrEmpty(filePath)
            ? Task.FromResult<byte[]?>(null)
            : LoadArtworkContentAsync(MediaReference.Local(filePath));
    }

    public Task<byte[]?> GetTrackArtContentAsync(MediaReference media) => LoadArtworkContentAsync(media);

    /// <summary>
    /// Applies a loaded image on the UI thread below render and input priority, so a burst of
    /// completing thumbnails cannot starve scrolling and pointer handling. An image whose request
    /// was canceled while it loaded is released instead of applied.
    /// </summary>
    public static void ApplyOnUiThread<T>(T image, Action<T> apply, CancellationToken cancellationToken)
        where T : class, IImage
        => PostOrRelease(image, apply, static discarded => (discarded as IDisposable)?.Dispose(), cancellationToken);

    /// <inheritdoc cref="ApplyOnUiThread{T}(T, Action{T}, CancellationToken)"/>
    public static void ApplyOnUiThread(IReadOnlyList<IImage> images, Action<IReadOnlyList<IImage>> apply, CancellationToken cancellationToken)
        => PostOrRelease(images, apply, ReleaseImages, cancellationToken);

    /// <summary>
    /// Releases artwork the app no longer references. Frames already in flight are unaffected: the
    /// renderer holds its own reference to anything it has drawn.
    /// </summary>
    public static void ReleaseImages(IEnumerable<IImage> images)
    {
        foreach (var image in images)
        {
            (image as IDisposable)?.Dispose();
        }
    }

    private static void PostOrRelease<T>(T loaded, Action<T> apply, Action<T> release, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            release(loaded);
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    release(loaded);
                    return;
                }

                apply(loaded);
            },
            DispatcherPriority.Background);
    }

    private Task<Bitmap?> LoadAlbumArtAsync(MediaReference? media, CancellationToken cancellationToken) =>
        LoadAsync(
            media,
            async (reference, token) =>
            {
                var content = await LoadArtworkContentAsync(reference, token).ConfigureAwait(false);
                if (content is null || token.IsCancellationRequested)
                {
                    return null;
                }

                return await ImageDecoding.DecodeBitmapAsync(content, cancellationToken: token).ConfigureAwait(false);
            },
            1,
            cancellationToken);

    private Task<FastImageSource?> LoadThumbnailAsync(
        MediaReference? media,
        int pixelSize,
        int fifoWindowSize,
        CancellationToken cancellationToken,
        FastImageCache? cachePool = null)
    {
        if (!cancellationToken.IsCancellationRequested && media is { Path.Length: > 0 } cachedMedia &&
            cachePool?.TryGet(cachedMedia, pixelSize, out var cached) == true)
        {
            return Task.FromResult(cached);
        }

        return LoadAsync(
            media,
            async (reference, token) =>
            {
                if (cachePool?.TryGet(reference, pixelSize, out var pooled) == true)
                    return pooled;

                var platform = await mediaResolver.ResolvePlatformAsync(reference, token).ConfigureAwait(false);
                var image = await ImageDecoding.LoadArtworkAsync(new PhotoDecodeRequest(platform, reference.Path), pixelSize, token)
                    .ConfigureAwait(false);
                if (image is not null && !token.IsCancellationRequested)
                    cachePool?.Set(reference, image, pixelSize);
                return image;
            },
            fifoWindowSize,
            cancellationToken);
    }

    private async Task<T?> LoadAsync<T>(
        MediaReference? media,
        Func<MediaReference, CancellationToken, Task<T?>> load,
        int fifoWindowSize,
        CancellationToken cancellationToken)
        where T : class, IDisposable
    {
        if (media is not { Path.Length: > 0 } reference)
        {
            return null;
        }

        // Acquired outside the try so a canceled wait never releases a slot it does not hold.
        if (!await throttle.WaitAsync(cancellationToken, fifoWindowSize).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var image = await load(reference, cancellationToken).ConfigureAwait(false);
            if (image is not null)
            {
                // Loaded images are handed over at background priority, which a scrolling UI thread
                // only reaches between frames. Holding the slot until it does keeps the decoders from
                // running ahead and leaving hundreds of finished, undisplayed images queued up.
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background)
                    .GetTask().ConfigureAwait(false);
            }

            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Artwork.Load", "Artwork could not be decoded.", exception);
            return null;
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<byte[]?> LoadArtworkContentAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(media.Path))
        {
            return null;
        }

        // The platform owns both embedded extraction and conventional adjacent images. Media
        // identifiers may belong to a remote server or a content provider, so this layer must not
        // derive a directory or enumerate files using the client's System.IO implementation.
        try
        {
            var platform = await mediaResolver.ResolvePlatformAsync(media, cancellationToken).ConfigureAwait(false);
            var bytes = await platform
                .GetAlbumCoverAsync(media.Path, cancellationToken)
                .ConfigureAwait(false);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static MediaReference? LocalMedia(string? path) =>
        string.IsNullOrEmpty(path) ? null : MediaReference.Local(path);

    /// <summary>
    /// Concurrency gate that prioritizes the newest window of queued requests, then serves that
    /// window in arrival order. Newer tiles stay ahead of stale scroll backlog without making each
    /// visible window load backward.
    /// </summary>
    private sealed class LifoThrottle(int maxConcurrency)
    {
        /// <summary>Canceled waiters are cleared out in bulk once the queue grows past this many entries.</summary>
        private const int PruneThreshold = 64;

        private readonly object gate = new();
        private readonly List<Waiter> waiters = [];
        private readonly Queue<Waiter> priorityWindow = new();
        private int available = maxConcurrency;

        /// <summary>Returns false when the request was canceled, in which case no slot is held.</summary>
        public async ValueTask<bool> WaitAsync(CancellationToken cancellationToken, int fifoWindowSize)
        {
            Waiter waiter;
            lock (gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (available > 0)
                {
                    available--;
                    return true;
                }

                waiter = new Waiter(Math.Max(1, fifoWindowSize));

                if (waiters.Count >= PruneThreshold)
                {
                    waiters.RemoveAll(static queued => queued.IsCompleted);
                }

                waiters.Add(waiter);
            }

            // Registered outside the gate, and never touching the queue, because the callback runs
            // inline on an already-canceled token and on the canceling thread otherwise.
            using var registration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);

            return await waiter.Task.ConfigureAwait(false);
        }

        public void Release()
        {
            lock (gate)
            {
                while (priorityWindow.Count > 0 || waiters.Count > 0)
                {
                    if (priorityWindow.Count == 0)
                    {
                        var windowSize = Math.Min(waiters[^1].FifoWindowSize, waiters.Count);
                        var first = waiters.Count - windowSize;

                        for (var index = first; index < waiters.Count; ++index)
                        {
                            priorityWindow.Enqueue(waiters[index]);
                        }

                        waiters.RemoveRange(first, windowSize);
                    }

                    var waiter = priorityWindow.Dequeue();

                    // Waiters canceled while queued cannot take the slot; pass it to the next request.
                    if (waiter.TryHandOverSlot())
                    {
                        return;
                    }
                }

                available++;
            }
        }

        /// <summary>
        /// A queued request. Cancellation completes it where it stands instead of unlinking it, so
        /// the queue is only ever touched by a thread that entered through <see cref="gate"/>.
        /// </summary>
        private sealed class Waiter(int fifoWindowSize)
        {
            private readonly TaskCompletionSource<bool> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int FifoWindowSize { get; } = fifoWindowSize;

            public Task<bool> Task => completion.Task;

            public bool IsCompleted => completion.Task.IsCompleted;

            public void Cancel() => completion.TrySetResult(false);

            public bool TryHandOverSlot() => completion.TrySetResult(true);
        }
    }
}

/// <summary>Artwork and its blurred backdrop, owned together until each is handed on or disposed.</summary>
internal sealed class ArtworkWithBackdrop(Bitmap? cover, Bitmap? backdrop) : IDisposable
{
    public Bitmap? Cover { get; } = cover;

    public Bitmap? Backdrop { get; } = backdrop;

    public void Dispose()
    {
        Cover?.Dispose();
        Backdrop?.Dispose();
    }
}
