using LightStudio.MediaLibraryCore.CueIndex;
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

using MediaMetadata = Tuple<MediaInfo, string, DateTimeOffset>;

public class MusicIndexer
{
    private readonly MediaLibraryDatabase m_database;
    private readonly IMultiMediaPlatformIO platform;
    private readonly IEqualityComparer<string> pathComparer;
    private readonly Func<string, Task<Stream>> openMediaFileForScan;
    private readonly bool caseSensitivePaths;
    private int m_scannedCount = 0;

    public MusicIndexer(MediaLibraryDatabase database)
        : this(database, IMultiMediaPlatformIO.Instance, DefaultPathComparer())
    {
    }

    public MusicIndexer(MediaLibraryDatabase database, IMultiMediaPlatformIO platform)
        : this(database, platform, DefaultPathComparer())
    {
    }

    public MusicIndexer(
        MediaLibraryDatabase database,
        IMultiMediaPlatformIO platform,
        IEqualityComparer<string> pathComparer,
        Func<string, Task<Stream>> openMediaFileForScan = null)
    {
        m_database = database;
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.pathComparer = pathComparer ?? throw new ArgumentNullException(nameof(pathComparer));
        this.openMediaFileForScan = openMediaFileForScan ?? (path => platform.OpenMediaFileAsync(path));
        caseSensitivePaths = !pathComparer.Equals("a", "A");
    }

    bool AutoIgnoreDrmProtectedFiles
    {
        get { return platform.GetConfigurationValue<bool>("AutoIgnoreDrmProtectedFiles"); }
    }

    public static HashSet<string>
        SupportedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".tta",
            ".tak",
            ".ape",
            ".mp3",
            ".wma",
            ".flac",
            ".wav",
            ".ogg",
            ".m4a"
        };

    public event EventHandler<int> IndexItemAdded;
    public event EventHandler<DbMediaFile[]> NewItemAdded;

    public bool HasChanges { get; private set; }

    private async Task<int> UpdateDbAsync(
        ChannelReader<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)> infoReader,
        ChannelReader<DbMediaFile> removeReader,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        CancellationToken cancellationToken)
    {
        var send = new List<DbMediaFile>(30);
        try
        {
            while (await infoReader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<(string Path, MediaInfo Info, DateTimeOffset Date, ManagedAudioIndexCue Cue)>(30);
                while (batch.Count < 30 && infoReader.TryRead(out var item))
                    batch.Add(item);
                await m_database.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var (path, info, date, cue) in batch)
                {
                    try
                    {
                        var dbFile = info.ToDbMediaFile(date);
                        if (string.IsNullOrWhiteSpace(info.Title))
                            dbFile.Title = MediaIdentityNames.FileName(path);
                        dbFile.Path = path;
                        if (cue != null)
                            dbFile.StartTime = (int)cue.StartTime.TotalMilliseconds;
                        if (string.IsNullOrWhiteSpace(info.Album)) info.Album = null;
                        if (string.IsNullOrWhiteSpace(info.Artist)) info.Artist = null;
                        if (string.IsNullOrWhiteSpace(info.AlbumArtist)) dbFile.AlbumArtist = null;
                        await m_database.InsertMediaFileAsync(dbFile).ConfigureAwait(false);
                        send.Add(dbFile);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(new Tuple<string, Exception>(path, ex));
                    }
                }
                await m_database.CommitTransactionAsync().ConfigureAwait(false);
                if (send.Count > 0)
                {
                    HasChanges = true;
                    m_scannedCount += send.Count;
                    NewItemAdded?.Invoke(this, send.ToArray());
                    send.Clear();
                    IndexItemAdded?.Invoke(this, m_scannedCount);
                }
            }

            while (await removeReader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<DbMediaFile>(30);
                while (batch.Count < 30 && removeReader.TryRead(out var item))
                    batch.Add(item);
                if (cancellationToken.IsCancellationRequested)
                    continue;
                await m_database.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var remove in batch)
                {
                    await m_database.DeleteMediaFileAsync(remove.Id).ConfigureAwait(false);
                }
                await m_database.CommitTransactionAsync().ConfigureAwait(false);
                HasChanges |= batch.Count > 0;
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
            await foreach (var unused in infoReader.ReadAllAsync().ConfigureAwait(false)) { }
            await foreach (var unused in removeReader.ReadAllAsync().ConfigureAwait(false)) { }
        }
        var scanned = m_scannedCount;
        m_scannedCount = 0;
        return scanned;
    }

    class FileTrackInfo
    {
        public string FilePath { get; }
        public DateTimeOffset LastModifiedTime { get; }
        public List<string> TrackIdentifiers { get; } = new List<string>();
        public FileTrackInfo(string filePath, DateTimeOffset lastModifiedTime)
        {
            FilePath = filePath;
            LastModifiedTime = lastModifiedTime;
        }
    }

    private async Task ParallelMediaScanWorker(
        ChannelReader<IFile> input,
        ConcurrentDictionary<string, DbMediaFile> trackIdentifierDict,
        IReadOnlyDictionary<string, FileTrackInfo> fileInfoDict,
        ChannelWriter<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)> infoChannel,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        ConcurrentDictionary<string, byte> failedPaths,
        bool ignoreDrm,
        CancellationToken cancellationToken)
    {
        var budget = new ScanWorkBudget(platform, cancellationToken);
        while (await input.WaitToReadAsync())
        {
            if (!input.TryRead(out var file)) continue;
            if (cancellationToken.IsCancellationRequested) continue;
            try
            {
                await budget.YieldAsync();
                async Task AddMediaFile(IFile file, ChannelWriter<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)> infoChannel, bool ignoreDrm)
                {
                    try
                    {
                        var date = file.LastModifiedUtc;
                        var info = await platform.GetMediaInfoAsync(file.Path, cancellationToken);
                        if (ignoreDrm &&
                            info.AllProperties.TryGetValue("encryption", out var enc) &&
                            !string.IsNullOrWhiteSpace(enc))
                        {
                            throw new DrmProtectedException();
                        }

                        CueFile idx;
                        if (!info.AllProperties.TryGetValue("cuesheet", out var cue) ||
                            string.IsNullOrWhiteSpace(cue) ||
                            (idx = CueFile.CreateFromString(cue)).Indices.Count == 0)
                        {
                            await infoChannel.WriteAsync((file.Path, info, date, null), cancellationToken);
                        }
                        else
                        {
                            var totalDuration = info.Duration;
                            foreach (var track in idx.Indices)
                            {
                                await budget.YieldAsync();
                                if (track.StartTime + track.Duration > totalDuration)
                                {
                                    continue;
                                }

                                if (track.Duration == TimeSpan.Zero)
                                {
                                    track.TrackInfo.Duration
                                        = track.Duration
                                        = totalDuration - track.StartTime;
                                }

                                await infoChannel.WriteAsync((file.Path, track.TrackInfo, file.LastModifiedUtc, track), cancellationToken);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Strings.Resources.AudioFileMetadataFailureExceptionMessage, ex);
                    }
                }

                if (fileInfoDict.TryGetValue(file.Path, out var fileTrackInfo))
                {
                    var lastModified = file.LastModifiedUtc;
                    if (lastModified > fileTrackInfo.LastModifiedTime)
                    {
                        await AddMediaFile(file, infoChannel, ignoreDrm);
                    }
                    else
                    {
                        foreach (var item in fileTrackInfo.TrackIdentifiers)
                        {
                            await budget.YieldAsync();
                            trackIdentifierDict.Remove(item, out DbMediaFile _);
                        }
                    }
                }
                else
                {
                    await AddMediaFile(file, infoChannel, ignoreDrm);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (FileAccessErrors.IsAccessDenied(ex))
            {
                failedPaths.TryAdd(file.Path, 0);
                AppLog.Warning("Library.Scan", $"Skipping inaccessible music file {file.Path}.", ex);
            }
            catch (Exception ex)
            {
                failedPaths.TryAdd(file.Path, 0);
                exceptions.Add(new Tuple<string, Exception>(file.Path, ex));
            }
        }
    }

    public Task<Tuple<int, List<Tuple<string, Exception>>>> ScanAsync(IEnumerable<string> folders) =>
        ScanAsync(folders, CancellationToken.None);

    public async Task<Tuple<int, List<Tuple<string, Exception>>>> ScanAsync(
        IEnumerable<string> folders, CancellationToken cancellationToken, bool removeMissingFiles = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new ScanWorkBudget(platform, cancellationToken);
        HasChanges = false;
        bool ignoreDrm = AutoIgnoreDrmProtectedFiles;

        var fileChannel = Channel.CreateBounded<IFile>(new BoundedChannelOptions(1000)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
        var infoChannel = Channel.CreateBounded<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)>(new BoundedChannelOptions(64)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var removeChannel = Channel.CreateBounded<DbMediaFile>(new BoundedChannelOptions(1000)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var exceptions = new ConcurrentBag<Tuple<string, Exception>>();
        var failedPaths = new ConcurrentDictionary<string, byte>(pathComparer);

        var metadata = new List<MediaMetadata>();
        IndexItemAdded?.Invoke(this, m_scannedCount);

        var excluded = PathExclusion.GetExcludedPath();

        var trackIdentifierDict = new ConcurrentDictionary<string, DbMediaFile>(pathComparer);
        var fileInfoDict = new Dictionary<string, FileTrackInfo>(pathComparer);
        foreach (var file in await m_database.GetAllMediaFilesAsync().ConfigureAwait(false))
        {
            await budget.YieldAsync();
            var id = TrackIdentifier(file);
            trackIdentifierDict.TryAdd(id, file);
            if (!fileInfoDict.TryGetValue(file.Path, out var fileItem))
            {
                fileInfoDict.Add(file.Path, fileItem = new FileTrackInfo(file.Path, file.FileLastModifiedDate));
            }
            fileItem.TrackIdentifiers.Add(id);
        }
        var dbTask = UpdateDbAsync(infoChannel, removeChannel, exceptions, cancellationToken);

        var s = new Stack<string>(folders);
        var workerCount = Math.Clamp(platform.OptimalIoThreadCount, 1, 8);
        List<Task> fileScanTasks = new();
        for (var i = 0; i < workerCount; i++)
        {
            fileScanTasks.Add(ParallelMediaScanWorker(
                fileChannel,
                trackIdentifierDict,
                fileInfoDict,
                infoChannel,
                exceptions,
                failedPaths,
                ignoreDrm,
                cancellationToken));
        }

        string current = null;
        var enumerationComplete = true;
        while (s.Count != 0)
        {
            current = s.Pop();
            try
            {
                await budget.YieldAsync();
                var subDirectories = await platform.ListFoldersFullNameInPathAsync(current, cancellationToken);
                foreach (var dir in subDirectories)
                {
                    await budget.YieldAsync();
                    s.Push(dir);
                }

                var fileList = (await platform.ListFilesFullNameInPathAsync(current, cancellationToken)).ToList();
                var cueFiles = fileList.Where(f => Path.GetExtension(f.Path).ToLower() == ".cue").ToList();
                foreach (var cue in cueFiles)
                {
                    try
                    {
                        await budget.YieldAsync();
                        using var cueFile = await openMediaFileForScan(cue.Path);
                        var idx = await CueFile.CreateFromStreamAsync(cueFile, cancellationToken);

                        if (string.IsNullOrWhiteSpace(idx.FileName) ||
                            idx.Indices.Count == 0)
                        {
                            throw new Exception($"{Strings.Resources.CueInvalid}: {cue.Name}");
                        }

                        var audioTrack = (from f
                                          in fileList
                                          where string.Compare(idx.FileName, f.Name, true) == 0
                                          select f).FirstOrDefault();

                        if (audioTrack == null)
                        {
                            var referencedPath = await platform.ResolveFileRelativeToAsync(cue.Path, idx.FileName, cancellationToken);
                            if (referencedPath is not null)
                            {
                                using var referencedFile = await openMediaFileForScan(referencedPath);
                            }
                            throw new Exception($"{Strings.Resources.AudioFileAccessFailure}: {idx.FileName}");
                        }

                        var audioTrackInfo = await platform.GetMediaInfoAsync(audioTrack.Path, cancellationToken);
                        if (audioTrackInfo == null)
                        {
                            throw new Exception($"{Strings.Resources.AudioFileMetadataFailure}: {idx.FileName}");
                        }

                        var totalDuration = audioTrackInfo.Duration;
                        foreach (var track in idx.Indices)
                        {
                            await budget.YieldAsync();
                            if (track.StartTime + track.Duration > totalDuration)
                            {
                                continue;
                            }

                            if (track.Duration == TimeSpan.Zero)
                            {
                                track.TrackInfo.Duration
                                    = track.Duration
                                    = totalDuration - track.StartTime;
                            }

                            var fileIdentifier = TrackIdentifier(
                                audioTrack.Path,
                                (int)track.StartTime.TotalMilliseconds,
                                track.Duration);
                            if (trackIdentifierDict.TryGetValue(fileIdentifier, out DbMediaFile f))
                            {
                                if (cue.LastModifiedUtc > f.FileLastModifiedDate)
                                {
                                    await infoChannel.Writer.WriteAsync((audioTrack.Path, track.TrackInfo, cue.LastModifiedUtc, track), cancellationToken);
                                }
                                else
                                {
                                    trackIdentifierDict.Remove(fileIdentifier, out _);
                                }
                            }
                            else
                            {
                                await infoChannel.Writer.WriteAsync((audioTrack.Path, track.TrackInfo, cue.LastModifiedUtc, track), cancellationToken);
                            }
                        }

                        fileList.Remove(audioTrack);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (FileAccessErrors.IsAccessDenied(ex))
                    {
                        enumerationComplete = false;
                        AppLog.Warning("Library.Scan", $"Skipping inaccessible CUE media {cue.Path}.", ex);
                    }
                    catch (Exception ex)
                    {
                        enumerationComplete = false;
                        exceptions.Add(new Tuple<string, Exception>(cue.Path, ex));
                    }
                }

                foreach (var file in fileList)
                {
                    await budget.YieldAsync();
                    if (excluded.Any(path => file.Path.IsSubPathOf(path)) || !SupportedFormats.Contains(Path.GetExtension(file.Path)))
                        continue;
                    await fileChannel.Writer.WriteAsync(file, cancellationToken);
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
                AppLog.Warning("Library.Scan", $"Skipping inaccessible music folder {current}.", ex);
            }
            catch (Exception ex)
            {
                enumerationComplete = false;
                exceptions.Add(new Tuple<string, Exception>(current, ex));
            }
        }

        fileChannel.Writer.Complete();
        await Task.WhenAll(fileScanTasks);
        infoChannel.Writer.Complete();

        foreach (var file in enumerationComplete && removeMissingFiles && !cancellationToken.IsCancellationRequested
            ? trackIdentifierDict.Where(item => !failedPaths.ContainsKey(item.Value.Path))
            : [])
        {
            await removeChannel.Writer.WriteAsync(file.Value);
        }

        removeChannel.Writer.Complete();
        var scannedCount = -1;
        try
        {
            scannedCount = await dbTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            exceptions.Add(new Tuple<string, Exception>(DatabaseConstants.DbFileName, ex));
        }

        cancellationToken.ThrowIfCancellationRequested();
        platform.SetConfigurationValue("LastAutoRefereshTime", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
        return new Tuple<int, List<Tuple<string, Exception>>>(scannedCount, exceptions.ToList());
    }

    private static StringComparer DefaultPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private string TrackIdentifier(DbMediaFile file) => caseSensitivePaths
        ? TrackIdentifier(file.Path, file.StartTime, file.Duration)
        : file.ToString();

    private string TrackIdentifier(string path, int? startTime, TimeSpan duration)
    {
        var identityPath = caseSensitivePaths ? path : path.ToLower();
        return startTime.HasValue
            ? $"{identityPath}|{startTime.Value}|{duration.TotalMilliseconds}"
            : identityPath;
    }
}
