using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Browses indexed tracks as a flat list or by folder, applying the persisted song sorting settings.
/// </summary>
public sealed class SongsPageViewModel : LibraryBrowsePageViewModelBase<TrackRowModel>, INowPlayingHighlightTarget,
    IPageAddressBarSource
{
    private readonly TrackActionCallbacks callbacks;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private readonly ILibrarySourceRegistry? sourceRegistry;
    private readonly Action<MusicFolderReference?>? openFolder;
    private readonly Action<MusicFolderReference?>? openAncestorFolder;
    private readonly MusicFolderReference? currentFolder;
    private MusicFolderListing currentListing = MusicFolderListing.Flat;
    private LibraryLayoutMode layout;
    private MediaItemKey? nowPlaying;

    public SongsPageViewModel(
        TrackActionCallbacks callbacks,
        IUnifiedLibraryQueryService libraryQuery,
        ILibrarySourceRegistry? sourceRegistry = null,
        LibraryLayoutMode layout = LibraryLayoutMode.Grid,
        MusicFolderReference? folder = null,
        Action<MusicFolderReference?>? openFolder = null,
        Action<MusicFolderReference?>? openAncestorFolder = null)
        : base(LibraryItemKind.Song)
    {
        this.callbacks = callbacks;
        this.libraryQuery = libraryQuery;
        this.sourceRegistry = sourceRegistry;
        this.layout = layout;
        this.openFolder = openFolder;
        this.openAncestorFolder = openAncestorFolder;
        currentFolder = folder;
        if (folder is null)
        {
            TitleKey = nameof(Strings.Resources.Nav_Music);
        }
        else
        {
            Title = folder.Path is { Length: > 0 } path ? VideoFolderTree.LeafName(path) : folder.SourceName;
        }
        EmptyHeaderKey = nameof(Strings.Resources.Songs_EmptyHeader);
        EmptyDescriptionKey = PlatformServices.NativeMediaLibrary?.SupportsMusic == true
            ? nameof(Strings.Resources.Library_EmptySourcesDescription) : nameof(Strings.Resources.Songs_EmptyDescription);
        NavigateToFolderCommand = new RelayCommand<MusicFolderReference?>(
            value => { if (value != currentFolder) openAncestorFolder?.Invoke(value); });
        RefreshCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(LoadAsync));
        AddressBar = new PageAddressBarModel(new RelayCommand(GoUp), RefreshCommand,
            nameof(Strings.Resources.Video_GoUp));
        UpdateBreadcrumb(folder?.Path is { Length: > 0 } initialPath
            ? [new MusicFolderEntry(Title, initialPath)] : []);
        UpdateAddressBarVisibility();
        Selection.ObserveItems(Items.Where(item => !item.IsFolder));
    }

    public LibraryLayoutMode Layout => layout;

    public PageAddressBarModel AddressBar { get; }

    public IRelayCommand<MusicFolderReference?> NavigateToFolderCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public void SetLayout(LibraryLayoutMode value)
    {
        if (currentFolder is not null)
        {
            openAncestorFolder?.Invoke(null);
            return;
        }

        if (SetProperty(ref layout, value, nameof(Layout)))
        {
            UpdateAddressBarVisibility();
            ScrollOffset = 0;
            _ = AppErrorReporter.GuardAsync(LoadAsync);
        }
    }

    protected override void OnIsNarrowChanged(bool value) => UpdateAddressBarVisibility();

    protected override void OnHasShellPathMenuChanged(bool value) => UpdateAddressBarVisibility();

    private void UpdateAddressBarVisibility() =>
        AddressBar.IsVisible = Layout == LibraryLayoutMode.Folders && !IsNarrow && !HasShellPathMenu;

    private void GoUp()
    {
        if (currentFolder is not null)
        {
            openAncestorFolder?.Invoke(currentFolder.Path is null ? null : currentFolder with
            {
                Path = currentListing.Ancestors.Count > 1 ? currentListing.Ancestors[^2].Path : null,
            });
        }
    }

    private void UpdateBreadcrumb(IEnumerable<MusicFolderEntry> ancestors)
    {
        AddressBar.CanGoUp = currentFolder is not null;
        AddressBar.Breadcrumb = currentFolder is null ? [] : new[]
        {
            new PageAddressBarItem(Localized(nameof(Strings.Resources.Video_LibraryRoot), "Library"),
                null, NavigateToFolderCommand),
            new PageAddressBarItem(currentFolder.SourceName, currentFolder with { Path = null }, NavigateToFolderCommand),
        }.Concat(ancestors.Select(entry => new PageAddressBarItem(entry.Name,
            currentFolder with { Path = entry.Path }, NavigateToFolderCommand))).ToArray();
    }

    internal void SeedBreadcrumb(SongsPageViewModel previous)
    {
        if (currentFolder is not { Path: { } path } || previous.currentFolder?.SourceId != currentFolder.SourceId)
        {
            return;
        }

        var ancestors = previous.currentListing.Ancestors;
        var index = ancestors.ToList().FindIndex(entry => entry.Path == path);
        if (index >= 0)
        {
            UpdateBreadcrumb(ancestors.Take(index + 1));
        }
        else if (previous.currentListing.Folders.FirstOrDefault(entry => entry.Path == path) is { } child)
        {
            UpdateBreadcrumb(ancestors.Append(child));
        }
    }

    /// <summary>Highlights the row whose file is currently playing, matched by path.</summary>
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(Items, mediaItem);
    }

    // Rows are recreated on load, so re-apply the highlight the shell last pushed.
    protected override void OnItemsRebuilt()
    {
        Selection.SetItems(Items.Where(item => !item.IsFolder));
        NowPlayingHighlight.Apply(Items, nowPlaying);
    }

    protected override bool SupportsIncrementalLoading => true;

    protected override async Task<GroupedLibraryQuery<TrackRowModel>> QueryGroupsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken)
    {
        if (Layout != LibraryLayoutMode.Folders)
            return (await libraryQuery.GetGroupedTracksAsync(settings, cancellationToken)).Select(CreatePagedRow);

        if (currentFolder is null)
        {
            var roots = sourceRegistry?.CreateSnapshot(LibraryMediaCategory.Music).Sources
                .Select(item => CreateFolderRow(new MusicFolderReference(item.Source.Id, null, item.Source.DisplayName),
                    item.Source.DisplayName)).ToArray() ?? [];
            return GroupedLibraryQueries.FromItems(roots,
                item => new LibraryBrowseIndexEntry(item.Title, IsFolder: true), settings, Kind);
        }

        var services = await GetFolderServicesAsync(cancellationToken);
        currentListing = services.MusicFolders is { } browser
            ? await browser.ListAsync(currentFolder.Path, cancellationToken) : MusicFolderListing.Flat;
        cancellationToken.ThrowIfCancellationRequested();
        UpdateBreadcrumb(currentListing.Ancestors);
        if (currentListing.Ancestors.LastOrDefault() is { } leaf)
            Title = leaf.Name;
        var query = currentListing.HasTracks
            ? (await libraryQuery.GetGroupedTracksAsync(settings, cancellationToken, currentFolder.SourceId,
                currentListing.TrackFolderPath)).Select(CreatePagedRow)
            : new GroupedLibraryQuery<TrackRowModel>([], (_, _, _) => Task.FromResult(Array.Empty<TrackRowModel>()));
        var folders = currentListing.Folders.Select(entry =>
                CreateFolderRow(currentFolder with { Path = entry.Path }, entry.Name))
            .OrderBy(item => item, CreateComparer(SortField, Ascending)).ToArray();
        return query.PrependGroup(Strings.Resources.Shell_GroupFolders, folders);
    }

    protected override async Task<PagedResult<TrackRowModel>> QueryPageAsync(
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (Layout == LibraryLayoutMode.Folders)
        {
            return await QueryFolderPageAsync(cursor, cancellationToken);
        }

        var page = await libraryQuery.GetMediaFilesPageAsync(
            new MediaFilePageRequest(ToMediaFileSort(SortField), Ascending, Cursor: cursor),
            cancellationToken);
        ApplyUnifiedMetadata(page);
        foreach (var removal in page.VisibilityRemovals ?? [])
        {
            var existing = Items.FirstOrDefault(item =>
                item.SourceId == removal.SourceId &&
                string.Equals(item.FilePath, removal.Path, StringComparison.Ordinal) &&
                item.StartTimeMs == removal.StartTimeMs);
            if (existing is not null)
            {
                Items.Remove(existing);
            }
        }
        var rows = page.Items.Select(CreatePagedRow).ToArray();
        return new PagedResult<TrackRowModel>(rows, page.NextCursor, page.TotalCount);
    }

    private async Task<PagedResult<TrackRowModel>> QueryFolderPageAsync(string? cursor, CancellationToken cancellationToken)
    {
        PartialFailureText = string.Empty;
        if (currentFolder is null)
        {
            var roots = sourceRegistry?.CreateSnapshot(LibraryMediaCategory.Music).Sources
                .Select(item => CreateFolderRow(new MusicFolderReference(item.Source.Id, null, item.Source.DisplayName),
                    item.Source.DisplayName)) ?? [];
            var rows = roots.OrderBy(item => item, CreateComparer(SortField, Ascending)).ToArray();
            return new PagedResult<TrackRowModel>(rows, null, rows.Length);
        }

        var services = await GetFolderServicesAsync(cancellationToken);
        if (cursor is null)
        {
            var listing = services.MusicFolders is { } browser
                ? await browser.ListAsync(currentFolder.Path, cancellationToken)
                : MusicFolderListing.Flat;
            cancellationToken.ThrowIfCancellationRequested();
            currentListing = listing;
            UpdateBreadcrumb(listing.Ancestors);
            if (listing.Ancestors.LastOrDefault() is { } leaf)
            {
                Title = leaf.Name;
            }
        }

        var folders = cursor is null
            ? currentListing.Folders.Select(entry => CreateFolderRow(currentFolder with { Path = entry.Path }, entry.Name))
                .OrderBy(item => item, CreateComparer(SortField, Ascending)).ToArray()
            : [];
        var page = currentListing.HasTracks
            ? await QuerySourceTracksAsync(services.LibraryQuery, currentFolder.SourceId,
                new MediaFilePageRequest(ToMediaFileSort(SortField), Ascending, Cursor: cursor,
                    FolderPath: currentListing.TrackFolderPath), cancellationToken)
            : new UnifiedPage<Sourced<DbMediaFile>>([], null, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return new PagedResult<TrackRowModel>(folders.Concat(page.Items.Select(CreatePagedRow)).ToArray(),
            page.NextCursor, page.TotalCount + currentListing.Folders.Count);
    }

    private Task<LibrarySourceServices> GetFolderServicesAsync(CancellationToken cancellationToken = default) =>
        currentFolder is not null && sourceRegistry?.TryGetSource(currentFolder.SourceId, out var source) == true && source.LoadMusic
            ? source.EnsureConnectedAsync(cancellationToken)
            : throw new InvalidOperationException("The music library source is unavailable.");

    private TrackRowModel CreateFolderRow(MusicFolderReference folder, string title) =>
        new(folder, title, value => openFolder?.Invoke(value), callbacks);

    private static async Task<UnifiedPage<Sourced<DbMediaFile>>> QuerySourceTracksAsync(
        ILibraryQueryService query, LibrarySourceId sourceId, MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await query.GetMediaFilesPageAsync(request, cancellationToken);
        return new UnifiedPage<Sourced<DbMediaFile>>(
            page.Items.Select(file => new Sourced<DbMediaFile>(sourceId, file)).ToArray(), page.NextCursor, page.TotalCount);
    }

    protected override IComparer<TrackRowModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new TrackRowComparer(field, ascending);

    private TrackRowModel CreatePagedRow(Sourced<DbMediaFile> file)
    {
        var row = TrackRowFactory.CreateRow(file, callbacks);
        row.AsyncPlaybackContext = LoadPlaybackContextAsync;
        return row;
    }

    private async Task<IReadOnlyList<TrackRowModel>> LoadPlaybackContextAsync()
    {
        if (Grouping is { } grouped)
        {
            while (ReferenceEquals(Grouping, grouped) && grouped.HasMoreItems && !grouped.HasError)
                await grouped.LoadMoreAsync();
            if (!ReferenceEquals(Grouping, grouped))
                throw new OperationCanceledException("The grouped playback context changed.");
            if (grouped.HasError)
                throw new InvalidOperationException("The grouped playback context could not be loaded.");
            return grouped.LoadedItems.Where(item => !item.IsFolder).ToArray();
        }
        if (!HasMoreItems)
        {
            return Items.Where(item => !item.IsFolder).ToList();
        }

        var sort = ToMediaFileSort(SortField);
        var ascending = Ascending;
        var folderQuery = Layout == LibraryLayoutMode.Folders ? (await GetFolderServicesAsync()).LibraryQuery : null;
        var folderPath = currentListing.TrackFolderPath;
        for (var attempt = 0; ; attempt++)
        {
            var rows = new List<TrackRowModel>();
            string? cursor = null;
            try
            {
                do
                {
                    var request = new MediaFilePageRequest(
                        sort,
                        ascending,
                        MediaFilePageRequest.MaxPageSize,
                        cursor);
                    var page = folderQuery is null
                        ? await libraryQuery.GetMediaFilesPageAsync(request)
                        : await QuerySourceTracksAsync(folderQuery, currentFolder!.SourceId,
                            request with { FolderPath = folderPath });
                    rows.AddRange(page.Items.Select(file => TrackRowFactory.CreateRow(file, callbacks)));
                    cursor = page.NextCursor;
                }
                while (cursor is not null);

                return rows;
            }
            catch (UnifiedQueryInvalidatedException) when (attempt == 0)
            {
            }
        }
    }

    private static MediaFileSortField ToMediaFileSort(LibrarySortField field) => field switch
    {
        LibrarySortField.AlbumName => MediaFileSortField.AlbumName,
        LibrarySortField.ArtistName => MediaFileSortField.ArtistName,
        LibrarySortField.Genre => MediaFileSortField.Genre,
        LibrarySortField.Year => MediaFileSortField.Year,
        LibrarySortField.DateAdded => MediaFileSortField.DateAdded,
        LibrarySortField.Duration => MediaFileSortField.Duration,
        _ => MediaFileSortField.Title,
    };

    private sealed class TrackRowComparer : IComparer<TrackRowModel>
    {
        private readonly LibrarySortField field;
        private readonly bool ascending;

        public TrackRowComparer(LibrarySortField field, bool ascending)
        {
            this.field = field;
            this.ascending = ascending;
        }

        public int Compare(TrackRowModel? x, TrackRowModel? y)
        {
            if (x is null || y is null)
            {
                return 0;
            }

            if (x.IsFolder != y.IsFolder)
            {
                return x.IsFolder ? -1 : 1;
            }
            if (x.IsFolder)
            {
                var names = LibrarySortKeys.CompareText(x.Title, y.Title);
                return ascending ? names : -names;
            }

            var primary = field switch
            {
                LibrarySortField.AlbumName => LibrarySortKeys.CompareText(x.Album, y.Album),
                LibrarySortField.ArtistName => LibrarySortKeys.CompareText(x.Artist, y.Artist),
                LibrarySortField.Genre => LibrarySortKeys.CompareText(x.Genre, y.Genre),
                LibrarySortField.Year => LibrarySortKeys.CompareYear(x.Year, y.Year),
                LibrarySortField.DateAdded => x.DateAdded.CompareTo(y.DateAdded),
                LibrarySortField.Duration => x.DurationTime.CompareTo(y.DurationTime),
                _ => LibrarySortKeys.CompareText(x.Title, y.Title),
            };

            primary = ascending ? primary : -primary;
            if (primary != 0)
            {
                return primary;
            }

            // Within an album, keep natural disc/track order regardless of direction.
            if (field == LibrarySortField.AlbumName)
            {
                var disc = x.DiscNumber.CompareTo(y.DiscNumber);
                if (disc != 0)
                {
                    return disc;
                }

                var track = x.TrackNumber.CompareTo(y.TrackNumber);
                if (track != 0)
                {
                    return track;
                }
            }

            return LibrarySortKeys.CompareText(x.Title, y.Title);
        }
    }
}
