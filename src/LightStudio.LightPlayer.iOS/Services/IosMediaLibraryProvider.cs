using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Foundation;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using MediaPlayer;
using Photos;
using PhotosUI;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>The iOS media library (local, unprotected songs) and photo library (locally stored photos and videos).</summary>
internal sealed class IosMediaLibraryProvider : INativeMediaLibraryProvider, ISystemLibraryCatalog, IDisposable
{
    internal const string PhotoScheme = "photos-library://asset/";
    internal const string MusicScheme = "ipod-library://";

    private readonly SystemLibraryCatalogCache<SystemLibraryTrack> music;
    private readonly SystemLibraryCatalogCache<IndexedMediaFile> media;
    private readonly SystemLibraryQueryService query;
    private readonly IMultiMediaPlatformIO platform;
    private readonly Timer notifyTimer;
    private readonly NSObject foregroundObserver;
    private readonly object observerGate = new();
    private NSObject? musicObserver;
    private PhotoLibraryObserver? photoObserver;
    private int musicAccessRequested;
    private int photoAccessRequested;
    private bool disposed;

    public IosMediaLibraryProvider(IMultiMediaPlatformIO platform)
    {
        this.platform = platform;
        music = new SystemLibraryCatalogCache<SystemLibraryTrack>(ReadMusic);
        media = new SystemLibraryCatalogCache<IndexedMediaFile>(ReadMedia);
        query = new SystemLibraryQueryService(this);
        notifyTimer = new Timer(_ => CatalogChanged?.Invoke(this, EventArgs.Empty));
        foregroundObserver = UIApplication.Notifications.ObserveWillEnterForeground((_, _) => Refresh());
        ObserveLibraries();
    }

    public bool SupportsMusic => true;

    public bool SupportsPhotosAndVideos => true;

    public event EventHandler? CatalogChanged;

    public ILibrarySource CreateSource(IAppSettingsStore settingsStore, IPhotoSearchService? photoSearch) =>
        new SystemLibrarySource(settingsStore, new LibrarySourceServices(
            query, query, platform, null, new SystemLibraryRefreshService(Refresh), query, PhotoSearch: photoSearch),
            new LibrarySourceCapabilities(SupportsFileReveal: false), () => disposed);

    // The first read asks for access without waiting; answering the prompt refreshes the catalog.
    public Task<SystemLibrarySnapshot<SystemLibraryTrack>> GetMusicAsync(CancellationToken cancellationToken = default)
    {
        if (MPMediaLibrary.AuthorizationStatus == MPMediaLibraryAuthorizationStatus.NotDetermined &&
            Interlocked.Exchange(ref musicAccessRequested, 1) == 0)
        {
            _ = RequestMusicAuthorizationAsync();
        }

        return music.GetAsync(cancellationToken);
    }

    public Task<SystemLibrarySnapshot<IndexedMediaFile>> GetMediaAsync(CancellationToken cancellationToken = default)
    {
        if (PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) == PHAuthorizationStatus.NotDetermined &&
            Interlocked.Exchange(ref photoAccessRequested, 1) == 0)
        {
            _ = RequestPhotoAuthorizationAsync();
        }

        return media.GetAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DbImageFile>> GetPhotosAsync(CancellationToken cancellationToken = default) =>
        (await GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(item => item.Image).OfType<DbImageFile>().ToArray();

    public Task<byte[]?> GetThumbnailAsync(string path, CancellationToken cancellationToken) =>
        IosMediaLibraryReader.GetPhotoThumbnailAsync(path, cancellationToken);

    public async Task<bool> RequestAccessAsync(LibraryMediaCategory category, bool manageAccess = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool granted;
        if (category == LibraryMediaCategory.Music)
        {
            var status = MPMediaLibrary.AuthorizationStatus;
            if (status == MPMediaLibraryAuthorizationStatus.NotDetermined)
                status = await RequestMusicAuthorizationAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            granted = status == MPMediaLibraryAuthorizationStatus.Authorized;
            if (manageAccess)
                await OpenSettingsAsync().ConfigureAwait(false);
        }
        else
        {
            var status = PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite);
            if (status == PHAuthorizationStatus.NotDetermined)
                status = await RequestPhotoAuthorizationAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            granted = status is PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited;
            if (manageAccess && status == PHAuthorizationStatus.Limited)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PHPhotoLibrary.SharedPhotoLibrary
                    .PresentLimitedLibraryPickerAsync(IosStorage.GetViewController()));
            }
            else if (manageAccess)
            {
                await OpenSettingsAsync().ConfigureAwait(false);
            }
        }

        Refresh();
        return granted;
    }

    public void Refresh()
    {
        if (disposed)
            return;
        ObserveLibraries();
        Invalidate();
        notifyTimer.Change(0, Timeout.Infinite);
    }

    private void Invalidate()
    {
        music.Invalidate();
        media.Invalidate();
        query.Invalidate();
    }

    private void OnLibraryChanged()
    {
        if (disposed)
            return;
        Invalidate();
        notifyTimer.Change(250, Timeout.Infinite);
    }

    private void ObserveLibraries()
    {
        lock (observerGate)
        {
            if (musicObserver is null && MPMediaLibrary.AuthorizationStatus == MPMediaLibraryAuthorizationStatus.Authorized)
            {
                MPMediaLibrary.DefaultMediaLibrary.BeginGeneratingLibraryChangeNotifications();
                musicObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                    MPMediaLibrary.DidChangeNotification, _ => OnLibraryChanged());
            }

            if (photoObserver is null && PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) is
                PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited)
            {
                photoObserver = new PhotoLibraryObserver(OnLibraryChanged);
                PHPhotoLibrary.SharedPhotoLibrary.RegisterChangeObserver(photoObserver);
            }
        }
    }

    private Task<MPMediaLibraryAuthorizationStatus> RequestMusicAuthorizationAsync()
    {
        var completion = new TaskCompletionSource<MPMediaLibraryAuthorizationStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MPMediaLibrary.RequestAuthorization(status =>
        {
            completion.TrySetResult(status);
            Refresh();
        });
        return completion.Task;
    }

    private async Task<PHAuthorizationStatus> RequestPhotoAuthorizationAsync()
    {
        var status = await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.ReadWrite).ConfigureAwait(false);
        Refresh();
        return status;
    }

    private static Task OpenSettingsAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        UIApplication.SharedApplication.OpenUrl(new NSUrl(UIApplication.OpenSettingsUrlString),
            new UIApplicationOpenUrlOptions(), null)).GetTask();

    private static SystemLibraryTrack[] ReadMusic(CancellationToken cancellationToken)
    {
        if (MPMediaLibrary.AuthorizationStatus != MPMediaLibraryAuthorizationStatus.Authorized)
            return [];

        var tracks = new List<SystemLibraryTrack>();
        using var query = MPMediaQuery.SongsQuery;
        query.AddFilterPredicate(MPMediaPropertyPredicate.PredicateWithValue(
            NSNumber.FromBoolean(false), MPMediaItem.IsCloudItemProperty));
        foreach (var item in query.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (item)
            using (new NSAutoreleasePool())
            {
                if (item.AssetURL?.AbsoluteString is not { Length: > 0 } path || item.HasProtectedAsset || item.IsCloudItem)
                    continue;
                var artist = item.Artist ?? string.Empty;
                var albumArtist = item.AlbumArtist ?? string.Empty;
                var year = item.ReleaseDate is { } released
                    ? ((DateTime)released).Year
                    : (item.ValueForProperty(new NSString("year")) as NSNumber)?.Int32Value ?? 0;
                var added = Timestamp(item.DateAdded);
                tracks.Add(new SystemLibraryTrack(new DbMediaFile
                {
                    Id = tracks.Count + 1,
                    Path = path,
                    Title = item.Title is { Length: > 0 } title ? title : Path.GetFileName(path),
                    Artist = artist,
                    Album = item.AlbumTitle ?? string.Empty,
                    AlbumArtist = albumArtist.Length > 0 ? albumArtist : artist,
                    AlbumGroupingKey = "ipod:" + item.AlbumPersistentID.ToString(CultureInfo.InvariantCulture),
                    Genre = item.Genre ?? string.Empty,
                    Composer = item.Composer ?? string.Empty,
                    Date = Positive(year),
                    DiscNumber = Positive(item.DiscNumber),
                    TrackNumber = Positive(item.AlbumTrackNumber),
                    TotalDiscs = Positive(item.DiscCount),
                    TotalTracks = Positive(item.AlbumTrackCount),
                    Description = string.Empty,
                    Copyright = string.Empty,
                    Comments = item.Comments ?? string.Empty,
                    Grouping = item.UserGrouping ?? string.Empty,
                    Performer = string.Empty,
                    Duration = TimeSpan.FromSeconds(Math.Max(0, item.PlaybackDuration)),
                    DatabaseItemAddedDate = added,
                    FileLastModifiedDate = added,
                }, string.Empty, string.Empty));
            }
        }

        return tracks.ToArray();
    }

    private static IndexedMediaFile[] ReadMedia(CancellationToken cancellationToken)
    {
        if (PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) is not
            (PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited))
        {
            return [];
        }

        var folder = LibraryFolderName();
        var items = new List<IndexedMediaFile>();
        using var options = new PHFetchOptions
        {
            IncludeAssetSourceTypes = PHAssetSourceType.UserLibrary | PHAssetSourceType.iTunesSynced,
        };
        using var assets = PHAsset.FetchAssets(options);
        var skipped = 0;
        for (nint index = 0; index < assets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pool = new NSAutoreleasePool();
            if (assets[index] is not PHAsset asset)
                continue;
            using (asset)
            {
                if (asset.MediaType is not (PHAssetMediaType.Image or PHAssetMediaType.Video))
                    continue;
                if (IosMediaLibraryReader.DescribeLocalResource(asset) is not { } resource)
                {
                    skipped++;
                    continue;
                }

                var path = PhotoScheme + Uri.EscapeDataString(asset.LocalIdentifier) + "/" + Uri.EscapeDataString(resource.FileName);
                var created = Timestamp(asset.CreationDate);
                var modified = asset.ModificationDate is { } date ? Timestamp(date) : created;
                if (asset.MediaType == PHAssetMediaType.Image)
                {
                    items.Add(new IndexedMediaFile(Image: new DbImageFile
                    {
                        Id = items.Count + 1,
                        Path = path,
                        FileName = resource.FileName,
                        FolderPath = folder,
                        LibraryRoot = folder,
                        FileSize = resource.Size,
                        Width = (int)asset.PixelWidth,
                        Height = (int)asset.PixelHeight,
                        DateTaken = asset.CreationDate is null ? null : created.ToLocalTime(),
                        FileCreatedDate = created,
                        FileLastModifiedDate = modified,
                        DatabaseItemAddedDate = created,
                    }));
                }
                else
                {
                    var duration = TimeSpan.FromSeconds(Math.Max(0, asset.Duration));
                    items.Add(new IndexedMediaFile(Video: new DbVideoFile
                    {
                        Id = items.Count + 1,
                        Path = path,
                        FileName = resource.FileName,
                        Title = Path.GetFileNameWithoutExtension(resource.FileName),
                        FolderPath = folder,
                        LibraryRoot = folder,
                        FileSize = resource.Size,
                        Width = (int)asset.PixelWidth,
                        Height = (int)asset.PixelHeight,
                        Duration = duration,
                        BitRate = duration.TotalSeconds > 0 ? (long)(resource.Size * 8.0 / duration.TotalSeconds) : 0,
                        FileCreatedDate = created,
                        FileLastModifiedDate = modified,
                        DatabaseItemAddedDate = created,
                    }));
                }
            }
        }

        if (skipped > 0)
            AppLog.Information("iOS.MediaLibrary", $"Skipped {skipped} photo library items that are not stored on this device.");
        return items.ToArray();
    }

    // The system-localized name of the photo library's all-items album, used as its only folder.
    private static string LibraryFolderName()
    {
        using var collections = PHAssetCollection.FetchAssetCollections(
            PHAssetCollectionType.SmartAlbum, PHAssetCollectionSubtype.SmartAlbumUserLibrary, null);
        var title = collections.FirstObject is PHAssetCollection collection ? collection.LocalizedTitle : null;
        return string.IsNullOrWhiteSpace(title) ? "Photos" : title.Replace('/', ' ').Replace('\\', ' ');
    }

    private static string Positive(nint value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static DateTimeOffset Timestamp(NSDate? date) => date is null
        ? DateTimeOffset.UnixEpoch
        : DateTimeOffset.FromUnixTimeMilliseconds((long)(date.SecondsSince1970 * 1000));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foregroundObserver.Dispose();
        notifyTimer.Dispose();
        lock (observerGate)
        {
            if (musicObserver is not null)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(musicObserver);
                MPMediaLibrary.DefaultMediaLibrary.EndGeneratingLibraryChangeNotifications();
            }

            if (photoObserver is not null)
            {
                PHPhotoLibrary.SharedPhotoLibrary.UnregisterChangeObserver(photoObserver);
                photoObserver.Dispose();
            }
        }
    }

    private sealed class PhotoLibraryObserver : PHPhotoLibraryChangeObserver
    {
        private readonly Action changed;

        public PhotoLibraryObserver(Action changed) => this.changed = changed;

        public override void PhotoLibraryDidChange(PHChange changeInstance) => changed();
    }
}
