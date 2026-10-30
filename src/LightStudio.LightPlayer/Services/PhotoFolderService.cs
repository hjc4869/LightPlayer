using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>One picture in a media library or folder listing.</summary>
public sealed record PhotoFileReference(LibrarySourceId Source, string Path, string FileName, long SizeInBytes,
    DateTimeOffset LastModifiedUtc = default, DateTimeOffset? CreatedUtc = null, DateTimeOffset? DateTaken = null,
    string? Country = null, string? State = null, string? City = null)
{
    /// <summary>The picture with the capture date and location recorded by the library index.</summary>
    public PhotoFileReference WithIndexedMetadata(DbImageFile image) => this with
    {
        DateTaken = image.DateTaken,
        Country = image.Country,
        State = image.State,
        City = image.City,
    };

    /// <summary>Rough size for the tile subtitle, which is all a picture has to say before it opens.</summary>
    public string DisplaySize => SizeInBytes switch
    {
        <= 0 => string.Empty,
        >= 1024 * 1024 => string.Create(
            CultureInfo.CurrentCulture,
            $"{SizeInBytes / (1024.0 * 1024.0):0.#} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{SizeInBytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{SizeInBytes} B"),
    };

    /// <summary>True when the picture sits on this device, and so can be read off the disk.</summary>
    public bool IsLocal => Source.IsLocal;

    public MediaReference Media => new(Source, Path);
}

/// <summary>
/// Lists the pictures in a library folder and decodes their thumbnails when a tile asks for one.
/// </summary>
/// <remarks>
/// <para>
/// Folders are read when opened and thumbnails are decoded as tiles come into view. Encoded
/// thumbnails share the resolution and bounded memory cache of video previews, with persistent
/// storage for local items.
/// </para>
/// <para>
/// A remote library's pictures arrive in the folder listing instead, since only the server can read
/// its own disk; its thumbnails are asked of the server the same way a video's preview is.
/// </para>
/// <para>
/// The decode is throttled the same way stored video previews are, because scrolling a folder of
/// several hundred pictures would otherwise start a decode for every one of them at once. Encoding
/// from source pictures runs on its own bounded queue that serves the newest request first and
/// finishes what it started even after the tile that asked has scrolled away.
/// </para>
/// </remarks>
public sealed class PhotoFolderService
{
    private static readonly int MaxConcurrentLoads = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
    private static readonly int BrowserMaxConcurrentLoads = 4;

    private readonly SemaphoreSlim throttle = OperatingSystem.IsBrowser()
        ? new(BrowserMaxConcurrentLoads, BrowserMaxConcurrentLoads)
        : new(MaxConcurrentLoads, MaxConcurrentLoads);

    // Encoding from the source picture can take far longer than loading a stored thumbnail, so it is bounded on its own.
    private readonly GenerationQueue generation;
    private readonly ThumbnailMemoryCache thumbnails;

    public PhotoFolderService(ThumbnailMemoryCache? thumbnails = null)
    {
        this.thumbnails = thumbnails ?? ThumbnailMemoryCache.Shared;
        generation = new GenerationQueue(this.thumbnails, OperatingSystem.IsBrowser() ? BrowserMaxConcurrentLoads : MaxConcurrentLoads);
    }

    public static async Task<ImageProbeInfo?> ProbeMetadataAsync(
        IMultiMediaPlatformIO platform, string path, CancellationToken cancellationToken = default)
    {
        using var stream = await platform.OpenMediaFileAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ReadMetadata(stream), cancellationToken).ConfigureAwait(false);
    }

    public static ImageProbeInfo ReadMetadata(Stream stream)
    {
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return ReadMetadata(buffered);
        }

        var exif = ExifMetadata.ReadPhoto(stream);
        stream.Position = 0;
        (int width, int height) = StillImage.ReadPixelSize(stream);
        return new ImageProbeInfo(
            width > 0 ? width : Math.Max(0, exif.Width),
            height > 0 ? height : Math.Max(0, exif.Height),
            DateTimeOffset.TryParse(exif.DateTaken, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var taken) ? taken : null,
            exif.Latitude, exif.Longitude);
    }

    public static Task<StillImageThumbnail?> DecodeIndexImageAsync(
        IMultiMediaPlatformIO platform, string path, CancellationToken cancellationToken = default) =>
        ImageDecoding.DecodePhotoThumbnailAsync(new PhotoDecodeRequest(platform, path), 1536, cancellationToken);

    /// <summary>
    /// Returns the pictures supplied by the platform or server's folder listing.
    /// </summary>
    public IReadOnlyList<PhotoFileReference> List(LibrarySourceId source, VideoFolderListing listing)
    {
        return listing.Entries
            .Where(entry => entry.Kind == VideoFolderEntryKind.Photo)
            .Select(entry => new PhotoFileReference(source, entry.Path, entry.Name, entry.SizeInBytes,
                entry.LastModifiedUtc, entry.CreatedUtc))
            .ToList();
    }

    /// <summary>
    /// The picture at <paramref name="path"/>, or null when it is no longer there to read. Used by
    /// lists that hold nothing but a path, such as the recently viewed folder. A remote picture is
    /// taken at its word: only its server can say whether it is still there, and the tile finds out
    /// when it asks for a thumbnail.
    /// </summary>
    public PhotoFileReference? Describe(LibrarySourceId source, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (source != LibrarySourceId.Local)
            return new PhotoFileReference(source, path, VideoFolderTree.LeafName(path), 0);

        if (!PlatformServices.SupportsLocalFileAccess)
            return null;

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new PhotoFileReference(source, file.FullName, file.Name, file.Length,
                file.LastWriteTimeUtc, file.CreationTimeUtc > DateTime.UnixEpoch ? file.CreationTimeUtc : null) : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a cached tile thumbnail or decodes one, and returns null when the picture cannot be
    /// read. Already upright: a tile is plain pixels with nowhere to carry camera rotation metadata.
    /// </summary>
    public async Task<IImage?> GetThumbnailAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cached = await thumbnails.TryGetAsync(ThumbnailCacheKind.Photo, path, cancellationToken)
                    .ConfigureAwait(false);
                if (cached.Found)
                {
                    return await DecodeAsync(cached.Thumbnail, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                throttle.Release();
            }

            byte[]? encoded = await generation.GenerateAsync(path, cancellationToken).ConfigureAwait(false);
            if (encoded is null)
            {
                return null;
            }

            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await DecodeAsync(encoded, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Photo", $"Could not decode thumbnail for {path}.", exception);
            if (!cancellationToken.IsCancellationRequested && !VideoPreviewFactory.IsTransientFailure(exception))
            {
                await thumbnails.SetAsync(ThumbnailCacheKind.Photo, path, null).ConfigureAwait(false);
            }

            return null;
        }
    }

    private static Task<IImage?> DecodeAsync(byte[]? encoded, CancellationToken cancellationToken) =>
        ImageDecoding.DecodeThumbnailAsync(encoded, VideoPreviewService.DecodeWidth, cancellationToken);

    /// <summary>
    /// Encodes thumbnails from source pictures on a bounded number of workers, newest request first. Requests for the
    /// same picture share one job, and leaving only abandons a job that has not started: a started job finishes and
    /// stores its thumbnail even after every request for it has gone, so scrolling past a picture never discards the
    /// work spent on it.
    /// </summary>
    private sealed class GenerationQueue(ThumbnailMemoryCache thumbnails, int workers)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, Job> jobs = [];
        private readonly List<Job> waiting = [];
        private int running;

        public async Task<byte[]?> GenerateAsync(string path, CancellationToken cancellationToken)
        {
            Job? job;
            Job? started;
            lock (gate)
            {
                if (!jobs.TryGetValue(path, out job))
                {
                    jobs.Add(path, job = new Job(path));
                    waiting.Add(job);
                }

                job.Requests++;
                started = TakeNext();
            }

            if (started is not null)
            {
                _ = RunAsync(started);
            }

            try
            {
                return await job.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    if (--job.Requests == 0 && !job.Started)
                    {
                        waiting.Remove(job);
                        jobs.Remove(job.Path);
                    }
                }

                throw;
            }
        }

        private Job? TakeNext()
        {
            if (running >= workers || waiting.Count == 0)
            {
                return null;
            }

            Job job = waiting[^1];
            waiting.RemoveAt(waiting.Count - 1);
            job.Started = true;
            running++;
            return job;
        }

        private async Task RunAsync(Job job)
        {
            byte[]? encoded = null;
            bool remember = true;
            try
            {
                encoded = await ImageDecoding.EncodePhotoThumbnailAsync(
                    new PhotoDecodeRequest(IMultiMediaPlatformIO.Instance, job.Path),
                    VideoPreviewFactory.PreviewMaxDimension).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Photo", $"Could not decode thumbnail for {job.Path}.", exception);
                remember = !VideoPreviewFactory.IsTransientFailure(exception);
            }

            try
            {
                if (remember)
                {
                    await thumbnails.SetAsync(ThumbnailCacheKind.Photo, job.Path, encoded).ConfigureAwait(false);
                }
            }
            finally
            {
                Job? next;
                lock (gate)
                {
                    jobs.Remove(job.Path);
                    running--;
                    next = TakeNext();
                }

                job.Completion.TrySetResult(encoded);
                if (next is not null)
                {
                    _ = RunAsync(next);
                }
            }
        }

        private sealed class Job(string path)
        {
            public string Path { get; } = path;

            public TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Requests { get; set; }

            public bool Started { get; set; }
        }
    }
}
