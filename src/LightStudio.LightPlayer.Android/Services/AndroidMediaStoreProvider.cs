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
    private readonly SystemLibraryQueryService query;
    private readonly Handler handler = new(Looper.MainLooper!);
    private readonly MediaObserver observer;
    private readonly Action notifyChanged;
    private bool disposed;

    public AndroidMediaStoreProvider(Context context, AndroidMultimediaPlatformIO platform)
    {
        this.platform = platform;
        resolver = context.ContentResolver!;
        catalog = new AndroidMediaStoreCatalog(context);
        query = new SystemLibraryQueryService(catalog);
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
        new SystemLibrarySource(settingsStore, new LibrarySourceServices(
            query, query, platform, null, new SystemLibraryRefreshService(Refresh), query,
            PhotoSearch: photoSearch, MusicFolders: new IndexedMusicFolderBrowser(query, query.GetMusicRootsAsync)),
            new LibrarySourceCapabilities(SupportsFileReveal: true), () => disposed);

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
}
