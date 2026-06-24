using LightStudio.MediaLibraryCore.CueIndex;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Library;

//Tuple<IMediaInfo, string>
//Item1: Media information
//Item2: File path
using MediaMetadata = Tuple<MediaInfo, string, DateTimeOffset>;

public class FileIndexer
{
    private readonly MediaLibraryDbContext m_dbContext;
    private int m_scannedCount = 0;

    /// <summary>
    /// Class constructor that creates instance of <see cref="FileIndexer"/>.
    /// </summary>
    /// <param name="dbContext"></param>
    public FileIndexer(MediaLibraryDbContext dbContext)
    {
        m_dbContext = dbContext;
    }

    bool AutoIgnoreDrmProtectedFiles
    {
        get { return IMultiMediaPlatformIO.Instance.GetConfigurationValue<bool>("AutoIgnoreDrmProtectedFiles"); }
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

    private int ParseWithFallback(string s)
    {
        if (int.TryParse(s, out int result))
        {
            return result;
        }
        return 0;
    }

    private async void UpdateDb(
        ChannelReader<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)> infoReader,
        ChannelReader<DbMediaFile> removeReader,
        ConcurrentBag<Tuple<string, Exception>> exceptions,
        IThumbnailOperations thumbnail,
        TaskCompletionSource<int> taskCompletionSource,
        bool incremental = false)
    {
        var send = new List<DbMediaFile>(30);
        await foreach (var (path, info, date, cue) in infoReader.ReadAllAsync())
        {
            try
            {
                var dbFile = info.ToDbMediaFile(date);
                if (string.IsNullOrWhiteSpace(info.Title))
                {
                    dbFile.Title = Path.GetFileName(path);
                }
                dbFile.Path = path;
                if (cue != null)
                {
                    dbFile.StartTime = (int)cue.StartTime.TotalMilliseconds;
                }

                if (string.IsNullOrWhiteSpace(info.Album)) info.Album = null;
                if (string.IsNullOrWhiteSpace(info.Artist)) info.Artist = null;
                if (string.IsNullOrWhiteSpace(info.AlbumArtist)) dbFile.AlbumArtist = null;

                send.Add(dbFile);
                await m_dbContext.MediaFiles.AddAsync(dbFile);
                m_scannedCount++;
                if (m_scannedCount % 30 == 0)
                {
                    NewItemAdded?.Invoke(this, send.ToArray());
                    send.Clear();
                    IndexItemAdded?.Invoke(this, m_scannedCount);
                    await m_dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(new Tuple<string, Exception>(path, ex));
            }
        }

        try
        {
            IndexItemAdded?.Invoke(this, m_scannedCount);
            await m_dbContext.SaveChangesAsync();

            if (incremental)
            {
                await foreach (var remove in removeReader.ReadAllAsync())
                {
                    m_dbContext.PlaybackHistory.RemoveRange(
                        m_dbContext.PlaybackHistory.Where(
                            history => history.RelatedMediaFile == remove));

                    m_dbContext.MediaFiles.Remove(remove);
                }
            }
        }
        catch { }

        var scanned = m_scannedCount;
        m_scannedCount = 0;
        await m_dbContext.SaveChangesAsync();
        taskCompletionSource.TrySetResult(scanned);
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
        bool ignoreDrm)
    {
        while (await input.WaitToReadAsync())
        {
            if (!input.TryRead(out var file)) continue;
            try
            {
                static async Task AddMediaFile(IFile file, ChannelWriter<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)> infoChannel, bool ignoreDrm)
                {
                    try
                    {
                        var date = file.LastModifiedUtc;
                        var info = await IMultiMediaPlatformIO.Instance.GetMediaInfoAsync(file.Path);
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
                            await infoChannel.WriteAsync((file.Path, info, date, null));
                        }
                        else
                        {
                            var totalDuration = info.Duration;
                            foreach (var track in idx.Indices)
                            {
                                if (track.StartTime + track.Duration > totalDuration)
                                {
                                    // Invalid track
                                    continue;
                                }

                                if (track.Duration == TimeSpan.Zero)
                                {
                                    track.TrackInfo.Duration
                                        = track.Duration
                                        = totalDuration - track.StartTime;
                                }

                                await infoChannel.WriteAsync((file.Path, track.TrackInfo, file.LastModifiedUtc, track));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Strings.Resources.AudioFileMetadataFailureExceptionMessage, ex);
                    }
                }

                // Check modified time per file.
                if (fileInfoDict.TryGetValue(file.Path, out var fileTrackInfo))
                {
                    var lastModified = file.LastModifiedUtc;
                    if (lastModified > fileTrackInfo.LastModifiedTime)
                    {
                        // Read info from the file again since it may have been modified.
                        await AddMediaFile(file, infoChannel, ignoreDrm);
                    }
                    else
                    {
                        // The file is not modified.
                        // Remove all identifiers from trackIdentifierDict
                        foreach (var item in fileTrackInfo.TrackIdentifiers)
                        {
                            trackIdentifierDict.Remove(item, out DbMediaFile _);
                        }
                    }
                }
                else
                {
                    await AddMediaFile(file, infoChannel, ignoreDrm);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(new Tuple<string, Exception>(file.Path, ex));
            }
        }
    }

    public async Task<Tuple<int, List<Tuple<string, Exception>>>> ScanAsync(
        IEnumerable<string> folders,
        IThumbnailOperations thumbnail)
    {
        bool ignoreDrm = AutoIgnoreDrmProtectedFiles;

        var fileChannel = Channel.CreateBounded<IFile>(new BoundedChannelOptions(1000)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
        var infoChannel = Channel.CreateBounded<(string, MediaInfo, DateTimeOffset, ManagedAudioIndexCue)>(new BoundedChannelOptions(1000)
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

        // Due to the usage of asynchronous operations, awaiting the database thread action to complete
        // is not a good idea. (https://docs.microsoft.com/en-us/windows/uwp/threading-async/best-practices-for-using-the-thread-pool)
        // We use a custom TaskCompletionSource to represent database worker thread's status.
        Task dbTask;
        TaskCompletionSource<int> dbCompletionSource = new TaskCompletionSource<int>();

        var metadata = new List<MediaMetadata>();
        IndexItemAdded?.Invoke(this, m_scannedCount);

        var excluded = PathExclusion.GetExcludedPath();

        var trackIdentifierDict = new ConcurrentDictionary<string, DbMediaFile>(StringComparer.OrdinalIgnoreCase);// await m_dbContext.MediaFiles.ToDictionaryAsync(x => x.ToString());
        var fileInfoDict = new Dictionary<string, FileTrackInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in m_dbContext.MediaFiles)
        {
            var id = file.ToString();
            trackIdentifierDict.TryAdd(id, file);
            if (!fileInfoDict.TryGetValue(file.Path, out var fileItem))
            {
                fileInfoDict.Add(file.Path, fileItem = new FileTrackInfo(file.Path, file.FileLastModifiedDate));
            }
            fileItem.TrackIdentifiers.Add(id);
        }
        dbTask = Task.Factory.StartNew(
            () => UpdateDb(infoChannel, removeChannel, exceptions, thumbnail, dbCompletionSource, true),
            TaskCreationOptions.LongRunning);

        var s = new Stack<string>(folders);
        var workerCount = IMultiMediaPlatformIO.Instance.OptimalIoThreadCount;
        List<Task> fileScanTasks = new();
        for (var i = 0; i < workerCount; i++)
        {
            fileScanTasks.Add(ParallelMediaScanWorker(fileChannel, trackIdentifierDict, fileInfoDict, infoChannel, exceptions, ignoreDrm));
        }

        string current = null;
        while (s.Count != 0)
        {
            current = s.Pop();
            try
            {
                var subDirectories = await IMultiMediaPlatformIO.Instance.ListFoldersFullNameInPathAsync(current);
                foreach (var dir in subDirectories)
                {
                    s.Push(dir);
                }

                var fileList = (await IMultiMediaPlatformIO.Instance.ListFilesFullNameInPathAsync(current)).ToList();
                var cueFiles = fileList.Where(f => Path.GetExtension(f.Path).ToLower() == ".cue").ToList();
                foreach (var cue in cueFiles)
                {
                    try
                    {
                        using var cueFile = await IMultiMediaPlatformIO.Instance.OpenMediaFileAsync(cue.Path);
                        var idx = await CueFile.CreateFromStreamAsync(cueFile);

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
                            throw new Exception($"{Strings.Resources.AudioFileAccessFailure}: {idx.FileName}");
                        }

                        var audioTrackInfo = await IMultiMediaPlatformIO.Instance.GetMediaInfoAsync(audioTrack.Path);
                        if (audioTrackInfo == null)
                        {
                            throw new Exception($"{Strings.Resources.AudioFileMetadataFailure}: {idx.FileName}");
                        }

                        var totalDuration = audioTrackInfo.Duration;
                        foreach (var track in idx.Indices)
                        {
                            if (track.StartTime + track.Duration > totalDuration)
                            {
                                // Invalid track
                                continue;
                            }

                            if (track.Duration == TimeSpan.Zero)
                            {
                                track.TrackInfo.Duration
                                    = track.Duration
                                    = totalDuration - track.StartTime;
                            }

                            var fileIdentifier = $"{audioTrack.Path}|{track.StartTime.TotalMilliseconds}|{track.Duration.TotalMilliseconds}";
                            if (trackIdentifierDict.TryGetValue(fileIdentifier, out DbMediaFile f))
                            {
                                if (cue.LastModifiedUtc > f.FileLastModifiedDate)
                                {
                                    await infoChannel.Writer.WriteAsync((audioTrack.Path, track.TrackInfo, cue.LastModifiedUtc, track));
                                }
                                else
                                {
                                    trackIdentifierDict.Remove(fileIdentifier, out _);
                                }
                            }
                            else
                            {
                                await infoChannel.Writer.WriteAsync((audioTrack.Path, track.TrackInfo, cue.LastModifiedUtc, track));
                            }
                        }

                        fileList.Remove(audioTrack);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(new Tuple<string, Exception>(cue.Path, ex));
                    }
                }

                var files = fileList
                    .Where(f => !excluded.Any(x => f.Path.IsSubPathOf(x)) &&
                    SupportedFormats.Contains(Path.GetExtension(f.Path)));
                foreach (var file in files) await fileChannel.Writer.WriteAsync(file);
            }
            catch { }
        }

        fileChannel.Writer.Complete();
        await Task.WhenAll(fileScanTasks);
        infoChannel.Writer.Complete();

        foreach (var file in trackIdentifierDict)
        {
            await removeChannel.Writer.WriteAsync(file.Value);
        }

        removeChannel.Writer.Complete();
        try
        {
            await dbCompletionSource.Task;
        }
        catch
        {
            // Ignore
        }

        IMultiMediaPlatformIO.Instance.SetConfigurationValue("LastAutoRefereshTime", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
        GlobalLibraryCache.Invalidate();

        return new Tuple<int, List<Tuple<string, Exception>>>((dbCompletionSource.Task.Status == TaskStatus.RanToCompletion) ?
            dbCompletionSource.Task.Result :
            -1, exceptions.ToList());
    }
}
