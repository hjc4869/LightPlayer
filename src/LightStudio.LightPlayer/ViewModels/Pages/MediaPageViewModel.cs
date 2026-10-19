using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// The media library page. Presents indexed photos and videos as a flat grid, or the contents of
/// one folder with an explorer-style path above the grid. Each folder is a separate page instance
/// pushed onto the shell's navigation stack, so Back returns to the folder above it with its scroll
/// position intact.
/// </summary>
public sealed partial class MediaPageViewModel
    : SelectionPageViewModelBase, ILibraryBrowsePageViewModel, IIncrementalLoadingViewModel, IPageAddressBarSource, IDisposable
{
    /// <summary>Child previews a folder tile composites, at most one full 3x3 grid.</summary>
    private const int MaxCollageImages = 9;

    /// <summary>How many files beneath a folder are tried before giving up on finding previews.</summary>
    private const int MaxCollageCandidates = 24;

    /// <summary>How many folders one walk of a subtree lists before it stops descending.</summary>
    private const int MaxWalkedFolders = 256;

    /// <summary>
    /// How far a folder tile's collage looks for previews. Far shorter than a walk the user asked
    /// for: every tile on screen runs one, and over a remote library each listing is a request.
    /// </summary>
    private const int MaxCollageFolders = 8;

    private readonly IUnifiedVideoLibraryQueryService videoLibraryQuery;
    private readonly ILibrarySourceRegistry sourceRegistry;
    private readonly VideoPreviewService previews;
    private readonly PhotoFolderService photos;
    private readonly IVideoPlaybackStateStore playbackStates;
    private readonly MediaPageCallbacks callbacks;
    private readonly IPlaylistService? videoPlaylists;
    private readonly IVideoHistoryStore? history;

    private VideoFolderListing currentListing = VideoFolderListing.Empty;
    private IReadOnlyList<Sourced<DbVideoFile>> currentFolderVideos = [];
    private IReadOnlyList<PhotoFileReference> currentFolderPhotos = [];
    private IReadOnlyList<VideoHistoryEntry> shownHistory = [];
    private CancellationTokenSource? pageCancellation;
    private string? nextCursor;
    private int pageVersion;
    private Task? loadMoreTask;
    private bool disposed;
    private readonly VideoFolderReference? currentFolder;
    private LibraryViewSettings viewSettings = LibraryViewSettings.Default(LibraryItemKind.Video);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool isLoadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool hasMoreItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool hasLoadMoreError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaTotalItemCount))]
    private int totalItemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialFailures))]
    private string partialFailureText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private LibraryLayoutMode layout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCompactGrid))]
    [NotifyPropertyChangedFor(nameof(UseNarrowList))]
    private LibraryItemPresentation narrowPresentation;

    public bool UseCompactGrid => IsNarrow && NarrowPresentation == LibraryItemPresentation.Grid;

    public bool UseNarrowList => IsNarrow && NarrowPresentation == LibraryItemPresentation.List;

    public MediaPageViewModel(
        IUnifiedVideoLibraryQueryService videoLibraryQuery,
        VideoPreviewService previews,
        PhotoFolderService photos,
        IVideoPlaybackStateStore playbackStates,
        MediaPageCallbacks callbacks,
        ILibrarySourceRegistry sourceRegistry,
        LibraryLayoutMode layout,
        VideoFolderReference? folder = null,
        IPlaylistService? videoPlaylists = null,
        IVideoHistoryStore? history = null)
    {
        this.videoLibraryQuery = videoLibraryQuery;
        this.previews = previews;
        this.photos = photos;
        this.playbackStates = playbackStates;
        this.callbacks = callbacks;
        this.sourceRegistry = sourceRegistry;
        this.layout = layout;
        this.videoPlaylists = videoPlaylists;
        this.history = history;
        currentFolder = folder;
        Strings.LocalizedStrings.Instance.PropertyChanged += OnGroupingLanguageChanged;

        if (videoPlaylists is not null)
        {
            // The star also turns where the list is edited elsewhere: from the favorite video
            // playlist page, or from another copy of this page on the back stack.
            videoPlaylists.Favorites.Items.CollectionChanged += OnFavoriteVideosChanged;
        }

        // The folder's own name titles the page; the library root keeps the navigation label. Set
        // from the path rather than the (not yet loaded) tree so the title is right on the first frame.
        if (folder is { IsRecent: true })
        {
            TitleKey = nameof(Strings.Resources.Video_RecentlyViewed);
        }
        else if (folder is not null)
        {
            Title = folder.Path is { Length: > 0 }
                ? VideoFolderTree.LeafName(folder.Path)
                : folder.SourceName;

            // Browsing is what the history records; opening the list of it must not add to it.
            history?.RecordFolder(folder.SourceId, folder.Path);
        }
        else
        {
            TitleKey = nameof(Strings.Resources.Nav_Video);
        }

        NavigateToFolderCommand = new RelayCommand<VideoFolderReference?>(
            value => { if (value != currentFolder) callbacks.OpenAncestorFolder(value); });
        GoUpCommand = new RelayCommand(GoUp);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        RetryLoadMoreCommand = new AsyncRelayCommand(RetryLoadMoreAsync);
        AddressBar = new PageAddressBarModel(GoUpCommand, RefreshCommand, nameof(Strings.Resources.Video_GoUp))
        {
            Sorting = new MediaSortModel(settings =>
            {
                if (callbacks.ChangeViewSettings is { } changeSettings)
                    changeSettings(settings);
                else
                    ApplyViewSettings(settings);
            }),
        };
        UpdateBreadcrumb(folder?.Path is { Length: > 0 } path
            ? [new VideoFolderEntry(VideoFolderEntryKind.Folder, Title, path)]
            : []);
        UpdateAddressBarLayout();
        Selection.ObserveItems(Items);
        Items.CollectionChanged += OnItemsChanged;
        Selection.LoadAllItemsAsync = LoadAllItemsAsync;
        PageSearch.SetSource(new PageSearchSource(
            () => PageSearchSource.Entries(Items), [Items], this,
            isLoading: () => IsLoading || IsLoadingMore,
            hasPendingItems: () => HasMoreItems || HasLoadMoreError || HasPartialFailures,
            pagination: this, itemState: () => Items));
        MultiSelectionActions.AddVideoActions(Selection, callbacks, playbackStates, ResolveFolderVideosAsync);
        if (callbacks.OpenPhoto is { } openPhoto)
        {
            Selection.AddAction(nameof(Strings.Resources.Common_Open), selected =>
            {
                var selectedPhotos = selected.Cast<VideoTileModel>().Select(item => item.Photo!).ToArray();
                openPhoto(selectedPhotos[0], selectedPhotos);
                return Task.CompletedTask;
            }, selected => selected.All(item => item is VideoTileModel { Photo: not null }));
        }
    }

    public LibraryItemKind Kind => LibraryItemKind.Video;

    /// <summary>Scroll offset preserved while the shell keeps this page on its back stack.</summary>
    public double ScrollOffset { get; set; }

    public IRelayCommand<VideoFolderReference?> NavigateToFolderCommand { get; }

    public IRelayCommand GoUpCommand { get; }

    /// <summary>
    /// Lists the folder again. Browsing reads the library as it is at the moment a folder is opened,
    /// and nothing watches it afterwards, so this is how a file added since then is picked up.
    /// </summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand RetryLoadMoreCommand { get; }

    public PageAddressBarModel AddressBar { get; }

    public BulkObservableCollection<VideoTileModel> Items { get; } = [];

    public BulkObservableCollection<VideoTileModel> FolderItems { get; } = [];

    public BulkObservableCollection<VideoTileModel> MediaItems { get; } = [];

    public bool HasFolders => FolderItems.Count > 0;

    public bool HasMedia => MediaItems.Count > 0;

    public int MediaTotalItemCount => Math.Max(MediaItems.Count, TotalItemCount - FolderItems.Count);

    public int LoadedItemCount => Items.Count;

    public bool CanLoadMore =>
        !disposed && Layout == LibraryLayoutMode.Grid && HasMoreItems &&
        !IsLoading && !IsLoadingMore && !HasLoadMoreError;

    public void ApplyViewSettings(LibraryViewSettings settings)
    {
        settings = settings.Normalized(Kind);
        if (viewSettings == settings || disposed)
        {
            return;
        }

        var groupingChanged = viewSettings.GroupingEnabled || settings.GroupingEnabled;
        viewSettings = settings;
        OnPropertyChanged(nameof(GroupingEnabled));
        AddressBar.Sorting?.ApplySettings(settings);
        ScrollOffset = 0;
        if ((Layout == LibraryLayoutMode.Grid || groupingChanged) && pageCancellation is not null)
        {
            _ = LoadAsync();
        }
        else
        {
            Items.ReplaceAll(SortItems(Items));
        }
    }

    private IEnumerable<VideoTileModel> SortItems(IEnumerable<VideoTileModel> items) =>
        items.OrderBy(item => item, Comparer<VideoTileModel>.Create(CompareItems)).ToArray();

    private int CompareItems(VideoTileModel first, VideoTileModel second)
    {
        var result = (second.Folder?.IsRecent == true).CompareTo(first.Folder?.IsRecent == true);
        if (result != 0)
            return result;

        result = second.IsFolder.CompareTo(first.IsFolder);
        if (result != 0)
            return result;

        result = first.IsFolder ? 0 : viewSettings.Sort switch
        {
            LibrarySortField.DateAdded => (first.Photo?.DateTaken ?? first.Photo?.LastModifiedUtc ?? first.Video?.FileLastModifiedDate ?? default)
                .CompareTo(second.Photo?.DateTaken ?? second.Photo?.LastModifiedUtc ?? second.Video?.FileLastModifiedDate ?? default),
            LibrarySortField.Duration => (first.Video?.Duration ?? TimeSpan.Zero)
                .CompareTo(second.Video?.Duration ?? TimeSpan.Zero),
            LibrarySortField.FileSize => (first.Photo?.SizeInBytes ?? first.Video?.FileSize ?? 0)
                .CompareTo(second.Photo?.SizeInBytes ?? second.Video?.FileSize ?? 0),
            LibrarySortField.FileType => StringComparer.CurrentCultureIgnoreCase.Compare(
                Path.GetExtension(first.Photo?.FileName ?? first.Video?.FileName),
                Path.GetExtension(second.Photo?.FileName ?? second.Video?.FileName)),
            _ => 0,
        };
        if (result == 0)
            result = StringComparer.CurrentCultureIgnoreCase.Compare(first.Title, second.Title);

        return viewSettings.Ascending ? result : -result;
    }

    public bool ShowContent => !IsLoading && !IsEmpty;

    public bool ShowEmpty => !IsLoading && IsEmpty;

    public bool HasPartialFailures => PartialFailureText.Length > 0;

    /// <summary>True while this page is showing the synthetic recently viewed folder.</summary>
    private bool IsRecentFolder => currentFolder is { IsRecent: true };

    public string EmptyHeader => IsRecentFolder
        ? Localized(nameof(Strings.Resources.Video_RecentEmptyHeader), "Nothing viewed yet.")
        : Localized(nameof(Strings.Resources.Video_EmptyHeader), "No media yet.");

    public string EmptyDescription => IsRecentFolder
        ? Localized(
            nameof(Strings.Resources.Video_RecentEmptyDescription),
            "Folders you browse and media you open show up here.")
        : PlatformServices.NativeMediaLibrary?.SupportsPhotosAndVideos == true
            ? Strings.Resources.Library_EmptySourcesDescription
            : Localized(nameof(Strings.Resources.Video_EmptyDescription), "Add a media folder in settings to get started.");

    public string LoadingText => Localized(nameof(Strings.Resources.Video_Loading), "Loading media...");

    public async Task LoadAsync()
    {
        if (disposed)
        {
            return;
        }

        Selection.Exit();
        Grouping = null;
        var (version, cancellationToken) = BeginRequest();
        IsLoading = true;
        IsLoadingMore = false;
        HasLoadMoreError = false;
        HasMoreItems = false;
        PartialFailureText = string.Empty;
        try
        {
            if (Layout == LibraryLayoutMode.Grid)
            {
                if (GroupingEnabled)
                    await LoadGroupedFlatAsync(version, cancellationToken);
                else
                    await LoadFirstFlatPageAsync(version, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await LoadCurrentFolderAsync(version, cancellationToken).ConfigureAwait(true);
                if (GroupingEnabled && version == pageVersion)
                    await GroupFolderItemsAsync(version, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == pageVersion)
            {
                ApplyFolderFailure(exception);
                if (GroupingEnabled)
                {
                    HasLoadMoreError = true;
                    AppErrorReporter.Report(Strings.Resources.Shell_WarnLoadPage, exception);
                }
                ReplaceItems([]);
            }
        }
        finally
        {
            if (version == pageVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Switches between the flat grid and folder browsing. The choice only exists at the library
    /// root, so switching from inside a folder navigates back out to it.
    /// </summary>
    public void SetLayout(LibraryLayoutMode value)
    {
        if (currentFolder is not null)
        {
            callbacks.OpenFolder(null);
            return;
        }

        if (Layout == value)
        {
            return;
        }

        Layout = value;
        ScrollOffset = 0;
        _ = LoadAsync();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Strings.LocalizedStrings.Instance.PropertyChanged -= OnGroupingLanguageChanged;
        Grouping = null;
        Items.CollectionChanged -= OnItemsChanged;
        PageSearch.Close();
        ++pageVersion;
        Selection.Dispose();
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        if (videoPlaylists is not null)
        {
            videoPlaylists.Favorites.Items.CollectionChanged -= OnFavoriteVideosChanged;
        }

        Release(Items);
    }

    protected override void OnIsNarrowChanged(bool value)
    {
        UpdateAddressBarLayout();
        OnPropertyChanged(nameof(UseCompactGrid));
        OnPropertyChanged(nameof(UseNarrowList));
    }

    protected override void OnHasShellPathMenuChanged(bool value) => UpdateAddressBarLayout();

    private void UpdateAddressBarLayout()
    {
        AddressBar.IsVisible = !HasShellPathMenu;
        AddressBar.Margin = IsNarrow ? new Avalonia.Thickness(16, 8, 16, 0) : new Avalonia.Thickness(32, 12, 32, 0);
    }

    /// <summary>
    /// Leaves for the folder above, which is the one before this in the path the listing reported.
    /// A folder whose listing never arrived falls back to its library source's root.
    /// </summary>
    private void GoUp()
    {
        if (currentFolder is null)
        {
            return;
        }

        if (IsRecentFolder || currentFolder.Path is null)
        {
            callbacks.OpenAncestorFolder(null);
            return;
        }

        callbacks.OpenAncestorFolder(currentListing.Ancestors is { Count: > 1 } chain
            ? currentFolder with { Path = chain[^2].Path }
            : currentFolder with { Path = null });
    }

    private IReadOnlyList<VideoTileModel> BuildFolderItems()
    {
        AddressBar.CanGoUp = currentFolder is not null;
        if (currentFolder is null)
        {
            var roots = new List<VideoTileModel>();
            if (CreateRecentTile() is { } recent)
            {
                roots.Add(recent);
            }

            roots.AddRange(sourceRegistry.CreateSnapshot(LibraryMediaCategory.Video).Sources
                .Select(item => CreateSourceTile(item.Source.Id, item.Source.DisplayName)));
            return roots;
        }

        var tiles = new List<VideoTileModel>(currentListing.Entries.Count);
        tiles.AddRange(currentListing.Entries
            .Where(entry => entry.Kind == VideoFolderEntryKind.Folder)
            .Select(entry => CreateFolderTile(entry, currentFolder)));
        tiles.AddRange(currentFolderVideos.Select(CreateVideoTile));
        tiles.AddRange(currentFolderPhotos.Select(CreatePhotoTile));
        return tiles;
    }

    internal void SeedBreadcrumb(MediaPageViewModel previous)
    {
        if (currentFolder is not { IsRecent: false, Path: { } path } ||
            previous.currentFolder is not { IsRecent: false } origin || origin.SourceId != currentFolder.SourceId)
        {
            return;
        }

        var ancestors = previous.currentListing.Ancestors;
        for (int i = 0; i < ancestors.Count; i++)
        {
            if (ancestors[i].Path == path)
            {
                UpdateBreadcrumb(ancestors.Take(i + 1));
                return;
            }
        }

        if (previous.currentListing.Entries.FirstOrDefault(entry =>
                entry.Kind == VideoFolderEntryKind.Folder && entry.Path == path) is { } child)
        {
            UpdateBreadcrumb(ancestors.Append(child));
        }
    }

    private void UpdateBreadcrumb(IEnumerable<VideoFolderEntry> ancestors)
    {
        if (currentFolder is null)
        {
            AddressBar.Breadcrumb = [];
            return;
        }

        if (IsRecentFolder)
        {
            AddressBar.Breadcrumb =
            [
                new PageAddressBarItem(
                    Localized(nameof(Strings.Resources.Video_LibraryRoot), "Library"), null, NavigateToFolderCommand),
                new PageAddressBarItem(
                    Localized(nameof(Strings.Resources.Video_RecentlyViewed), "Recently viewed"),
                    currentFolder, NavigateToFolderCommand),
            ];
            return;
        }

        var sourceRootReference = currentFolder with { Path = null };
        AddressBar.Breadcrumb = new[]
            {
                new PageAddressBarItem(
                    Localized(nameof(Strings.Resources.Video_LibraryRoot), "Library"),
                    null,
                    NavigateToFolderCommand),
                new PageAddressBarItem(currentFolder.SourceName, sourceRootReference, NavigateToFolderCommand),
            }
            .Concat(ancestors
            .Select(step => new PageAddressBarItem(
                step.Name,
                currentFolder with { Path = step.Path },
                NavigateToFolderCommand)))
            .ToList();
    }

    private VideoTileModel CreateMediaTile(Sourced<IndexedMediaFile> media) => media.Value.Image is { } image
        ? CreatePhotoTile(new PhotoFileReference(media.SourceId, image.Path, image.FileName, image.FileSize,
            image.FileLastModifiedDate, image.FileCreatedDate).WithIndexedMetadata(image))
        : CreateVideoTile(new Sourced<DbVideoFile>(media.SourceId, media.Value.Video!));

    private VideoTileModel CreatePhotoTile(PhotoFileReference photo)
    {
        var tile = VideoTileModel.ForPhoto(photo);
        tile.OpenCommand = new RelayCommand(() =>
        {
            if (!IsRecentFolder)
            {
                history?.RecordPhoto(photo.Source, photo.Path);
            }

            callbacks.OpenPhoto?.Invoke(photo, ShownPhotos());
        });
        if (callbacks.ShowPhotoProperties is { } showProperties)
        {
            tile.PropertiesCommand = new RelayCommand(() => showProperties(photo));
        }

        if (photo.IsLocal && callbacks.OpenPhotoContainingFolder is { } openContainingFolder)
        {
            tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(photo));
        }

        tile.PreviewLoader = async (model, token) =>
        {
            if (await PhotoThumbnailAsync(photo.Source, photo.Path, token).ConfigureAwait(false) is { } bitmap)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };
        return tile;
    }

    /// <summary>
    /// Every picture on the page in the order it is listed, read off the tiles rather than off the
    /// folder so the recently viewed list -- which spans folders -- pages the same way a folder does.
    /// </summary>
    private IReadOnlyList<PhotoFileReference> ShownPhotos() =>
        Items.Where(item => item.Photo is not null).Select(item => item.Photo!).ToList();

    /// <summary>
    /// A picture's tile thumbnail: decoded here when the picture is on this device, and asked of the
    /// server otherwise, since pulling a whole remote photo library over just to shrink it would
    /// cost far more than the tiles are worth.
    /// </summary>
    private Task<Bitmap?> PhotoThumbnailAsync(
        LibrarySourceId source,
        string path,
        CancellationToken cancellationToken) =>
        source.IsLocal
            ? photos.GetThumbnailAsync(path, cancellationToken)
            : previews.GetPreviewAsync(new MediaReference(source, path), cancellationToken);

    private VideoTileModel CreateFolderTile(VideoFolderEntry entry, VideoFolderReference parent)
    {
        var reference = parent with { Path = entry.Path };

        var tile = VideoTileModel.ForFolder(
            entry.Name,
            reference,
            entry.ItemCount is { } count ? FormatItemCount(count) : string.Empty);
        tile.PreviewLoader = entry.PreviewPaths is { } previewPaths
            ? async (model, token) => ApplyCollage(
                model,
                await LoadFolderPreviewPathsAsync(reference, previewPaths, token).ConfigureAwait(false),
                token)
            : (model, token) => LoadFolderContentsAsync(reference, model, token);
        tile.OpenCommand = new RelayCommand(() => callbacks.OpenFolder(reference));
        if (callbacks.OpenContainingFolderOfFolder is { } revealFolder && entry.Path is { Length: > 0 })
        {
            tile.OpenFolderCommand = new RelayCommand(() => revealFolder(reference));
        }

        AttachFolderPlaybackCommands(tile, reference);
        return tile;
    }

    /// <summary>One library source, standing for everything it holds.</summary>
    private VideoTileModel CreateSourceTile(LibrarySourceId sourceId, string displayName)
    {
        var reference = new VideoFolderReference(sourceId, null, displayName);
        var tile = VideoTileModel.ForFolder(displayName, reference, string.Empty);
        tile.OpenCommand = new RelayCommand(() => callbacks.OpenFolder(reference));
        tile.PreviewLoader = (model, token) => LoadFolderContentsAsync(reference, model, token);
        AttachFolderPlaybackCommands(tile, reference);
        return tile;
    }

    private void AttachFolderPlaybackCommands(VideoTileModel tile, VideoFolderReference folder)
    {
        tile.MarkAsWatchedCommand = new AsyncRelayCommand(
            () => ApplyToFolderVideosAsync(folder, callbacks.MarkWatched));
        tile.ClearPlaybackProgressCommand = new AsyncRelayCommand(
            () => ApplyToFolderVideosAsync(folder, callbacks.ClearPlaybackProgress));
    }

    /// <summary>
    /// The synthetic folder listing what was browsed, played and viewed recently, shown above the
    /// library sources. Absent while playback history is turned off, and while nothing has been recorded.
    /// </summary>
    private VideoTileModel? CreateRecentTile()
    {
        if (history is null || history.GetEntries().Count == 0)
        {
            return null;
        }

        var name = Localized(nameof(Strings.Resources.Video_RecentlyViewed), "Recently viewed");
        var reference = VideoFolderReference.Recent(name);
        var tile = VideoTileModel.ForFolder(name, reference, string.Empty);
        tile.GlyphKey = "IconHistory";
        // The folder actions cover a subtree; this list spans sources and owns none of its items.
        tile.ContextFlyoutKey = "VideoRecentTileContextFlyout";
        tile.OpenCommand = new RelayCommand(() => callbacks.OpenFolder(reference));
        tile.ClearRecentCommand = new RelayCommand(ClearRecentlyViewed);
        tile.PreviewLoader = LoadRecentCollageAsync;
        return tile;
    }

    private void ClearRecentlyViewed()
    {
        history?.Clear();
        RefreshRecentlyViewed();
    }

    private async Task LoadRecentCollageAsync(VideoTileModel tile, CancellationToken cancellationToken)
    {
        var images = new List<IImage>(MaxCollageImages);
        var pending = new List<Task<Bitmap?>>(MaxCollageImages);
        foreach (var entry in history?.GetEntries() ?? [])
        {
            if (entry.Path is not { Length: > 0 } path)
            {
                continue;
            }

            Task<Bitmap?>? load = entry.Kind switch
            {
                VideoHistoryEntryKind.Video => previews.GetPreviewAsync(
                    new MediaReference(entry.Source, path),
                    cancellationToken),
                VideoHistoryEntryKind.Photo => PhotoThumbnailAsync(
                    entry.Source,
                    path,
                    cancellationToken),
                _ => null,
            };

            if (load is null)
            {
                continue;
            }

            pending.Add(load);
            if (pending.Count < MaxCollageImages - images.Count)
            {
                continue;
            }

            await FlushCollageLoadsAsync(images, pending).ConfigureAwait(false);
            if (images.Count >= MaxCollageImages)
            {
                break;
            }
        }

        if (pending.Count > 0)
        {
            await FlushCollageLoadsAsync(images, pending).ConfigureAwait(false);
        }

        ApplyCollage(tile, images, cancellationToken);
    }

    /// <summary>Runs a playback-state action over every video beneath a folder, however deep it sits.</summary>
    private async Task ApplyToFolderVideosAsync(
        VideoFolderReference folder,
        Action<IReadOnlyList<Sourced<DbVideoFile>>> apply) =>
        apply(await ResolveFolderVideosAsync(folder));

    private async Task<IReadOnlyList<Sourced<DbVideoFile>>> ResolveFolderVideosAsync(VideoFolderReference folder)
    {
        var videos = new List<Sourced<DbVideoFile>>();
        var indexed = new Dictionary<string, Dictionary<string, DbVideoFile>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var (folderPath, entry) in WalkVideoEntriesAsync(
            folder,
            MaxWalkedFolders,
            CancellationToken.None).ConfigureAwait(true))
        {
            // Marking watched measures against the video's duration, which only the index has.
            if (!indexed.TryGetValue(folderPath, out var known))
            {
                known = await IndexedVideosAsync(folder.SourceId, folderPath, CancellationToken.None)
                    .ConfigureAwait(true);
                indexed[folderPath] = known;
            }

            videos.Add(ToSourcedVideo(entry, known, folder.SourceId, folderPath));
        }

        return videos;
    }

    /// <summary>
    /// Composites the previews of the videos and pictures beneath a folder into its tile, and labels
    /// it with what the folder directly holds. Folders that yield no image keep the folder glyph.
    /// </summary>
    private async Task LoadFolderContentsAsync(
        VideoFolderReference folder,
        VideoTileModel tile,
        CancellationToken cancellationToken)
    {
        VideoFolderListing listing;
        try
        {
            listing = await videoLibraryQuery
                .BrowseFolderAsync(folder.SourceId, folder.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A folder that cannot be listed keeps its glyph and says nothing about its contents.
            return;
        }

        var folderPhotos = photos.List(folder.SourceId, listing);
        var count = listing.Entries.Count + folderPhotos.Count;
        if (!cancellationToken.IsCancellationRequested)
        {
            var subtitle = FormatItemCount(count);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    tile.Subtitle = subtitle;
                }
            });
        }

        var images = await CollectFolderPreviewsAsync(folder, listing, folderPhotos, cancellationToken)
            .ConfigureAwait(false);
        ApplyCollage(tile, images, cancellationToken);
    }

    private static void ApplyCollage(
        VideoTileModel tile,
        IReadOnlyList<IImage> images,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0)
        {
            return;
        }

        // Square grid of whichever previews turned up, so a folder with two videos gets a 2x2 with
        // one empty cell rather than a lone thumbnail in the corner of an otherwise empty 3x3.
        var columns = images.Count >= 5 ? 3 : images.Count >= 2 ? 2 : 1;
        AlbumArtService.ApplyOnUiThread(
            images,
            loaded =>
            {
                tile.CollageColumns = columns;
                tile.CollageImages = loaded;
            },
            cancellationToken);
    }

    /// <summary>
    /// Fills a folder tile's collage from what lies beneath it, videos first and then the pictures
    /// of the same folder, so a folder holding both is recognizable by its videos.
    /// </summary>
    private async Task<IReadOnlyList<IImage>> CollectFolderPreviewsAsync(
        VideoFolderReference folder,
        VideoFolderListing listing,
        IReadOnlyList<PhotoFileReference> listingPhotos,
        CancellationToken cancellationToken)
    {
        if (listing.PreviewPaths is { } previewPaths)
        {
            return await LoadFolderPreviewPathsAsync(folder, previewPaths, cancellationToken)
                .ConfigureAwait(false);
        }

        var images = new List<IImage>(MaxCollageImages);
        var pending = new List<Task<Bitmap?>>(MaxCollageImages);
        var candidates = 0;
        var first = true;

        async Task FlushAsync()
        {
            await FlushCollageLoadsAsync(images, pending).ConfigureAwait(false);
        }

        async Task<bool> AddCandidateAsync(Task<Bitmap?> load)
        {
            pending.Add(load);
            candidates++;
            if (pending.Count < MaxCollageImages - images.Count &&
                candidates < MaxCollageCandidates)
            {
                return false;
            }

            await FlushAsync().ConfigureAwait(false);
            return images.Count >= MaxCollageImages || candidates >= MaxCollageCandidates;
        }

        try
        {
            await foreach (var listed in WalkFolderListingsAsync(
                folder,
                MaxCollageFolders,
                cancellationToken,
                listing).ConfigureAwait(false))
            {
                foreach (var entry in listed.Entries)
                {
                    if (entry.Kind != VideoFolderEntryKind.Video)
                    {
                        continue;
                    }

                    if (await AddCandidateAsync(previews.GetPreviewAsync(
                        new MediaReference(folder.SourceId, entry.Path),
                        cancellationToken)).ConfigureAwait(false))
                    {
                        return images;
                    }
                }

                foreach (var photo in first ? listingPhotos : photos.List(folder.SourceId, listed))
                {
                    if (await AddCandidateAsync(PhotoThumbnailAsync(
                        photo.Source,
                        photo.Path,
                        cancellationToken)).ConfigureAwait(false))
                    {
                        return images;
                    }
                }

                first = false;
            }

            if (pending.Count > 0)
            {
                await FlushAsync().ConfigureAwait(false);
            }

            return images;
        }
        catch
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                AlbumArtService.ReleaseImages(images);
                foreach (var pendingLoad in pending)
                {
                    if (pendingLoad.IsCompletedSuccessfully &&
                        await pendingLoad.ConfigureAwait(false) is { } bitmap)
                    {
                        bitmap.Dispose();
                    }
                }
            }

            throw;
        }
    }

    private async Task<IReadOnlyList<IImage>> LoadFolderPreviewPathsAsync(
        VideoFolderReference folder,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var loaded = await Task.WhenAll(paths.Take(MaxCollageImages).Select(path =>
            previews.GetPreviewAsync(new MediaReference(folder.SourceId, path), cancellationToken)))
            .ConfigureAwait(false);
        return loaded.OfType<IImage>().ToArray();
    }

    private static async Task FlushCollageLoadsAsync(
        List<IImage> images,
        List<Task<Bitmap?>> pending)
    {
        var loaded = await Task.WhenAll(pending).ConfigureAwait(false);
        pending.Clear();
        images.AddRange(loaded.OfType<IImage>());
    }

    /// <summary>
    /// Every video beneath a folder with the folder it was found in, breadth first so a folder's own
    /// videos come before its subfolders'.
    /// </summary>
    private async IAsyncEnumerable<(string FolderPath, VideoFolderEntry Entry)> WalkVideoEntriesAsync(
        VideoFolderReference folder,
        int maxFolders,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        VideoFolderListing? first = null)
    {
        await foreach (var listing in WalkFolderListingsAsync(
            folder,
            maxFolders,
            cancellationToken,
            first).ConfigureAwait(false))
        {
            if (listing.Path is not { Length: > 0 } listedPath)
            {
                continue;
            }

            foreach (var entry in listing.Entries)
            {
                if (entry.Kind != VideoFolderEntryKind.Folder)
                {
                    yield return (listedPath, entry);
                }
            }
        }
    }

    /// <summary>
    /// The listings of a folder and the folders beneath it, breadth first. The walk is bounded: a
    /// library is browsed a folder at a time, and refusing to descend forever is what keeps one tile
    /// from listing a whole disk.
    /// </summary>
    private async IAsyncEnumerable<VideoFolderListing> WalkFolderListingsAsync(
        VideoFolderReference folder,
        int maxFolders,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        VideoFolderListing? first = null)
    {
        var queue = new Queue<string?>();
        queue.Enqueue(folder.Path);
        var listed = 0;
        while (queue.Count > 0 && listed < maxFolders)
        {
            var path = queue.Dequeue();
            listed++;
            var listing = first is not null && listed == 1
                ? first
                : await TryBrowseAsync(folder.SourceId, path, cancellationToken).ConfigureAwait(false);
            if (listing is null)
            {
                continue;
            }

            foreach (var entry in listing.Entries)
            {
                if (entry.Kind == VideoFolderEntryKind.Folder)
                {
                    queue.Enqueue(entry.Path);
                }
            }

            yield return listing;
        }
    }

    /// <summary>Lists a folder, or null when it can no longer be read.</summary>
    private async Task<VideoFolderListing?> TryBrowseAsync(
        LibrarySourceId sourceId,
        string? folderPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await videoLibraryQuery
                .BrowseFolderAsync(sourceId, folderPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>What the index knows about the videos of one folder, keyed by path.</summary>
    private async Task<Dictionary<string, DbVideoFile>> IndexedVideosAsync(
        LibrarySourceId sourceId,
        string folderPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var videos = await videoLibraryQuery
                .GetVideosInFolderAsync(sourceId, folderPath, cancellationToken)
                .ConfigureAwait(false);
            return videos
                .GroupBy(video => video.Value.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Browsing does not depend on the index; without it the files simply carry no metadata.
            return new Dictionary<string, DbVideoFile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<Dictionary<string, DbImageFile>> IndexedImagesAsync(
        LibrarySourceId sourceId, string folderPath, CancellationToken cancellationToken)
    {
        try
        {
            var images = await videoLibraryQuery.GetImagesInFolderAsync(sourceId, folderPath, cancellationToken)
                .ConfigureAwait(false);
            return images.ToDictionary(image => image.Value.Path, image => image.Value, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("Library.Query", "Could not read indexed photo metadata.", exception);
            return new Dictionary<string, DbImageFile>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The video behind one listed file: what the index recorded for it, or a stand-in built from
    /// the listing itself for a file no scan has reached yet.
    /// </summary>
    private static Sourced<DbVideoFile> ToSourcedVideo(
        VideoFolderEntry entry,
        IReadOnlyDictionary<string, DbVideoFile> indexed,
        LibrarySourceId sourceId,
        string folderPath)
    {
        if (!indexed.TryGetValue(entry.Path, out var video))
        {
            video = new DbVideoFile
            {
                Path = entry.Path,
                MediaKind = entry.MediaKind,
                DiscTitle = entry.DiscTitle,
                FolderPath = folderPath,
                FileName = entry.Name,
                ExternalAudioPaths = DbVideoFile.JoinPaths(entry.ExternalAudioPaths ?? []),
                ExternalSubtitlePaths = DbVideoFile.JoinPaths(entry.ExternalSubtitlePaths ?? []),
            };
        }

        if (entry.LastModifiedUtc != default)
            video.FileLastModifiedDate = entry.LastModifiedUtc;
        if (entry.CreatedUtc is { } created)
            video.FileCreatedDate = created;
        if (entry.SizeInBytes > 0)
            video.FileSize = entry.SizeInBytes;

        return new Sourced<DbVideoFile>(sourceId, video);
    }

    private VideoTileModel CreateVideoTile(Sourced<DbVideoFile> video)
    {
        var media = new MediaReference(video.SourceId, video.Value.Path);
        var tile = VideoTileModel.ForVideo(video, FormatVideoSubtitle(video.Value));
        tile.OpenCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: false));
        tile.ResumeCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: true));
        tile.ApplyPlaybackState(playbackStates.Get(media));
        tile.PreviewLoader = async (model, token) =>
        {
            var bitmap = await previews.GetPreviewAsync(
                new MediaReference(video.SourceId, video.Value.Path),
                token).ConfigureAwait(false);
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };
        if (callbacks.ShowProperties is { } showProperties)
        {
            tile.PropertiesCommand = new RelayCommand(() => showProperties(video));
        }

        if (callbacks.OpenContainingFolder is { } openContainingFolder)
        {
            tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(video));
        }

        if (callbacks.ClearSubtitleCache is { } clearSubtitleCache)
        {
            tile.ClearSubtitleCacheCommand = new RelayCommand(() => clearSubtitleCache(video));
        }

        tile.ClearPlaybackProgressCommand = new RelayCommand(() =>
        {
            callbacks.ClearPlaybackProgress([video]);
            tile.ApplyPlaybackState(null);
        });

        tile.MarkAsWatchedCommand = new RelayCommand(() =>
        {
            callbacks.MarkWatched([video]);
            tile.ApplyPlaybackState(playbackStates.Get(media));
        });

        if (videoPlaylists is not null)
        {
            tile.IsFavorite = videoPlaylists.IsFavorite(FavoriteEntry(video));
            tile.ToggleFavoriteCommand = new AsyncRelayCommand(() => ToggleFavoriteAsync(video));
        }

        if (callbacks.AddToPlaylist is { } addToPlaylist)
        {
            tile.AddToPlaylistCommand = new RelayCommand(() => addToPlaylist(video));
        }

        return tile;
    }

    private static PlaylistItemModel FavoriteEntry(Sourced<DbVideoFile> video) =>
        PlaylistItemModel.FromVideo(video.SourceId, video.Value);

    private async Task ToggleFavoriteAsync(Sourced<DbVideoFile> video)
    {
        if (videoPlaylists is null)
        {
            return;
        }

        var entry = FavoriteEntry(video);
        if (videoPlaylists.IsFavorite(entry))
        {
            await videoPlaylists.RemoveFromFavoritesAsync(entry);
        }
        else
        {
            await videoPlaylists.AddToFavoritesAsync(entry);
        }
    }

    private void OnFavoriteVideosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (videoPlaylists is null)
        {
            return;
        }

        foreach (var tile in Items)
        {
            if (tile.SourcedVideo is { } video)
            {
                tile.IsFavorite = videoPlaylists.IsFavorite(FavoriteEntry(video));
            }
        }
    }

    /// <summary>Reloads resume points after a retained library page returns from playback.</summary>
    public void RefreshPlaybackProgress()
    {
        foreach (var tile in Items)
        {
            if (tile.SourcedVideo is { } video)
            {
                tile.ApplyPlaybackState(playbackStates.Get(
                    new MediaReference(video.SourceId, video.Value.Path)));
            }
        }

        RefreshRecentlyViewed();
    }

    /// <summary>
    /// Catches a retained page up with history recorded while it sat on the back stack: the library
    /// root gains or loses its recently viewed tile, and the list itself is rebuilt when it moved on.
    /// </summary>
    private void RefreshRecentlyViewed()
    {
        if (history is null || IsLoading)
        {
            return;
        }

        if (IsRecentFolder)
        {
            if (!history.GetEntries().SequenceEqual(shownHistory))
            {
                // The whole load, not just the items: folder entries are resolved against the
                // folder tree, which has to be rebuilt along with them.
                _ = LoadAsync();
            }

            return;
        }

        if (currentFolder is not null)
        {
            return;
        }

        var shown = Items.Count > 0 && Items[0].Folder is { IsRecent: true };
        if (shown == (history.GetEntries().Count > 0))
        {
            return;
        }

        if (GroupingEnabled)
        {
            _ = LoadAsync();
            return;
        }

        if (shown)
        {
            Items[0].ReleasePreview();
            Items.RemoveAt(0);
            TotalItemCount--;
        }
        else if (CreateRecentTile() is { } recent)
        {
            Items.Insert(0, recent);
            TotalItemCount++;
        }

        OnPropertyChanged(nameof(LoadedItemCount));
        IsEmpty = Items.Count == 0;
    }

    private async Task OpenVideoAsync(Sourced<DbVideoFile> video, bool resume)
    {
        try
        {
            var siblings = Layout == LibraryLayoutMode.Folders &&
                currentFolder?.SourceId == video.SourceId &&
                string.Equals(currentFolder.Path, video.Value.FolderPath, StringComparison.OrdinalIgnoreCase)
                    ? currentFolderVideos
                    : await videoLibraryQuery.GetVideosInFolderAsync(
                        video.SourceId,
                        video.Value.FolderPath).ConfigureAwait(true);
            if (!IsRecentFolder)
            {
                history?.RecordVideo(video.SourceId, video.Value.Path);
            }

            callbacks.OpenVideo(video, siblings, resume);
        }
        catch
        {
            // Keep the current page and playback state when its folder can no longer be loaded.
        }
    }

    private async Task LoadFirstFlatPageAsync(int version, CancellationToken cancellationToken)
    {
        var page = await videoLibraryQuery
            .GetMediaItemsPageAsync(new PageRequest(), cancellationToken)
            .ConfigureAwait(true);
        var media = page.Items.ToList();
        var failures = new List<UnifiedSourceFailure>(page.SourceFailures ?? []);
        if (viewSettings != LibraryViewSettings.Default(Kind))
        {
            while (page.NextCursor is { } cursor)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page = await videoLibraryQuery.GetMediaItemsPageAsync(
                    new PageRequest(Cursor: cursor), cancellationToken).ConfigureAwait(true);
                media.AddRange(page.Items);
                failures.AddRange(page.SourceFailures ?? []);
            }
        }

        if (version != pageVersion)
        {
            return;
        }

        ApplyMetadata(page);
        ApplyFailures(failures.Distinct().ToArray());
        AddressBar.Breadcrumb = [];
        AddressBar.CanGoUp = false;
        currentListing = VideoFolderListing.Empty;
        currentFolderVideos = [];
        currentFolderPhotos = [];
        var tiles = media.Select(CreateMediaTile);
        ReplaceItems(CreateRecentTile() is { } recent ? tiles.Prepend(recent) : tiles);
        nextCursor = page.NextCursor;
        TotalItemCount = Math.Max(page.TotalCount is { } count ? count + FolderItems.Count : Items.Count, Items.Count);
        HasMoreItems = nextCursor is not null;
    }

    private async Task LoadCurrentFolderAsync(int version, CancellationToken cancellationToken)
    {
        if (currentFolder is null)
        {
            currentListing = VideoFolderListing.Empty;
            currentFolderVideos = [];
            currentFolderPhotos = [];
            UpdateBreadcrumb([]);
            ReplaceItems(BuildFolderItems());
            TotalItemCount = Items.Count;
            HasMoreItems = false;
            return;
        }

        if (IsRecentFolder)
        {
            await LoadRecentItemsAsync(version, cancellationToken).ConfigureAwait(true);
            return;
        }

        // The folder is read as it is now rather than as the index last recorded it, so a file
        // copied in since the last scan is here to open; what the index holds only decorates it.
        var listing = await videoLibraryQuery
            .BrowseFolderAsync(currentFolder.SourceId, currentFolder.Path, cancellationToken)
            .ConfigureAwait(true);
        if (version != pageVersion)
        {
            return;
        }

        UpdateBreadcrumb(listing.Ancestors);
        IReadOnlyList<Sourced<DbVideoFile>> videos = [];
        if (listing.Path is { Length: > 0 } folderPath)
        {
            var indexed = await IndexedVideosAsync(currentFolder.SourceId, folderPath, cancellationToken)
                .ConfigureAwait(true);
            videos = listing.Entries
                .Where(entry => entry.Kind == VideoFolderEntryKind.Video)
                .Select(entry => ToSourcedVideo(entry, indexed, currentFolder.SourceId, folderPath))
                .ToArray();
        }

        IReadOnlyList<PhotoFileReference> folderPhotos = await Task
            .Run(() => photos.List(currentFolder.SourceId, listing), cancellationToken)
            .ConfigureAwait(true);
        if (folderPhotos.Count > 0 && listing.Path is { Length: > 0 } photoFolder)
        {
            var indexed = await IndexedImagesAsync(currentFolder.SourceId, photoFolder, cancellationToken)
                .ConfigureAwait(true);
            folderPhotos = folderPhotos.Select(photo => indexed.TryGetValue(photo.Path, out var image)
                ? photo.WithIndexedMetadata(image)
                : photo).ToArray();
        }

        if (version != pageVersion)
        {
            return;
        }

        currentListing = listing;
        currentFolderVideos = videos;
        currentFolderPhotos = folderPhotos;
        if (listing.Ancestors is { Count: > 0 })
        {
            Title = listing.Ancestors[^1].Name;
        }

        ReplaceItems(BuildFolderItems());
        TotalItemCount = Items.Count;
        HasMoreItems = false;
    }

    /// <summary>
    /// Fills the recently viewed folder. Entries name a folder, a video or a picture by path; what
    /// the index knows about a video decorates its tile, and a video it has never seen is still
    /// listed by its file name. Nothing here records history: this page is a way back into the
    /// library, not a browse.
    /// </summary>
    private async Task LoadRecentItemsAsync(int version, CancellationToken cancellationToken)
    {
        var entries = history?.GetEntries() ?? [];
        var sourceNames = sourceRegistry.CreateSnapshot(LibraryMediaCategory.Video).Sources
            .ToDictionary(item => item.Source.Id, item => item.Source.DisplayName);
        var tiles = new List<VideoTileModel>(entries.Count);
        var photoListings = new Dictionary<(LibrarySourceId Source, string Path), VideoFolderListing>();
        var photoIndexes = new Dictionary<(LibrarySourceId Source, string Path), Dictionary<string, DbImageFile>>();
        async Task<VideoFolderListing> ReadListingAsync(LibrarySourceId sourceId, string parentPath)
        {
            var key = (sourceId, parentPath);
            if (!photoListings.TryGetValue(key, out var listing))
            {
                try
                {
                    listing = await videoLibraryQuery.BrowseFolderAsync(sourceId, parentPath, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Warning("Library.Query", "Could not read recently viewed media metadata.", exception);
                    listing = VideoFolderListing.Empty;
                }
                photoListings[key] = listing;
            }
            return listing;
        }
        foreach (var entry in entries)
        {
            var sourceId = entry.Source;
            if (!sourceNames.TryGetValue(sourceId, out var sourceName))
            {
                continue;
            }

            if (entry.Kind == VideoHistoryEntryKind.Folder)
            {
                tiles.Add(entry.Path is { Length: > 0 } folderPath
                    ? CreateFolderTile(
                        new VideoFolderEntry(
                            VideoFolderEntryKind.Folder,
                            VideoFolderTree.LeafName(folderPath),
                            folderPath),
                        new VideoFolderReference(sourceId, null, sourceName))
                    : CreateSourceTile(sourceId, sourceName));
            }
            else if (entry.Kind == VideoHistoryEntryKind.Photo)
            {
                if (photos.Describe(sourceId, entry.Path) is not { } photo)
                {
                    continue;
                }

                if (!photo.IsLocal && VideoFolderPaths.ParentOf(photo.Path) is { } parentPath)
                {
                    var listing = await ReadListingAsync(sourceId, parentPath);

                    if (listing.Entries.FirstOrDefault(item => item.Path == photo.Path) is { } metadata)
                        photo = photo with
                        {
                            SizeInBytes = metadata.SizeInBytes,
                            LastModifiedUtc = metadata.LastModifiedUtc,
                            CreatedUtc = metadata.CreatedUtc,
                        };
                }

                if (VideoFolderPaths.ParentOf(photo.Path) is { } photoFolder)
                {
                    var key = (sourceId, photoFolder);
                    if (!photoIndexes.TryGetValue(key, out var indexed))
                    {
                        indexed = await IndexedImagesAsync(sourceId, photoFolder, cancellationToken).ConfigureAwait(true);
                        photoIndexes[key] = indexed;
                    }
                    if (indexed.TryGetValue(photo.Path, out var image))
                        photo = photo.WithIndexedMetadata(image);
                }

                var tile = CreatePhotoTile(photo);
                tile.GoToLocationCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(async () =>
                {
                    var folder = await VideoFolderPaths.GetLocationAsync(photo.Media, sourceRegistry);
                    if (folder is not null)
                    {
                        callbacks.OpenFolder(folder);
                    }
                }));
                tiles.Add(tile);
            }
            else if (entry.Path is { Length: > 0 } videoPath)
            {
                var indexed = await videoLibraryQuery
                    .GetVideoByPathAsync(new MediaReference(sourceId, videoPath), cancellationToken)
                    .ConfigureAwait(true);
                var video = indexed ?? new Sourced<DbVideoFile>(
                    sourceId,
                    new DbVideoFile
                    {
                        Path = videoPath,
                        FolderPath = VideoFolderPaths.ParentOf(videoPath) ?? string.Empty,
                        FileName = VideoFolderTree.LeafName(videoPath),
                    });
                if (GroupingEnabled && video.Value.FileCreatedDate is null && sourceId != LibrarySourceId.Local &&
                    VideoFolderPaths.ParentOf(videoPath) is { } parentPath)
                {
                    var listing = await ReadListingAsync(sourceId, parentPath);
                    if (listing.Entries.FirstOrDefault(item => item.Kind == VideoFolderEntryKind.Video && item.Path == videoPath) is { } metadata)
                        video = ToSourcedVideo(metadata, new Dictionary<string, DbVideoFile> { [videoPath] = video.Value }, sourceId, parentPath);
                }
                var tile = CreateVideoTile(video);
                // This list spans folders, so the way back into the library has to be offered
                // explicitly; inside a folder page the tile is already at its location.
                tile.GoToLocationCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(async () =>
                {
                    var location = await VideoFolderPaths.GetLocationAsync(
                        new MediaReference(sourceId, video.Value.Path), sourceRegistry,
                        video.Value.FolderPath, video.Value.MediaKind != VideoMediaKind.File);
                    if (location is not null)
                    {
                        callbacks.OpenFolder(location);
                    }
                }));
                tiles.Add(tile);
            }
        }

        if (version != pageVersion)
        {
            return;
        }

        shownHistory = entries;
        currentListing = VideoFolderListing.Empty;
        currentFolderVideos = [];
        currentFolderPhotos = [];
        AddressBar.CanGoUp = true;
        UpdateBreadcrumb([]);
        ReplaceItems(tiles);
        TotalItemCount = Items.Count;
        HasMoreItems = false;
    }

    public Task LoadMoreAsync() => GroupingEnabled
        ? Grouping?.LoadMoreAsync() ?? Task.CompletedTask
        : loadMoreTask is { IsCompleted: false }
        ? loadMoreTask
        : loadMoreTask = LoadMoreCoreAsync();

    private async Task LoadMoreCoreAsync()
    {
        if (!CanLoadMore || pageCancellation is null)
        {
            return;
        }

        var version = pageVersion;
        var cancellationToken = pageCancellation.Token;
        IsLoadingMore = true;
        try
        {
            UnifiedPage<Sourced<IndexedMediaFile>> page;
            var restarted = false;
            try
            {
                page = await videoLibraryQuery
                    .GetMediaItemsPageAsync(new PageRequest(Cursor: nextCursor), cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (UnifiedQueryInvalidatedException) when (!cancellationToken.IsCancellationRequested)
            {
                if (version != pageVersion || Layout != LibraryLayoutMode.Grid)
                {
                    return;
                }

                page = await videoLibraryQuery
                    .GetMediaItemsPageAsync(new PageRequest(), cancellationToken)
                    .ConfigureAwait(true);
                restarted = true;
            }

            if (version != pageVersion || Layout != LibraryLayoutMode.Grid)
            {
                return;
            }

            ApplyMetadata(page);
            if (restarted)
            {
                if (page.Items.Length == 0 && HasLoadMoreError)
                {
                    return;
                }

                Selection.Exit();
                var tiles = page.Items.Select(CreateMediaTile);
                ReplaceItems(CreateRecentTile() is { } recent ? tiles.Prepend(recent) : tiles);
            }
            else
            {
                Items.AddRange(page.Items.Select(CreateMediaTile));
            }

            OnPropertyChanged(nameof(LoadedItemCount));
            nextCursor = page.NextCursor;
            TotalItemCount = Math.Max(page.TotalCount is { } count ? count + FolderItems.Count
                : restarted ? Items.Count : TotalItemCount, Items.Count);
            HasMoreItems = nextCursor is not null;
            IsEmpty = Items.Count == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == pageVersion)
            {
                HasLoadMoreError = true;
                AppErrorReporter.Report(Strings.Resources.Shell_WarnLoadPage, exception);
            }
        }
        finally
        {
            if (version == pageVersion)
            {
                IsLoadingMore = false;
            }
        }
    }

    private async Task RetryLoadMoreAsync()
    {
        if (GroupingEnabled)
        {
            if (Grouping is null || Grouping.RequiresReload || Grouping.Groups.All(group => group.IsFolder))
                await LoadAsync();
            else
                await Grouping.RetryAsync();
            return;
        }
        if (Layout == LibraryLayoutMode.Grid && viewSettings != LibraryViewSettings.Default(Kind))
        {
            await LoadAsync();
            return;
        }

        HasLoadMoreError = false;
        await LoadMoreAsync();
    }

    private async Task<bool> LoadAllItemsAsync()
    {
        var version = pageVersion;
        while (!disposed && version == pageVersion && Selection.IsActive)
        {
            if (HasLoadMoreError)
            {
                return false;
            }

            if (!IsLoadingMore && !CanLoadMore)
            {
                return !IsLoading && !HasMoreItems;
            }

            await LoadMoreAsync();
        }

        return false;
    }

    private (int Version, CancellationToken CancellationToken) BeginRequest()
    {
        var version = ++pageVersion;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        return (version, pageCancellation.Token);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added &&
            e.NewStartingIndex + added.Count == Items.Count)
        {
            FolderItems.AddRange(added.Cast<VideoTileModel>().Where(item => item.IsFolder));
            MediaItems.AddRange(added.Cast<VideoTileModel>().Where(item => !item.IsFolder));
        }
        else
        {
            var folders = Items.Where(item => item.IsFolder).ToArray();
            var media = Items.Where(item => !item.IsFolder).ToArray();
            if (!FolderItems.SequenceEqual(folders))
                FolderItems.ReplaceAll(folders);
            if (!MediaItems.SequenceEqual(media))
                MediaItems.ReplaceAll(media);
        }

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(MediaTotalItemCount));
    }

    private void ReplaceItems(IEnumerable<VideoTileModel> values)
    {
        var discarded = Items.ToList();
        Items.ReplaceAll(Layout == LibraryLayoutMode.Grid && viewSettings == LibraryViewSettings.Default(Kind)
            ? values : SortItems(values));
        Release(discarded);
        OnPropertyChanged(nameof(LoadedItemCount));
        IsEmpty = Items.Count == 0;
    }

    private static string FormatVideoSubtitle(DbVideoFile video)
    {
        var duration = video.Duration > TimeSpan.Zero
            ? video.Duration.TotalHours >= 1
                ? video.Duration.ToString(@"h\:mm\:ss")
                : video.Duration.ToString(@"m\:ss")
            : string.Empty;
        var resolution = video.Height > 0 ? $"{video.Width}×{video.Height}" : string.Empty;
        return string.Join("  ·  ", new[] { duration, resolution }.Where(part => part.Length > 0));
    }

    private void ApplyFailures(IReadOnlyList<UnifiedSourceFailure>? failures)
    {
        PartialFailureText = failures is { Count: > 0 }
            ? string.Join(Environment.NewLine, failures.Select(failure =>
                $"{failure.SourceName}: {failure.Message}"))
            : string.Empty;
    }

    /// <summary>Names the source a folder could not be listed from, above the empty grid it leaves.</summary>
    private void ApplyFolderFailure(Exception exception) =>
        PartialFailureText = currentFolder is null
            ? exception.Message
            : $"{currentFolder.SourceName}: {exception.Message}";

    private void ApplyMetadata<T>(UnifiedPage<T> page)
    {
        ApplyFailures(page.SourceFailures);
        if (page.Items.Length == 0 && page.SourceFailures is { Count: > 0 })
        {
            HasLoadMoreError = true;
        }
    }

    private string FormatItemCount(int count) => string.Format(
        CultureInfo.CurrentCulture,
        Localized(nameof(Strings.Resources.Common_ItemsFormat), "{0} items"),
        count);

    private static void Release(IReadOnlyList<VideoTileModel> tiles)
    {
        foreach (var tile in tiles)
        {
            tile.ReleasePreview();
        }
    }
}
