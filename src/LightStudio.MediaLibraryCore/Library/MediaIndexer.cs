using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Constant;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Tools;
using LightStudio.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Library;

/// <summary>
/// Scans the configured media folders into the <c>VideoFiles</c> and <c>ImageFiles</c> tables.
/// Runs after <see cref="MusicIndexer"/> and keeps the music tables independent.
/// </summary>
public class MediaIndexer
{
    private readonly MediaLibraryDatabase m_database;
    private readonly IMultiMediaPlatformIO platform;
    private readonly IEqualityComparer<string> pathComparer;
    private int m_scannedCount;

    public MediaIndexer(MediaLibraryDatabase database)
        : this(database, IMultiMediaPlatformIO.Instance, DefaultPathComparer())
    {
    }

    public MediaIndexer(MediaLibraryDatabase database, IMultiMediaPlatformIO platform)
        : this(database, platform, DefaultPathComparer())
    {
    }

    public MediaIndexer(
        MediaLibraryDatabase database,
        IMultiMediaPlatformIO platform,
        IEqualityComparer<string> pathComparer)
    {
        m_database = database;
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.pathComparer = pathComparer ?? throw new ArgumentNullException(nameof(pathComparer));
    }

    /// <summary>
    /// Photo, video, and disc containers considered during scanning.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedFormats = new HashSet<string>(
        MediaContainerFormats.Video.Concat(MediaContainerFormats.DiscImage).Concat(MediaContainerFormats.Image),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Sidecar audio containers picked up as extra tracks for a video.</summary>
    public static readonly IReadOnlySet<string> ExternalAudioFormats = MediaContainerFormats.ExternalAudio;

    /// <summary>Sidecar subtitle files picked up as extra tracks for a video.</summary>
    public static readonly IReadOnlySet<string> ExternalSubtitleFormats = MediaContainerFormats.ExternalSubtitle;

    public event EventHandler<int> IndexItemAdded;

    public int ImageIndexedCount { get; private set; }

    public bool HasChanges { get; private set; }

    /// <summary>
    /// Walks <paramref name="folders"/> and brings the video and image tables in line with what is on disk.
    /// Returns the number of videos in the library afterwards along with per-file failures.
    /// </summary>
    public Task<Tuple<int, List<Tuple<string, Exception>>>> ScanAsync(
        IEnumerable<string> folders, bool removeMissingFiles = true) =>
        ScanAsync(folders, CancellationToken.None, removeMissingFiles);

    public async Task<Tuple<int, List<Tuple<string, Exception>>>> ScanAsync(
        IEnumerable<string> folders, CancellationToken cancellationToken, bool removeMissingFiles = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new ScanWorkBudget(platform, cancellationToken);
        HasChanges = false;
        var exceptions = new ConcurrentBag<Tuple<string, Exception>>();
        var excluded = PathExclusion.GetExcludedPath();
        var discPlatform = platform as IVideoDiscPlatformIO;
        bool supportsDiscImages = discPlatform is not null &&
            (discPlatform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
                discPlatform.SupportsVideoDisc(VideoMediaKind.BluRay));

        // Everything already indexed, keyed by path. Entries are removed as they are seen on disk;
        // whatever is left at the end has been deleted or moved.
        var known = new Dictionary<string, DbVideoFile>(pathComparer);
        foreach (var existing in await m_database.GetAllVideoFilesAsync().ConfigureAwait(false))
        {
            await budget.YieldAsync();
            known[existing.Path] = existing;
        }

        var knownImages = new Dictionary<string, DbImageFile>(pathComparer);
        foreach (var existing in await m_database.GetAllImageFilesAsync().ConfigureAwait(false))
        {
            await budget.YieldAsync();
            knownImages[existing.Path] = existing;
        }

        var candidateChannel = Channel.CreateBounded<MediaCandidate>(new BoundedChannelOptions(256)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
        var resultChannel = Channel.CreateBounded<(DbVideoFile Video, DbImageFile Image)>(new BoundedChannelOptions(64)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        var seen = new ConcurrentDictionary<string, byte>(pathComparer);
        var writerTask = WriteAsync(resultChannel.Reader, exceptions);
        using var locations = new PhotoLocationResolver(platform);

        var workerCount = Math.Max(1, Math.Min(4, platform.OptimalIoThreadCount));
        var workers = new List<Task>(workerCount);
        for (var i = 0; i < workerCount; i++)
        {
            workers.Add(ProbeWorkerAsync(candidateChannel.Reader, resultChannel.Writer, known, knownImages, seen,
                locations, exceptions, cancellationToken));
        }

        var enumerationComplete = true;
        foreach (var root in folders)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var stack = new Stack<(string Path, string ParentPath)>();
            stack.Push((root, null));
            while (stack.Count != 0)
            {
                var (current, parent) = stack.Pop();
                try
                {
                    await budget.YieldAsync();
                    string[] directories = await platform
                        .ListFoldersFullNameInPathAsync(current, cancellationToken).ConfigureAwait(false);
                    var files = await platform
                        .ListFilesFullNameInPathAsync(current, cancellationToken).ConfigureAwait(false);

                    VideoDiscStructureInfo disc = await VideoDiscStructure
                        .DetectAsync(platform, current, directories)
                        .ConfigureAwait(false);
                    if (disc is not null)
                    {
                        if (excluded.Any(path => current.IsSubPathOf(path)))
                        {
                            continue;
                        }

                        if (discPlatform is null || !discPlatform.SupportsVideoDisc(disc.MediaKind))
                        {
                            continue;
                        }

                        VideoDiscMeasurement measurement = await VideoDiscStructure
                            .MeasureAsync(platform, current)
                            .ConfigureAwait(false);
                        DateTimeOffset lastModified = measurement.LastModified > DateTimeOffset.MinValue
                            ? measurement.LastModified
                            : disc.MarkerLastModified;
                        await candidateChannel.Writer.WriteAsync(new MediaCandidate(
                            current,
                            LeafName(current),
                            lastModified,
                            measurement.SizeInBytes,
                            parent ?? current,
                            root,
                            disc.MediaKind,
                            null,
                            null), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var directory in directories)
                    {
                        await budget.YieldAsync();
                        stack.Push((directory, current));
                    }

                    foreach (var file in files)
                    {
                        await budget.YieldAsync();
                        bool isImage = MediaContainerFormats.IsImage(file.Name);
                        if ((!isImage && !MediaContainerFormats.IsVideo(file.Name)) ||
                            (MediaContainerFormats.DiscImage.Contains(Path.GetExtension(file.Name)) &&
                                !supportsDiscImages) ||
                            excluded.Any(x => file.Path.IsSubPathOf(x)))
                        {
                            continue;
                        }

                        await candidateChannel.Writer.WriteAsync(new MediaCandidate(
                            file.Path,
                            file.Name,
                            file.LastModifiedUtc,
                            file.SizeInBytes,
                            current,
                            root,
                            VideoMediaKind.File,
                            isImage ? null : await FindSidecarsAsync(file, files, ExternalAudioFormats, budget).ConfigureAwait(false),
                            isImage ? null : await FindSidecarsAsync(file, files, ExternalSubtitleFormats, budget).ConfigureAwait(false),
                            isImage,
                            file.CreatedUtc), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    enumerationComplete = false;
                    break;
                }
                catch (Exception ex) when (FileAccessErrors.IsAccessDenied(ex))
                {
                    enumerationComplete = false;
                    AppLog.Warning("Library.Scan", $"Skipping inaccessible media folder {current}.", ex);
                }
                catch (Exception ex)
                {
                    enumerationComplete = false;
                    exceptions.Add(new Tuple<string, Exception>(current, ex));
                }
            }
        }

        candidateChannel.Writer.Complete();
        await Task.WhenAll(workers).ConfigureAwait(false);
        locations.Dispose();
        resultChannel.Writer.Complete();

        await writerTask.ConfigureAwait(false);

        if (enumerationComplete && removeMissingFiles && !cancellationToken.IsCancellationRequested)
        {
            await RemoveDeletedAsync(known, knownImages, seen, exceptions, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var total = await m_database.CountVideoFilesAsync().ConfigureAwait(false);
        ImageIndexedCount = await m_database.CountImageFilesAsync().ConfigureAwait(false);
        m_scannedCount = 0;
        return new Tuple<int, List<Tuple<string, Exception>>>(total, exceptions.ToList());
    }

    /// <summary>
    /// Matches <c>video-name.&lt;anything&gt;.&lt;ext&gt;</c> and bare <c>video-name.&lt;ext&gt;</c>
    /// beside the video, which is the convention for language-tagged sidecar tracks
    /// (for example, <c>show.mkv</c> with <c>show.ja.ass</c>, <c>show.srt</c>, or <c>show.ja.mka</c>).
    /// </summary>
    private static async Task<List<string>> FindSidecarsAsync(
        IFile video, IReadOnlyList<IFile> siblings, IReadOnlySet<string> extensions, ScanWorkBudget budget)
    {
        var stem = Path.GetFileNameWithoutExtension(video.Name);
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        var prefix = stem + ".";
        List<string> matches = null;
        foreach (var sibling in siblings)
        {
            await budget.YieldAsync();
            if (ReferenceEquals(sibling, video) ||
                !sibling.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !extensions.Contains(Path.GetExtension(sibling.Name)))
            {
                continue;
            }

            (matches ??= new List<string>()).Add(sibling.Path);
        }

        matches?.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }

    private async Task ProbeWorkerAsync(
        ChannelReader<MediaCandidate> input,
        ChannelWriter<(DbVideoFile Video, DbImageFile Image)> output,
        IReadOnlyDictionary<string, DbVideoFile> known,
        IReadOnlyDictionary<string, DbImageFile> knownImages,
        ConcurrentDictionary<string, byte> seen,
        PhotoLocationResolver locations,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        CancellationToken cancellationToken)
    {
        var budget = new ScanWorkBudget(platform, cancellationToken);
        while (await input.WaitToReadAsync().ConfigureAwait(false))
        {
            if (!input.TryRead(out var candidate))
            {
                continue;
            }
            if (cancellationToken.IsCancellationRequested) continue;

            try
            {
                await budget.YieldAsync();
                if (!seen.TryAdd(candidate.Path, 0))
                {
                    continue;
                }

                if (candidate.IsImage)
                {
                    await ProbeImageAsync(candidate, output, knownImages, locations, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var audioPaths = DbVideoFile.JoinPaths(candidate.ExternalAudio);
                var subtitlePaths = DbVideoFile.JoinPaths(candidate.ExternalSubtitles);
                DateTimeOffset lastModified = candidate.LastModified;
                DbVideoAudioStream[] sidecarAudioStreams = await ProbeSidecarAudioStreamsAsync(
                    candidate.ExternalAudio,
                    exceptions, cancellationToken).ConfigureAwait(false);
                bool isDisc = candidate.MediaKind != VideoMediaKind.File ||
                    MediaContainerFormats.DiscImage.Contains(Path.GetExtension(candidate.Path));

                if (known.TryGetValue(candidate.Path, out var existing) &&
                    existing.FileLastModifiedDate >= lastModified &&
                    (!isDisc ||
                        (existing.MediaKind != VideoMediaKind.File &&
                            (candidate.MediaKind == VideoMediaKind.File ||
                                existing.MediaKind == candidate.MediaKind) &&
                            existing.FileSize == candidate.FileSize)))
                {
                    // Content is unchanged, but a sidecar may have appeared or gone away since the
                    // last scan, or its own stream table may have changed.
                    DbVideoAudioStream[] audioStreams =
                    [
                        .. existing.AudioStreams.Where(stream => stream.SourcePath is null),
                        .. sidecarAudioStreams,
                    ];
                    if (existing.ExternalAudioPaths != audioPaths ||
                        existing.ExternalSubtitlePaths != subtitlePaths ||
                        !AudioStreamsEqual(existing.AudioStreams, audioStreams))
                    {
                        existing.ExternalAudioPaths = audioPaths;
                        existing.ExternalSubtitlePaths = subtitlePaths;
                        existing.AudioStreams = audioStreams;
                        await output.WriteAsync((existing, null), cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var probe = await platform
                    .ProbeVideoAsync(candidate.Path, withPreview: false, cancellationToken).ConfigureAwait(false);
                if (probe is null)
                {
                    // Not a video: an audio-only container with a video-ish extension, or unreadable.
                    continue;
                }

                long fileSize = candidate.FileSize;
                if (fileSize <= 0 && candidate.MediaKind == VideoMediaKind.File)
                {
                    var properties = await platform
                        .GetMediaFilePropertiesAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                    fileSize = properties?.Length ?? 0;
                }

                VideoMediaKind mediaKind = candidate.MediaKind == VideoMediaKind.File
                    ? probe.MediaKind
                    : candidate.MediaKind;

                await output.WriteAsync((new DbVideoFile
                {
                    Path = candidate.Path,
                    MediaKind = mediaKind,
                    DiscTitle = probe.DiscTitle,
                    FolderPath = candidate.FolderPath,
                    LibraryRoot = candidate.LibraryRoot,
                    FileName = candidate.FileName,
                    Title = string.IsNullOrWhiteSpace(probe.Title) ? null : probe.Title,
                    Duration = probe.Duration,
                    Width = probe.Width,
                    Height = probe.Height,
                    FrameRate = probe.FrameRate,
                    VideoCodec = probe.VideoCodec,
                    VideoStreamIndex = probe.VideoStreamIndex,
                    FileSize = fileSize,
                    BitRate = probe.BitRate > 0
                        ? probe.BitRate
                        : mediaKind == VideoMediaKind.File
                            ? CalculateBitRate(fileSize, probe.Duration)
                            : 0,
                    ExternalAudioPaths = audioPaths,
                    ExternalSubtitlePaths = subtitlePaths,
                    DatabaseItemAddedDate = existing?.DatabaseItemAddedDate ?? DateTimeOffset.UtcNow,
                    FileLastModifiedDate = lastModified,
                    AudioStreams =
                    [
                        .. probe.AudioStreams.Select(stream => ToDatabaseAudioStream(stream, sourcePath: null)),
                        .. sidecarAudioStreams,
                    ],
                }, null), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (FileAccessErrors.IsAccessDenied(ex))
            {
                AppLog.Warning("Library.Scan", $"Skipping inaccessible media file {candidate.Path}.", ex);
            }
            catch (Exception ex)
            {
                exceptions.Add(new Tuple<string, Exception>(candidate.Path, ex));
            }
        }
    }

    private async Task ProbeImageAsync(
        MediaCandidate candidate,
        ChannelWriter<(DbVideoFile Video, DbImageFile Image)> output,
        IReadOnlyDictionary<string, DbImageFile> known,
        PhotoLocationResolver locations,
        CancellationToken cancellationToken)
    {
        if (known.TryGetValue(candidate.Path, out var existing) &&
            existing.FileLastModifiedDate == candidate.LastModified &&
            existing.FileSize == candidate.FileSize)
        {
            var relocated = locations.NeedsUpdate(existing) &&
                await locations.ApplyAsync(existing, cancellationToken).ConfigureAwait(false);
            if (relocated || existing.FolderPath != candidate.FolderPath || existing.LibraryRoot != candidate.LibraryRoot ||
                existing.FileName != candidate.FileName || existing.FileCreatedDate != candidate.CreatedUtc)
            {
                existing.FolderPath = candidate.FolderPath;
                existing.LibraryRoot = candidate.LibraryRoot;
                existing.FileName = candidate.FileName;
                existing.FileCreatedDate = candidate.CreatedUtc;
                await output.WriteAsync((null, existing), cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var metadata = await platform.ProbeImageAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
        var image = new DbImageFile
        {
            Path = candidate.Path,
            FolderPath = candidate.FolderPath,
            LibraryRoot = candidate.LibraryRoot,
            FileName = candidate.FileName,
            FileSize = candidate.FileSize,
            Width = metadata?.Width ?? 0,
            Height = metadata?.Height ?? 0,
            DateTaken = metadata?.DateTaken,
            FileCreatedDate = candidate.CreatedUtc,
            FileLastModifiedDate = candidate.LastModified,
            DatabaseItemAddedDate = existing?.DatabaseItemAddedDate ?? DateTimeOffset.UtcNow,
            Latitude = metadata?.Latitude,
            Longitude = metadata?.Longitude,
        };
        await locations.ApplyAsync(image, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync((null, image), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DbVideoAudioStream[]> ProbeSidecarAudioStreamsAsync(
        IReadOnlyList<string> paths,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        CancellationToken cancellationToken)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var result = new List<DbVideoAudioStream>();
        foreach (string path in paths)
        {
            try
            {
                IReadOnlyList<AudioStreamProbeInfo> streams = await platform
                    .ProbeAudioStreamsAsync(path, cancellationToken).ConfigureAwait(false);
                result.AddRange(streams.Select(stream => ToDatabaseAudioStream(stream, path)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (FileAccessErrors.IsAccessDenied(ex))
            {
                AppLog.Warning("Library.Scan", $"Skipping inaccessible sidecar audio {path}.", ex);
            }
            catch (Exception ex)
            {
                exceptions.Add(new Tuple<string, Exception>(path, ex));
            }
        }

        return result.ToArray();
    }

    private static DbVideoAudioStream ToDatabaseAudioStream(AudioStreamProbeInfo stream, string sourcePath) => new()
    {
        SourcePath = sourcePath,
        StreamIndex = stream.StreamIndex,
        CodecName = stream.CodecName,
    };

    private static long CalculateBitRate(long fileSize, TimeSpan duration)
    {
        if (fileSize <= 0 || duration <= TimeSpan.Zero)
        {
            return 0;
        }

        double bitRate = fileSize * 8d / duration.TotalSeconds;
        return double.IsFinite(bitRate) && bitRate < long.MaxValue ? (long)Math.Round(bitRate) : 0;
    }

    private static bool AudioStreamsEqual(
        IReadOnlyList<DbVideoAudioStream> left,
        IReadOnlyList<DbVideoAudioStream> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            DbVideoAudioStream first = left[index];
            DbVideoAudioStream second = right[index];
            if (!string.Equals(first.SourcePath, second.SourcePath, StringComparison.Ordinal) ||
                first.StreamIndex != second.StreamIndex ||
                !string.Equals(first.CodecName, second.CodecName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteAsync(
        ChannelReader<(DbVideoFile Video, DbImageFile Image)> input,
        ConcurrentBag<Tuple<string, Exception>> exceptions)
    {
        try
        {
            while (await input.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<(DbVideoFile Video, DbImageFile Image)>(10);
                while (batch.Count < 10 && input.TryRead(out var item))
                    batch.Add(item);
                var written = 0;
                await m_database.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var file in batch)
                {
                    try
                    {
                        if (file.Image is { } image)
                            await m_database.UpsertImageFileAsync(image).ConfigureAwait(false);
                        else
                            await m_database.UpsertVideoFileAsync(file.Video).ConfigureAwait(false);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(new Tuple<string, Exception>(file.Image?.Path ?? file.Video.Path, ex));
                    }
                }
                await m_database.CommitTransactionAsync().ConfigureAwait(false);
                HasChanges |= written > 0;
                m_scannedCount += written;
                IndexItemAdded?.Invoke(this, m_scannedCount);
            }
        }
        catch (Exception ex)
        {
            exceptions.Add(new Tuple<string, Exception>(DatabaseConstants.DbFileName, ex));
            try
            {
                await m_database.RollbackTransactionAsync().ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                exceptions.Add(new Tuple<string, Exception>(DatabaseConstants.DbFileName, rollbackException));
            }
            await foreach (var unused in input.ReadAllAsync().ConfigureAwait(false)) { }
        }
    }

    private async Task RemoveDeletedAsync(
        IReadOnlyDictionary<string, DbVideoFile> known,
        IReadOnlyDictionary<string, DbImageFile> knownImages,
        ConcurrentDictionary<string, byte> seen,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        CancellationToken cancellationToken)
    {
        var budget = new ScanWorkBudget(platform, cancellationToken);
        try
        {
            var deleted = known.Select(pair => (pair.Key, pair.Value.Id, IsImage: false))
                .Concat(knownImages.Select(pair => (pair.Key, pair.Value.Id, IsImage: true)));
            var batch = new List<(int Id, bool IsImage)>(30);
            foreach (var file in deleted)
            {
                await budget.YieldAsync();
                if (seen.ContainsKey(file.Key)) continue;
                batch.Add((file.Id, file.IsImage));
                if (batch.Count == 30)
                    await FlushAsync().ConfigureAwait(false);
            }
            if (batch.Count > 0)
                await FlushAsync().ConfigureAwait(false);

            async Task FlushAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                await m_database.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var file in batch)
                {
                    if (file.IsImage)
                        await m_database.DeleteImageFileAsync(file.Id).ConfigureAwait(false);
                    else
                        await m_database.DeleteVideoFileAsync(file.Id).ConfigureAwait(false);
                }
                await m_database.CommitTransactionAsync().ConfigureAwait(false);
                HasChanges = true;
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            exceptions.Add(new Tuple<string, Exception>(DatabaseConstants.DbFileName, ex));
            await m_database.RollbackTransactionAsync().ConfigureAwait(false);
        }
    }

    private sealed record MediaCandidate(
        string Path,
        string FileName,
        DateTimeOffset LastModified,
        long FileSize,
        string FolderPath,
        string LibraryRoot,
        VideoMediaKind MediaKind,
        List<string> ExternalAudio,
        List<string> ExternalSubtitles,
        bool IsImage = false,
        DateTimeOffset? CreatedUtc = null);

    private static string LeafName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static StringComparer DefaultPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
