using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LocalServices;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>The Music app library (local, unprotected songs) and the Photos library (locally stored photos and videos).</summary>
internal sealed class MacMediaLibraryProvider : INativeMediaLibraryProvider, ISystemLibraryCatalog, IDisposable
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const long MusicCheckIntervalMs = 60_000;
    private static MacMediaLibraryProvider? current;

    private readonly SystemLibraryCatalogCache<SystemLibraryTrack> music;
    private readonly SystemLibraryCatalogCache<IndexedMediaFile> media;
    private readonly SystemLibraryQueryService query;
    private readonly IMultiMediaPlatformIO platform;
    private readonly Timer notifyTimer;
    private readonly object musicGate = new();
    private IAppSettingsStore? settings;
    private Task? musicOpening;
    private long photoStatus;
    private long lastMusicCheck = Environment.TickCount64;
    private int musicSignature;
    private int photoAccessRequested;
    private int checkingMusic;
    private IntPtr activationObserver;
    private bool disposed;

    public MacMediaLibraryProvider(IMultiMediaPlatformIO platform)
    {
        this.platform = platform;
        music = new SystemLibraryCatalogCache<SystemLibraryTrack>(ReadMusic);
        media = new SystemLibraryCatalogCache<IndexedMediaFile>(ReadMedia);
        query = new SystemLibraryQueryService(this);
        notifyTimer = new Timer(_ => CatalogChanged?.Invoke(this, EventArgs.Empty));
        current = this;
        photoStatus = MacPhotoLibrary.AuthorizationStatus;
        MacPhotoLibrary.Observe(OnPhotosChanged);
        ObserveActivation();
    }

    public bool SupportsMusic => true;

    public bool SupportsPhotosAndVideos => true;

    public event EventHandler? CatalogChanged;

    public ILibrarySource CreateSource(IAppSettingsStore settingsStore, IPhotoSearchService? photoSearch)
    {
        settings = settingsStore;
        return new SystemLibrarySource(settingsStore, new LibrarySourceServices(
            query, query, platform, null, new SystemLibraryRefreshService(Refresh), query,
            PhotoSearch: photoSearch, MusicFolders: new IndexedMusicFolderBrowser(query, query.GetMusicRootsAsync)),
            new LibrarySourceCapabilities(SupportsFileReveal: false), () => disposed);
    }

    // The first read opens the library without waiting; access prompts and the library load refresh the catalog when done.
    public Task<SystemLibrarySnapshot<SystemLibraryTrack>> GetMusicAsync(CancellationToken cancellationToken = default)
    {
        if (!MacMusicLibrary.IsOpen)
            _ = OpenMusicAsync();
        return music.GetAsync(cancellationToken);
    }

    public Task<SystemLibrarySnapshot<IndexedMediaFile>> GetMediaAsync(CancellationToken cancellationToken = default)
    {
        if (MacPhotoLibrary.IsUndetermined && Interlocked.Exchange(ref photoAccessRequested, 1) == 0)
            _ = RequestPhotoAccessAsync();
        return media.GetAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DbImageFile>> GetPhotosAsync(CancellationToken cancellationToken = default) =>
        (await GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(item => item.Image).OfType<DbImageFile>().ToArray();

    public Task<byte[]?> GetThumbnailAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => MacPhotoLibrary.WithAsset(path, MacPhotoLibrary.Thumbnail), cancellationToken);

    public async Task<bool> RequestAccessAsync(LibraryMediaCategory category, bool manageAccess = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool granted;
        if (category == LibraryMediaCategory.Music)
        {
            granted = MacMusicLibrary.IsOpen || await Task.Run(MacMusicLibrary.Open, cancellationToken).ConfigureAwait(false);
            if (manageAccess)
                OpenPrivacySettings("Privacy_Media");
        }
        else
        {
            if (MacPhotoLibrary.IsUndetermined)
                await RequestPhotoAccessAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            granted = MacPhotoLibrary.IsAuthorized;
            if (manageAccess)
                OpenPrivacySettings("Privacy_Photos");
        }

        Refresh();
        return granted;
    }

    public void Refresh()
    {
        if (disposed)
            return;
        MacMusicLibrary.Invalidate();
        Interlocked.Exchange(ref photoStatus, MacPhotoLibrary.AuthorizationStatus);
        MacPhotoLibrary.Observe(OnPhotosChanged);
        music.Invalidate();
        media.Invalidate();
        query.Invalidate();
        notifyTimer.Change(0, Timeout.Infinite);
    }

    private Task OpenMusicAsync()
    {
        lock (musicGate)
        {
            return musicOpening ??= Task.Run(() =>
            {
                if (MacMusicLibrary.Open())
                    Refresh();
            });
        }
    }

    private async Task RequestPhotoAccessAsync()
    {
        await MacPhotoLibrary.RequestAuthorizationAsync().ConfigureAwait(false);
        Refresh();
    }

    private void OnPhotosChanged()
    {
        if (disposed)
            return;
        media.Invalidate();
        query.Invalidate();
        notifyTimer.Change(250, Timeout.Infinite);
    }

    // Access granted in System Settings and Music app changes are picked up when this app becomes active.
    private void OnActivated()
    {
        if (disposed)
            return;
        var status = (long)MacPhotoLibrary.AuthorizationStatus;
        if (Interlocked.Exchange(ref photoStatus, status) != status)
            Refresh();
        if (settings?.NativeLibrary.UseSystemMusicLibrary != true ||
            Environment.TickCount64 - Volatile.Read(ref lastMusicCheck) < MusicCheckIntervalMs ||
            Interlocked.Exchange(ref checkingMusic, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref lastMusicCheck, Environment.TickCount64);
        _ = Task.Run(() =>
        {
            try
            {
                if (!MacMusicLibrary.IsOpen)
                {
                    if (MacMusicLibrary.Open())
                        Refresh();
                    return;
                }

                MacMusicLibrary.Invalidate();
                if (Signature(MacMusicLibrary.Read(CancellationToken.None)) == Volatile.Read(ref musicSignature))
                    return;
                music.Invalidate();
                query.Invalidate();
                notifyTimer.Change(0, Timeout.Infinite);
            }
            catch (Exception exception)
            {
                AppLog.Warning("macOS.MediaLibrary", "Could not check the Music library for changes.", exception);
            }
            finally
            {
                Volatile.Write(ref checkingMusic, 0);
            }
        });
    }

    private SystemLibraryTrack[] ReadMusic(CancellationToken cancellationToken)
    {
        var tracks = MacMusicLibrary.Read(cancellationToken);
        Volatile.Write(ref musicSignature, Signature(tracks));
        Volatile.Write(ref lastMusicCheck, Environment.TickCount64);
        return tracks;
    }

    private static int Signature(SystemLibraryTrack[] tracks)
    {
        var hash = new HashCode();
        foreach (var track in tracks)
        {
            hash.Add(track.File.Path);
            hash.Add(track.File.FileLastModifiedDate);
        }

        return hash.ToHashCode();
    }

    private static IndexedMediaFile[] ReadMedia(CancellationToken cancellationToken)
    {
        if (!MacPhotoLibrary.IsAuthorized)
            return [];
        var folder = MacPhotoLibrary.LibraryFolderName() is { Length: > 0 } title
            ? title.Replace('/', ' ').Replace('\\', ' ')
            : "Photos";
        var items = new List<IndexedMediaFile>();
        MacPhotoLibrary.ReadAssets((asset, resource) =>
        {
            var path = MacPhotoLibrary.Path(asset, resource);
            var creation = MacPhotoLibrary.CreationDate(asset);
            var created = creation ?? DateTimeOffset.UnixEpoch;
            var modified = MacPhotoLibrary.ModificationDate(asset) ?? created;
            var width = MacPhotoLibrary.PixelWidth(asset);
            var height = MacPhotoLibrary.PixelHeight(asset);
            if (!MacPhotoLibrary.IsVideo(asset))
            {
                items.Add(new IndexedMediaFile(Image: new DbImageFile
                {
                    Id = items.Count + 1,
                    Path = path,
                    FileName = resource.FileName,
                    FolderPath = folder,
                    LibraryRoot = folder,
                    FileSize = resource.Size,
                    Width = width,
                    Height = height,
                    DateTaken = creation?.ToLocalTime(),
                    FileCreatedDate = created,
                    FileLastModifiedDate = modified,
                    DatabaseItemAddedDate = created,
                }));
                return;
            }

            var duration = TimeSpan.FromSeconds(Math.Max(0, MacPhotoLibrary.Duration(asset)));
            items.Add(new IndexedMediaFile(Video: new DbVideoFile
            {
                Id = items.Count + 1,
                Path = path,
                FileName = resource.FileName,
                Title = Path.GetFileNameWithoutExtension(resource.FileName),
                FolderPath = folder,
                LibraryRoot = folder,
                FileSize = resource.Size,
                Width = width,
                Height = height,
                Duration = duration,
                BitRate = duration.TotalSeconds > 0 ? (long)(resource.Size * 8.0 / duration.TotalSeconds) : 0,
                FileCreatedDate = created,
                FileLastModifiedDate = modified,
                DatabaseItemAddedDate = created,
            }));
        }, cancellationToken);
        return items.ToArray();
    }

    private static void OpenPrivacySettings(string pane)
    {
        try
        {
            Process.Start(new ProcessStartInfo("open", $"x-apple.systempreferences:com.apple.preference.security?{pane}")
            {
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Warning("macOS.MediaLibrary", "Could not open the privacy settings.", exception);
        }
    }

    private unsafe void ObserveActivation()
    {
        var cls = ObjC.GetClass("LPMediaLibraryActivation");
        if (cls == IntPtr.Zero)
        {
            cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "LPMediaLibraryActivation");
            ObjC.AddMethod(cls, ObjC.Sel("applicationDidBecomeActive:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnApplicationActivated, "v@:@");
            ObjC.RegisterClassPair(cls);
        }

        activationObserver = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        var name = ObjC.ReadStringConstant(ObjC.LoadLibrary(AppKit), "NSApplicationDidBecomeActiveNotification");
        ObjC.Send(ObjC.Send(ObjC.GetClass("NSNotificationCenter"), ObjC.Sel("defaultCenter")),
            ObjC.Sel("addObserver:selector:name:object:"), activationObserver, ObjC.Sel("applicationDidBecomeActive:"), name,
            IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnApplicationActivated(IntPtr self, IntPtr selector, IntPtr notification)
    {
        try
        {
            current?.OnActivated();
        }
        catch (Exception exception)
        {
            AppLog.Error("macOS.MediaLibrary", "Could not handle app activation.", exception);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        current = null;
        MacPhotoLibrary.StopObserving();
        if (activationObserver != IntPtr.Zero)
        {
            ObjC.Send(ObjC.Send(ObjC.GetClass("NSNotificationCenter"), ObjC.Sel("defaultCenter")),
                ObjC.Sel("removeObserver:"), activationObserver);
            ObjC.Send(activationObserver, ObjC.Sel("release"));
            activationObserver = IntPtr.Zero;
        }

        notifyTimer.Dispose();
    }
}

/// <summary>Reads Photos library assets for <see cref="SystemLibraryPlatformIO"/>.</summary>
internal sealed class MacPhotoLibraryReader : ISystemMediaReader
{
    public bool Owns(string path) => path.StartsWith(MacPhotoLibrary.Scheme, StringComparison.Ordinal);

    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var asset = await Task.Run(() => MacPhotoLibrary.WithAsset(path, item => ObjC.Send(item, ObjC.Sel("retain"))),
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!MacPhotoLibrary.IsVideo(asset))
                return await Task.Run(() => MacPhotoLibrary.OpenPhoto(asset, path), cancellationToken).ConfigureAwait(false);

            // Edits that Photos has not rendered to a file, such as slow motion, arrive as compositions; the original is read instead.
            var file = await MacPhotoLibrary.RequestVideoPathAsync(asset, original: false, path)
                    .WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? await MacPhotoLibrary.RequestVideoPathAsync(asset, original: true, path)
                    .WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"The photo library did not provide a file for {path}.");
            try
            {
                return File.OpenRead(file);
            }
            catch (UnauthorizedAccessException exception)
            {
                AppLog.Warning("macOS.MediaLibrary", $"Copying {path} because its library file is not readable.", exception);
                return await ExportVideoAsync(asset, path, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ObjC.Send(asset, ObjC.Sel("release"));
        }
    }

    public Task<MediaFileProperties?> GetPropertiesAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => MacPhotoLibrary.WithAsset(path, asset =>
        {
            if (MacPhotoLibrary.DescribeLocalResource(asset) is not { } resource)
                return null;
            var modified = MacPhotoLibrary.ModificationDate(asset) ?? MacPhotoLibrary.CreationDate(asset);
            return new MediaFileProperties(resource.FileName, path, null, resource.Size > 0 ? resource.Size : -1,
                modified?.UtcDateTime ?? DateTime.UnixEpoch);
        }), cancellationToken);

    public Task<byte[]?> GetArtworkAsync(string path, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    private static async Task<Stream> ExportVideoAsync(IntPtr asset, string path, CancellationToken cancellationToken)
    {
        var pool = ObjC.PushAutoreleasePool();
        PhotoLibraryResource resource;
        try
        {
            resource = MacPhotoLibrary.DescribeLocalResource(asset) ?? throw new IOException($"{path} is not stored on this Mac.");
            ObjC.Send(resource.Resource, ObjC.Sel("retain"));
        }
        finally
        {
            ObjC.PopAutoreleasePool(pool);
        }

        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(resource.FileName));
        try
        {
            await MacPhotoLibrary.ExportAsync(resource, file).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        }
        catch
        {
            File.Delete(file);
            throw;
        }
        finally
        {
            ObjC.Send(resource.Resource, ObjC.Sel("release"));
        }
    }
}
