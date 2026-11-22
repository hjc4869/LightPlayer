using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// The search page: a query field over song, album, artist, video, and photo results, narrowed by
/// result kind and by library source. Results follow the query as it is typed. The All scope
/// previews every kind, and each kind's own scope lists all of its results in batches.
/// </summary>
public sealed partial class SearchPageViewModel : PageViewModelBase, INowPlayingHighlightTarget,
    IScrollPositionPageViewModel, IIncrementalLoadingViewModel, INavigationParameterSource, IDisposable
{
    private const int BatchSize = 60;
    private const int SongPreviewCount = 5;
    private const int PhotoPreviewCount = 8;
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RecentDelay = TimeSpan.FromSeconds(2);

    private readonly LibraryCommands commands;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private readonly ILibrarySourceRegistry registry;
    private readonly RecentSearchList recentSearches;
    private readonly VideoSearchServices? videoSearch;
    private readonly PhotoSearchServices? photoSearch;
    private HashSet<LibrarySourceId>? includedSources;
    private CancellationTokenSource? searchCancellation;
    private SearchRun? activeRun;
    private SearchRun? appliedRun;
    private PhotoSearchSession? photoSession;
    private Task searchTask = Task.CompletedTask;
    private Task? loadMoreTask;
    private MediaItemKey? nowPlaying;
    private string requestedTerm = string.Empty;
    private string? autoRecorded;
    private int searchVersion;
    private double contentWidth = 1160;
    private bool photosHaveMore;
    private int? photoTotalCount;
    private bool updatingSources;
    private bool disposed;

    [ObservableProperty]
    private string query;

    [ObservableProperty]
    private SearchScope scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool isLoadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    private string failureText = string.Empty;

    [ObservableProperty]
    private bool showResults;

    [ObservableProperty]
    private bool showNoResults;

    [ObservableProperty]
    private bool showRecentSearches;

    [ObservableProperty]
    private bool showLandingHint;

    /// <summary>Whether videos and photos are listed as rows, for lack of room for two tiles side by side.</summary>
    [ObservableProperty]
    private bool showMediaAsList;

    [ObservableProperty]
    private bool showSourceFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearFilters))]
    [NotifyPropertyChangedFor(nameof(NoResultsDescription))]
    private bool isSourceFilterActive;

    [ObservableProperty]
    private string sourceFilterLabel = string.Empty;

    public SearchPageViewModel(
        SearchRequest request,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery,
        ILibrarySourceRegistry registry,
        RecentSearchList recentSearches,
        VideoSearchServices? videoSearch = null,
        PhotoSearchServices? photoSearch = null)
    {
        this.commands = commands;
        this.libraryQuery = libraryQuery;
        this.registry = registry;
        this.recentSearches = recentSearches;
        this.videoSearch = videoSearch;
        this.photoSearch = photoSearch;
        TitleKey = nameof(Strings.Resources.Common_Search);
        query = request.Query;
        includedSources = request.Sources?.ToHashSet();

        Songs = new(SearchScope.Songs, nameof(Strings.Resources.Nav_Music), SelectScope, CreateSongRow, static _ => { });
        Albums = new(SearchScope.Albums, nameof(Strings.Resources.Nav_Albums), SelectScope,
            album => AlbumTileFactory.Create(album, commands.AlbumTileActions, albumArt), static tile => tile.ReleaseArt());
        Artists = new(SearchScope.Artists, nameof(Strings.Resources.Nav_Artists), SelectScope,
            artist => SearchArtistTileFactory.Create(artist, commands.ArtistTileActions), static tile => tile.ReleaseArt());
        Videos = new(SearchScope.Videos, nameof(Strings.Resources.Search_VideosHeader), SelectScope,
            CreateVideoTile, static tile => tile.ReleasePreview());
        Photos = new(SearchScope.Photos, nameof(Strings.Resources.Search_PhotosHeader), SelectScope,
            photo => PhotoSearchTileFactory.Create(photo, photoSearch!, () => Photos!.Items), static tile => tile.ReleasePreview());

        // Kinds the head has no library for are left out.
        var scopes = new List<SearchScopeOption>
        {
            new(SearchScope.All, nameof(Strings.Resources.Search_ScopeAll), SelectScope),
            new(SearchScope.Songs, nameof(Strings.Resources.Nav_Music), SelectScope),
            new(SearchScope.Albums, nameof(Strings.Resources.Nav_Albums), SelectScope),
            new(SearchScope.Artists, nameof(Strings.Resources.Nav_Artists), SelectScope),
        };
        if (videoSearch is not null)
        {
            scopes.Add(new(SearchScope.Videos, nameof(Strings.Resources.Search_VideosHeader), SelectScope));
        }

        if (photoSearch is not null)
        {
            scopes.Add(new(SearchScope.Photos, nameof(Strings.Resources.Search_PhotosHeader), SelectScope));
        }

        ScopeOptions = scopes;
        scope = ScopeOptions.Any(option => option.Scope == request.Scope) ? request.Scope : SearchScope.All;
        foreach (var option in ScopeOptions)
        {
            option.IsSelected = option.Scope == scope;
        }

        RefreshSourceOptions();
        RefreshRecentSearches();
        registry.DefinitionsChanged += OnSourcesChanged;
        registry.StatusChanged += OnSourcesChanged;
        recentSearches.Changed += OnRecentSearchesChanged;
        UpdateLayout();
    }

    /// <summary>Raised when the query field should take keyboard focus.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Raised when the results change to those of another query or scope and start from the top.</summary>
    public event EventHandler? ScrollResetRequested;

    public double ScrollOffset { get; set; }

    public SearchSectionViewModel<Sourced<DbMediaFile>, TrackRowModel> Songs { get; }

    public SearchSectionViewModel<Sourced<MediaLibraryAlbum>, LibraryTileModel> Albums { get; }

    public SearchSectionViewModel<UnifiedArtistReference, LibraryTileModel> Artists { get; }

    public SearchSectionViewModel<Sourced<DbVideoFile>, VideoTileModel> Videos { get; }

    public SearchSectionViewModel<Sourced<PhotoSearchMatch>, VideoTileModel> Photos { get; }

    public IReadOnlyList<SearchScopeOption> ScopeOptions { get; }

    public ObservableCollection<SearchSourceOption> SourceOptions { get; } = [];

    public ObservableCollection<RecentSearchItem> RecentSearches { get; } = [];

    /// <summary>Whether a search or a further batch of results is being loaded.</summary>
    public bool IsWorking => IsBusy || IsLoadingMore;

    public bool HasFailures => FailureText.Length > 0;

    public bool CanClearFilters => Scope != SearchScope.All || IsSourceFilterActive;

    public string NoResultsDescription => Localized(CanClearFilters
        ? nameof(Strings.Resources.Search_NoResultsFilteredDescription)
        : nameof(Strings.Resources.Search_NoResultsDescription));

    /// <inheritdoc />
    public object? NavigationParameter => new SearchRequest(
        Query.Trim(),
        Scope,
        includedSources is null
            ? null
            : SourceOptions.Where(option => option.IsSelected).Select(option => option.Id).ToArray());

    public bool CanLoadMore => !disposed && !IsBusy && !IsLoadingMore && Scope switch
    {
        SearchScope.Songs => Songs.Items.Count < Songs.ResultCount,
        SearchScope.Albums => Albums.Items.Count < Albums.ResultCount,
        SearchScope.Artists => Artists.Items.Count < Artists.ResultCount,
        SearchScope.Videos => Videos.Items.Count < Videos.ResultCount,
        SearchScope.Photos => Photos.Items.Count < Photos.ResultCount || photosHaveMore,
        _ => false,
    };

    public int LoadedItemCount => Scope switch
    {
        SearchScope.Songs => Songs.Items.Count,
        SearchScope.Albums => Albums.Items.Count,
        SearchScope.Artists => Artists.Items.Count,
        SearchScope.Videos => Videos.Items.Count,
        SearchScope.Photos => Photos.Items.Count,
        _ => 0,
    };

    public int TotalItemCount => Scope switch
    {
        SearchScope.Songs => Songs.ResultCount,
        SearchScope.Albums => Albums.ResultCount,
        SearchScope.Artists => Artists.ResultCount,
        // Video and photo panels keep no room for results they have yet to show.
        SearchScope.Videos => Videos.Items.Count,
        SearchScope.Photos => Photos.Items.Count,
        _ => 0,
    };

    /// <summary>Runs the search for the query the page was opened with.</summary>
    public Task LoadAsync()
    {
        if (requestedTerm.Length == 0 && Query.Trim().Length > 0)
        {
            StartSearch(TimeSpan.Zero);
        }

        return searchTask;
    }

    /// <summary>Asks the view to move keyboard focus to the query field.</summary>
    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Reports the width available to the result panels, which sizes the previews.</summary>
    public void SetContentWidth(double width)
    {
        if (width > 0 && Math.Abs(width - contentWidth) >= 0.5)
        {
            contentWidth = width;
            UpdateLayout();
        }
    }

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(Songs.Items, nowPlaying);
    }

    public Task LoadMoreAsync() => loadMoreTask is { IsCompleted: false }
        ? loadMoreTask
        : loadMoreTask = LoadMoreCoreAsync();

    public override void OnLocalizedTextChanged()
    {
        base.OnLocalizedTextChanged();
        foreach (var option in ScopeOptions)
        {
            option.RefreshText();
        }

        foreach (SearchSectionViewModel section in new SearchSectionViewModel[] { Songs, Albums, Artists, Videos, Photos })
        {
            section.RefreshText();
        }

        UpdateSourceSummary();
    }

    protected override void OnIsNarrowChanged(bool value) => UpdateLayout();

    partial void OnQueryChanged(string value)
    {
        if (value.Trim() != requestedTerm)
        {
            StartSearch(TypingDelay);
        }
    }

    partial void OnScopeChanged(SearchScope value)
    {
        foreach (var option in ScopeOptions)
        {
            option.IsSelected = option.Scope == value;
        }

        ScrollOffset = 0;
        ScrollResetRequested?.Invoke(this, EventArgs.Empty);
        UpdateLayout();
        OnPropertyChanged(nameof(CanClearFilters));
        OnPropertyChanged(nameof(NoResultsDescription));
        OnPropertyChanged(nameof(NavigationParameter));
    }

    /// <summary>Searches for the query at once and remembers it as a recent search.</summary>
    [RelayCommand]
    private void Submit()
    {
        var term = Query.Trim();
        if (term.Length == 0)
        {
            return;
        }

        if (activeRun is not { IsStarted: true } run || run.Term != term)
        {
            StartSearch(TimeSpan.Zero);
        }

        recentSearches.Add(term);
        autoRecorded = null;
    }

    [RelayCommand]
    private void ClearQuery()
    {
        Query = string.Empty;
        RequestFocus();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        if (includedSources is not null)
        {
            updatingSources = true;
            foreach (var option in SourceOptions)
            {
                option.IsSelected = true;
            }

            updatingSources = false;
            ApplySourceSelection();
        }

        Scope = SearchScope.All;
    }

    [RelayCommand]
    private void ClearRecentSearches() => recentSearches.Clear();

    private void SelectScope(SearchScope value) => Scope = value;

    private void StartSearch(TimeSpan delay)
    {
        var version = ++searchVersion;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
        activeRun = null;
        requestedTerm = Query.Trim();
        if (requestedTerm.Length == 0)
        {
            ClearResults();
            FailureText = string.Empty;
            IsBusy = false;
            searchTask = Task.CompletedTask;
            UpdateLayout();
            OnPropertyChanged(nameof(NavigationParameter));
            return;
        }

        searchCancellation = new CancellationTokenSource();
        IsBusy = true;
        UpdateState();
        activeRun = new SearchRun(version, requestedTerm, includedSources?.ToArray(), searchCancellation.Token);
        searchTask = RunSearchAsync(activeRun, delay);
    }

    private async Task RunSearchAsync(SearchRun run, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, run.Token);
            }

            run.IsStarted = true;
            OnPropertyChanged(nameof(NavigationParameter));

            // Each kind shows as soon as its own query returns, so a slow source does not hold the rest back.
            await Task.WhenAll(LoadMusicAsync(run), LoadVideosAsync(run), LoadPhotosAsync(run));
            if (!Begin(run))
            {
                return;
            }

            FailureText = string.Join(Environment.NewLine, run.Failures.Distinct());
            IsBusy = false;
            UpdateLayout();

            if (ShowResults)
            {
                await Task.Delay(RecentDelay, run.Token);
                RecordSettledQuery(run.Term);
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
        }
    }

    // Replaces the previous query's results once the first results of this one are in.
    private bool Begin(SearchRun run)
    {
        if (disposed || run.Version != searchVersion)
        {
            return false;
        }

        if (!ReferenceEquals(appliedRun, run))
        {
            ClearResults();
            appliedRun = run;
            ScrollOffset = 0;
            ScrollResetRequested?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    private async Task LoadMusicAsync(SearchRun run)
    {
        UnifiedLibrarySearchResults results;
        try
        {
            results = await libraryQuery.SearchAsync(run.Term, run.Token, run.Sources);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", "The music search failed.", exception);
            run.Failures.Add(exception.Message);
            return;
        }

        if (!Begin(run))
        {
            return;
        }

        run.AddFailures(results.SourceFailures);
        Songs.SetResults(results.Files);
        Albums.SetResults(results.Albums);
        Artists.SetResults(results.Artists);
        UpdateLayout();
    }

    private async Task LoadVideosAsync(SearchRun run)
    {
        if (videoSearch is null)
        {
            return;
        }

        UnifiedCollection<Sourced<DbVideoFile>> videos;
        try
        {
            videos = await videoSearch.Query.SearchVideosAsync(run.Term, run.Token, run.Sources);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", "The video search failed.", exception);
            run.Failures.Add(exception.Message);
            return;
        }

        if (!Begin(run))
        {
            return;
        }

        run.AddFailures(videos.SourceFailures);
        Videos.SetResults(videos.Items);
        UpdateLayout();
    }

    private async Task LoadPhotosAsync(SearchRun run)
    {
        if (photoSearch is null)
        {
            return;
        }

        PhotoSearchSession session;
        PhotoSearchBatch batch;
        try
        {
            (session, batch) = await ReadPhotosAsync(run, Scope == SearchScope.Photos ? BatchSize : PhotoPreviewCount);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", "The photo search failed.", exception);
            run.Failures.Add(exception.Message);
            return;
        }

        if (!Begin(run))
        {
            return;
        }

        run.AddFailures(batch.ErrorText);
        photoSession = session;
        photosHaveMore = batch.HasMore;
        photoTotalCount = batch.TotalCount;
        Photos.SetResults(batch.Items);
        UpdateLayout();
    }

    // Starts a photo session for the run. A source whose index changed meanwhile invalidates the
    // session, and the read starts over once.
    private async Task<(PhotoSearchSession Session, PhotoSearchBatch Batch)> ReadPhotosAsync(SearchRun run, int count)
    {
        var session = new PhotoSearchSession(photoSearch!.Sources, run.Term, run.Sources);
        var batch = await session.ReadAsync(count, run.Token);
        if (batch.RestartRequired)
        {
            session = new PhotoSearchSession(photoSearch.Sources, run.Term, run.Sources);
            batch = await session.ReadAsync(count, run.Token);
        }

        return (session, batch.RestartRequired ? batch with { HasMore = false } : batch);
    }

    private async Task LoadMoreCoreAsync()
    {
        if (!CanLoadMore || appliedRun is not { } run)
        {
            return;
        }

        IsLoadingMore = true;
        try
        {
            switch (Scope)
            {
                case SearchScope.Songs:
                    Songs.Show(Songs.Items.Count + BatchSize);
                    NowPlayingHighlight.Apply(Songs.Items, nowPlaying);
                    break;
                case SearchScope.Albums:
                    Albums.Show(Albums.Items.Count + BatchSize);
                    break;
                case SearchScope.Artists:
                    Artists.Show(Artists.Items.Count + BatchSize);
                    break;
                case SearchScope.Videos:
                    Videos.Show(Videos.Items.Count + BatchSize);
                    break;
                case SearchScope.Photos:
                    await LoadMorePhotosAsync(run);
                    break;
            }

            // Lets the new items lay out before another batch is considered.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", "Could not load more search results.", exception);
            if (Begin(run))
            {
                photosHaveMore = false;
                FailureText = exception.Message;
            }
        }
        finally
        {
            IsLoadingMore = false;
            if (Begin(run))
            {
                UpdateLayout();
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanLoadMore)), DispatcherPriority.Background);
            }
        }
    }

    private async Task LoadMorePhotosAsync(SearchRun run)
    {
        if (Photos.Items.Count < Photos.ResultCount)
        {
            Photos.Show(Photos.ResultCount);
            return;
        }

        if (photoSession is not { } session)
        {
            return;
        }

        var batch = await session.ReadAsync(BatchSize, run.Token);
        if (!Begin(run))
        {
            return;
        }

        if (batch.RestartRequired)
        {
            (session, batch) = await ReadPhotosAsync(run, BatchSize);
            if (!Begin(run))
            {
                return;
            }

            photoSession = session;
            Photos.SetResults([]);
        }

        Photos.Append(batch.Items);
        Photos.Show(Photos.ResultCount);
        photosHaveMore = batch.HasMore;
        photoTotalCount = batch.TotalCount;
        if (batch.ErrorText.Length > 0)
        {
            FailureText = batch.ErrorText;
        }
    }

    private void ClearResults()
    {
        appliedRun = null;
        photoSession = null;
        photosHaveMore = false;
        photoTotalCount = null;
        Songs.SetResults([]);
        Albums.SetResults([]);
        Artists.SetResults([]);
        Videos.SetResults([]);
        Photos.SetResults([]);
    }

    // Sizes each section for the scope and the width: a preview among the other kinds, or everything
    // loaded so far in the section's own scope.
    private void UpdateLayout()
    {
        var tileColumns = Math.Max(1, (int)(contentWidth / 136));
        var mediaColumns = Math.Max(1, (int)(contentWidth / 274));
        ShowMediaAsList = IsNarrow || mediaColumns < 2;
        var tilePreview = tileColumns * (tileColumns <= 3 ? 2 : 1);
        Arrange(Songs, SongPreviewCount);
        Arrange(Albums, tilePreview);
        Arrange(Artists, tilePreview);
        Arrange(Videos, ShowMediaAsList ? 3 : mediaColumns * (mediaColumns <= 2 ? 2 : 1));
        Arrange(Photos, ShowMediaAsList ? 3 : Math.Min(PhotoPreviewCount, mediaColumns * 2), photosHaveMore);
        NowPlayingHighlight.Apply(Songs.Items, nowPlaying);

        foreach (var option in ScopeOptions)
        {
            int? count = option.Scope switch
            {
                SearchScope.Songs => Songs.ResultCount,
                SearchScope.Albums => Albums.ResultCount,
                SearchScope.Artists => Artists.ResultCount,
                SearchScope.Videos => Videos.ResultCount,
                SearchScope.Photos => photoTotalCount,
                _ => null,
            };
            option.CountText = count > 0 ? count.Value.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
            option.IsEmpty = appliedRun is not null && !IsBusy &&
                (option.Scope == SearchScope.Photos ? Photos.ResultCount == 0 : count == 0);
        }

        UpdateState();
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(LoadedItemCount));
        OnPropertyChanged(nameof(TotalItemCount));
    }

    private void Arrange<TResult, TItem>(SearchSectionViewModel<TResult, TItem> section, int previewCount, bool moreAvailable = false)
    {
        var all = Scope == SearchScope.All;
        var active = Scope == section.Scope;
        if (all)
        {
            section.Show(previewCount);
        }
        else if (active)
        {
            section.Show(Math.Max(section.Items.Count, BatchSize));
        }

        section.IsVisible = (all || active) && section.Items.Count > 0;
        section.ShowHeader = all;
        section.HasMore = all && (section.ResultCount > section.Items.Count || moreAvailable);
        section.ExtentCount = all ? 0 : section.ResultCount;
    }

    private void UpdateState()
    {
        var hasQuery = requestedTerm.Length > 0;
        var anyVisible = Songs.IsVisible || Albums.IsVisible || Artists.IsVisible || Videos.IsVisible || Photos.IsVisible;
        ShowResults = hasQuery && anyVisible;
        ShowNoResults = hasQuery && !IsBusy && !anyVisible;
        ShowRecentSearches = !hasQuery && RecentSearches.Count > 0;
        ShowLandingHint = !hasQuery && RecentSearches.Count == 0;
    }

    // A query whose results stayed up is remembered without being submitted. Refining it replaces the entry.
    private void RecordSettledQuery(string term)
    {
        if (autoRecorded is { } previous && previous != term &&
            (term.StartsWith(previous, StringComparison.CurrentCultureIgnoreCase) ||
             previous.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)))
        {
            recentSearches.Remove(previous);
        }

        recentSearches.Add(term);
        autoRecorded = term;
    }

    private void OnRecentSearchesChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshRecentSearches();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshRecentSearches);
        }
    }

    private void RefreshRecentSearches()
    {
        if (disposed)
        {
            return;
        }

        RecentSearches.Clear();
        foreach (var text in recentSearches.Items)
        {
            RecentSearches.Add(new RecentSearchItem(text, SearchRecent, recentSearches.Remove));
        }

        UpdateState();
    }

    private void SearchRecent(string text)
    {
        Query = text;
        Submit();
    }

    private void OnSourcesChanged(object? sender, LibrarySourceChangedEventArgs args) =>
        Dispatcher.UIThread.Post(RefreshSourceOptions);

    private void RefreshSourceOptions()
    {
        if (disposed)
        {
            return;
        }

        var sources = registry.Sources
            .Where(source => (source.LoadMusic || source.LoadVideo) &&
                source.Status is not (LibrarySourceStatus.SignInRequired or LibrarySourceStatus.Incompatible))
            .Select(source => (source.Id, Name: source.DisplayName))
            .ToArray();
        if (SourceOptions.Select(option => (option.Id, option.Name)).SequenceEqual(sources))
        {
            return;
        }

        var previous = includedSources?.Count;
        includedSources?.IntersectWith(sources.Select(source => source.Id));
        if (includedSources is { } included && (included.Count == 0 || included.Count == sources.Length))
        {
            includedSources = null;
        }

        updatingSources = true;
        SourceOptions.Clear();
        foreach (var (id, name) in sources)
        {
            SourceOptions.Add(new SearchSourceOption(id, name, includedSources?.Contains(id) ?? true, OnSourceOptionChanged));
        }

        updatingSources = false;
        UpdateSourceSummary();
        if (previous != includedSources?.Count)
        {
            OnPropertyChanged(nameof(NavigationParameter));
            if (requestedTerm.Length > 0)
            {
                StartSearch(TimeSpan.Zero);
            }
        }
    }

    private void OnSourceOptionChanged(SearchSourceOption option)
    {
        if (!updatingSources)
        {
            ApplySourceSelection();
        }
    }

    private void ApplySourceSelection()
    {
        var selected = SourceOptions.Where(option => option.IsSelected).Select(option => option.Id).ToHashSet();

        // Leaving every library out searches all of them again.
        if (selected.Count == 0)
        {
            updatingSources = true;
            foreach (var option in SourceOptions)
            {
                option.IsSelected = true;
            }

            updatingSources = false;
        }

        includedSources = selected.Count is 0 || selected.Count == SourceOptions.Count ? null : selected;
        UpdateSourceSummary();
        OnPropertyChanged(nameof(NavigationParameter));
        if (requestedTerm.Length > 0)
        {
            StartSearch(TimeSpan.Zero);
        }
    }

    private void UpdateSourceSummary()
    {
        var selected = SourceOptions.Where(option => option.IsSelected).ToArray();
        ShowSourceFilter = SourceOptions.Count > 1;
        IsSourceFilterActive = selected.Length < SourceOptions.Count;
        SourceFilterLabel = !IsSourceFilterActive
            ? Strings.Resources.Search_AllLibraries
            : selected.Length == 1
                ? selected[0].Name
                : string.Format(CultureInfo.CurrentCulture, Strings.Resources.RemoteLibraries_LibrariesFormat, selected.Length);
    }

    private TrackRowModel CreateSongRow(Sourced<DbMediaFile> file)
    {
        var matches = Songs.Results;
        var row = TrackRowFactory.CreateRow(file, commands.TrackActions, () => Songs.Items);
        row.AsyncPlaybackContext = () => Task.FromResult<IReadOnlyList<TrackRowModel>>(
            matches.Select(match => TrackRowFactory.CreateRow(match, commands.TrackActions)).ToArray());
        return row;
    }

    /// <summary>
    /// Builds a result tile carrying the same actions the video library grid offers, so a video
    /// found here behaves like the one browsed to.
    /// </summary>
    private VideoTileModel CreateVideoTile(Sourced<DbVideoFile> video)
    {
        var services = videoSearch!;
        var media = new MediaReference(video.SourceId, video.Value.Path);
        var tile = VideoTileModel.ForVideo(video, VideoSubtitle(video.Value));
        tile.OpenCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: false));
        tile.ResumeCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: true));
        tile.ApplyPlaybackState(services.PlaybackStates.Get(media));
        tile.PreviewLoader = async (model, token) =>
        {
            var bitmap = await services.Previews.GetPreviewAsync(media, token).ConfigureAwait(false);
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };
        if (services.Callbacks.ShowProperties is { } showProperties)
        {
            tile.PropertiesCommand = new RelayCommand(() => showProperties(video));
        }

        if (services.Callbacks.OpenContainingFolder is { } openContainingFolder)
        {
            tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(video));
        }

        if (services.Callbacks.ClearSubtitleCache is { } clearSubtitleCache)
        {
            tile.ClearSubtitleCacheCommand = new RelayCommand(() => clearSubtitleCache(video));
        }

        if (services.Callbacks.AddToPlaylist is { } addToPlaylist)
        {
            tile.AddToPlaylistCommand = new RelayCommand(() => addToPlaylist(video));
        }

        tile.ClearPlaybackProgressCommand = new RelayCommand(() =>
        {
            services.Callbacks.ClearPlaybackProgress([video]);
            tile.ApplyPlaybackState(null);
        });

        tile.MarkAsWatchedCommand = new RelayCommand(() =>
        {
            services.Callbacks.MarkWatched([video]);
            tile.ApplyPlaybackState(services.PlaybackStates.Get(media));
        });

        return tile;
    }

    /// <summary>
    /// Plays a result with its own folder as the playlist rather than the rest of the results: what
    /// follows a video is what follows it in the library, not what else happened to match.
    /// </summary>
    private async Task OpenVideoAsync(Sourced<DbVideoFile> video, bool resume)
    {
        if (videoSearch is null)
        {
            return;
        }

        IReadOnlyList<Sourced<DbVideoFile>> siblings;
        try
        {
            siblings = await videoSearch.Query
                .GetVideosInFolderAsync(video.SourceId, video.Value.FolderPath, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", "Could not list the folder of a video result.", exception);
            siblings = [video];
        }

        videoSearch.Callbacks.OpenVideo(video, siblings, resume);
    }

    private static string VideoSubtitle(DbVideoFile video)
    {
        var duration = video.Duration > TimeSpan.Zero
            ? video.Duration.TotalHours >= 1
                ? video.Duration.ToString(@"h\:mm\:ss")
                : video.Duration.ToString(@"m\:ss")
            : string.Empty;
        var resolution = video.Height > 0 ? $"{video.Width}×{video.Height}" : string.Empty;
        return string.Join("  ·  ", new[] { duration, resolution }.Where(part => part.Length > 0));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        searchVersion++;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        registry.DefinitionsChanged -= OnSourcesChanged;
        registry.StatusChanged -= OnSourcesChanged;
        recentSearches.Changed -= OnRecentSearchesChanged;
        ClearResults();
    }

    // One search of a query against a set of sources, superseded when the query or the sources change.
    private sealed class SearchRun(int version, string term, IReadOnlyCollection<LibrarySourceId>? sources, CancellationToken token)
    {
        public int Version { get; } = version;

        public string Term { get; } = term;

        public IReadOnlyCollection<LibrarySourceId>? Sources { get; } = sources;

        public CancellationToken Token { get; } = token;

        /// <summary>Whether the run has queried its sources, after waiting for typing to pause.</summary>
        public bool IsStarted { get; set; }

        public List<string> Failures { get; } = [];

        public void AddFailures(IEnumerable<UnifiedSourceFailure> failures) =>
            Failures.AddRange(failures.Select(failure => $"{failure.SourceName}: {failure.Message}"));

        public void AddFailures(string text) =>
            Failures.AddRange(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
