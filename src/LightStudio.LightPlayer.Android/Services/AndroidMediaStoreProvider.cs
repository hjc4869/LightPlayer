#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Avalonia.Threading;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;
using AndroidUri = Android.Net.Uri;

namespace LightStudio.LightPlayer.Android.Services;

internal sealed class AndroidMediaStoreProvider : INativeMediaLibraryProvider, IDisposable
{
    private readonly ContentResolver resolver;
    private readonly AndroidMultimediaPlatformIO platform;
    private readonly AndroidMediaStoreCatalog catalog;
    private readonly AndroidMediaStoreQueryService query;
    private readonly Handler handler = new(Looper.MainLooper!);
    private readonly MediaObserver observer;
    private readonly Action notifyChanged;
    private bool disposed;

    public AndroidMediaStoreProvider(Context context, AndroidMultimediaPlatformIO platform)
    {
        this.platform = platform;
        resolver = context.ContentResolver!;
        catalog = new AndroidMediaStoreCatalog(context);
        query = new AndroidMediaStoreQueryService(catalog);
        notifyChanged = () => CatalogChanged?.Invoke(this, EventArgs.Empty);
        observer = new MediaObserver(handler, OnMediaChanged);
        using var root = AndroidUri.Parse("content://media")!;
        resolver.RegisterContentObserver(root, true, observer);
    }

    public bool SupportsMusic => true;

    public bool SupportsPhotosAndVideos => true;

    public event EventHandler? CatalogChanged;

    public async Task<IReadOnlyList<DbImageFile>> GetPhotosAsync(CancellationToken cancellationToken = default) =>
        (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(item => item.Image).OfType<DbImageFile>().ToArray();

    public ILibrarySource CreateSource(IAppSettingsStore settingsStore, IPhotoSearchService? photoSearch) =>
        new SystemLibrarySource(this, settingsStore, new LibrarySourceServices(
            query, query, platform, null, new RefreshService(this), query,
            PhotoSearch: photoSearch, MusicFolders: new IndexedMusicFolderBrowser(query, query.GetMusicRootsAsync)));

    public async Task<bool> RequestAccessAsync(LibraryMediaCategory category, bool manageAccess = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var granted = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (MainActivity.Current is not { } activity)
            {
                return false;
            }

            var hasAccess = category == LibraryMediaCategory.Music ? catalog.HasAudioAccess : catalog.HasImageAccess || catalog.HasVideoAccess;
            if (hasAccess && !manageAccess)
                return true;
            var fullVisualAccess = activity.CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaImages) == Permission.Granted &&
                activity.CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaVideo) == Permission.Granted;
            if (manageAccess && (category == LibraryMediaCategory.Music || fullVisualAccess))
            {
                using var packageUri = AndroidUri.Parse("package:" + activity.PackageName)!;
                using var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings, packageUri);
                activity.StartActivity(intent);
                return hasAccess;
            }

            string[] permissions = category == LibraryMediaCategory.Music
                ? [global::Android.Manifest.Permission.ReadMediaAudio]
                : [global::Android.Manifest.Permission.ReadMediaImages, global::Android.Manifest.Permission.ReadMediaVideo,
                    global::Android.Manifest.Permission.ReadMediaVisualUserSelected];
            await activity.RequestLibraryPermissionsAsync(permissions, cancellationToken, forceRequest: manageAccess);
            return category == LibraryMediaCategory.Music ? catalog.HasAudioAccess : catalog.HasImageAccess || catalog.HasVideoAccess;
        });
        Refresh();
        return granted;
    }

    public void Refresh()
    {
        if (disposed)
        {
            return;
        }

        catalog.Invalidate();
        query.Invalidate();
        handler.RemoveCallbacks(notifyChanged);
        notifyChanged();
    }

    private void OnMediaChanged()
    {
        if (disposed)
        {
            return;
        }

        catalog.Invalidate();
        query.Invalidate();
        handler.RemoveCallbacks(notifyChanged);
        handler.PostDelayed(notifyChanged, 250);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        resolver.UnregisterContentObserver(observer);
        handler.RemoveCallbacks(notifyChanged);
        observer.Dispose();
        handler.Dispose();
    }

    private sealed class MediaObserver(Handler handler, Action changed) : ContentObserver(handler)
    {
        public override void OnChange(bool selfChange) => changed();
    }

    private sealed class SystemLibrarySource(
        AndroidMediaStoreProvider provider, IAppSettingsStore settings, LibrarySourceServices services) : ILibrarySource
    {
        public LibrarySourceId Id => LibrarySourceId.System;
        public string DisplayName => Strings.Resources.LibrarySource_SystemLibrary;
        public bool LoadMusic => settings.NativeLibrary.UseSystemMusicLibrary;
        public bool LoadVideo => settings.NativeLibrary.UseSystemMediaLibrary;
        public LibrarySourceStatus Status => LibrarySourceStatus.Online;
        public string? LastError => null;
        public LibrarySourceCapabilities Capabilities { get; } = new(SupportsFileReveal: true);
        public LibrarySourceServices Services => services;
        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<LibrarySourceServices> EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(provider.disposed, provider);
            return Task.FromResult(services);
        }

        public void Dispose() { }
    }

    private sealed class RefreshService(AndroidMediaStoreProvider provider) : ILibraryScanService
    {
        public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted { add { } remove { } }
        public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged { add { } remove { } }
        public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning { add { } remove { } }
        public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted { add { } remove { } }

        public Task<LibraryScanResult> ScanAsync()
        {
            provider.Refresh();
            return Task.FromResult(new LibraryScanResult(0, []));
        }
    }
}
