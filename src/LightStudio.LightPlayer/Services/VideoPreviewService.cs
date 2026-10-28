using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Loads preview frames for video tiles, throttled so browsing a large video library cannot
/// saturate the thread pool or pile up decoded bitmaps. The frames themselves are produced on
/// demand behind <see cref="IUnifiedVideoLibraryQueryService.GetVideoPreviewAsync"/>.
/// </summary>
public sealed class VideoPreviewService(IUnifiedVideoLibraryQueryService videoLibraryQuery)
{
    /// <summary>Previews are wide, so they decode to a wider target than a square album cover.</summary>
    internal const int DecodeWidth = 480;

    private static readonly int MaxConcurrentLoads = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
    private static readonly int BrowserMaxConcurrentLoads = 4;

    private readonly SemaphoreSlim throttle = OperatingSystem.IsBrowser()
        ? new(BrowserMaxConcurrentLoads, BrowserMaxConcurrentLoads)
        : new(MaxConcurrentLoads, MaxConcurrentLoads);

    /// <summary>
    /// Returns the decoded preview for a video, or null when none could be produced (the tile then
    /// keeps its placeholder).
    /// </summary>
    public async Task<IImage?> GetPreviewAsync(string? path, CancellationToken cancellationToken = default)
        => string.IsNullOrEmpty(path)
            ? null
            : await GetPreviewAsync(MediaReference.Local(path), cancellationToken).ConfigureAwait(false);

    public async Task<IImage?> GetPreviewAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(media.Path))
        {
            return null;
        }

        try
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var content = await videoLibraryQuery.GetVideoPreviewAsync(media, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            return await ImageDecoding.DecodeThumbnailAsync(content, DecodeWidth, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Preview.Load", $"Could not decode preview for {media.Path}.", exception);
            return null;
        }
        finally
        {
            throttle.Release();
        }
    }
}
