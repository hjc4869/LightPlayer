using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Adaptive master-detail alternative to <c>GridItemsPageView</c> for the albums and artists
/// pages. Wraps a <see cref="GridItemsPageViewModelBase"/> (the real albums/artists view model,
/// exposed as <see cref="Browse"/>) so all querying, sorting and loading is reused, and adds the
/// selection, inline detail, and adaptive back handling the list layout needs.
///
/// The list stays visible beside the selected item's detail on wide layouts; on narrow layouts it
/// collapses to a single pane where the detail slides in over the list and the first back returns
/// to the list.
/// </summary>
public sealed partial class ListItemsPageViewModel
        : PageViewModelBase, ILibraryBrowsePageViewModel, IIncrementalLoadingViewModel,
            IInPageBackHandler, IInPageDetailState, INowPlayingHighlightTarget, IDisposable
{
    private readonly GridItemsPageViewModelBase browse;
    private readonly Func<LibraryTileModel, Action, PageViewModelBase?> detailFactory;
    private MediaItemKey? nowPlaying;
    private string? pendingDetailKey;
    private bool disposed;

    [ObservableProperty]
    private LibraryTileModel? selectedItem;

    [ObservableProperty]
    private PageViewModelBase? selectedDetail;

    [ObservableProperty]
    private bool isCompact;

    public ListItemsPageViewModel(
        GridItemsPageViewModelBase browse,
        Func<LibraryTileModel, Action, PageViewModelBase?> detailFactory,
        string detailPlaceholderHeader,
        string detailPlaceholderDescription)
    {
        this.browse = browse;
        this.detailFactory = detailFactory;
        DetailPlaceholderHeader = detailPlaceholderHeader;
        DetailPlaceholderDescription = detailPlaceholderDescription;
        Title = browse.Title;
        browse.Items.CollectionChanged += OnBrowseItemsChanged;
        browse.PropertyChanged += OnBrowsePropertyChanged;
        browse.Selection.PropertyChanged += OnSelectionChanged;
    }

    /// <summary>The wrapped grid view model that owns the item list, sort state and loading flags.</summary>
    public GridItemsPageViewModelBase Browse => browse;

    public override PageSearchModel PageSearch => browse.PageSearch;

    public string DetailPlaceholderHeader { get; }

    public string DetailPlaceholderDescription { get; }

    /// <inheritdoc />
    public LibraryItemKind Kind => browse.Kind;

    /// <inheritdoc />
    public double ScrollOffset { get; set; }

    /// <inheritdoc />
    public void ApplyViewSettings(LibraryViewSettings settings) =>
        browse.ApplyViewSettings(settings);

    /// <inheritdoc />
    public Task LoadAsync() => browse.LoadAsync();

    public bool CanLoadMore => browse.CanLoadMore;

    public int LoadedItemCount => browse.LoadedItemCount;

    public int TotalItemCount => browse.TotalItemCount;

    public Task LoadMoreAsync() => browse.LoadMoreAsync();

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        (SelectedDetail as INowPlayingHighlightTarget)?.SetNowPlaying(mediaItem);
    }

    /// <inheritdoc />
    public bool CanHandleBack => browse.CanHandleBack ||
        (SelectedDetail as IInPageBackHandler)?.CanHandleBack == true ||
        IsCompact && SelectedItem is not null;

    public bool CanPreviewBack => !browse.CanHandleBack && IsCompact && SelectedItem is not null &&
        SelectedDetail is not IInPageBackHandler { CanHandleBack: true };

    /// <inheritdoc />
    public string? DetailKey => SelectedItem is { } item ? DescribeTile(item) : null;

    /// <inheritdoc />
    public void RestoreDetail(string? key)
    {
        pendingDetailKey = key;
        ApplyPendingDetail();
    }

    /// <summary>
    /// On the narrow single-pane layout a selected item fills the page, so the first back collapses
    /// to the list instead of leaving the page. On wide layouts both panes are visible, so back is
    /// left to the shell.
    /// </summary>
    public bool TryHandleBack()
    {
        if (browse.TryHandleBack() || (SelectedDetail as IInPageBackHandler)?.TryHandleBack() == true)
        {
            return true;
        }

        if (IsCompact && SelectedItem is not null)
        {
            SelectedItem = null;
            return true;
        }

        return false;
    }

    partial void OnSelectedItemChanged(LibraryTileModel? value)
    {
        if (browse.Selection.IsActive && value is not null)
        {
            SelectedItem = null;
            return;
        }

        var previous = SelectedDetail;
        SelectedDetail = value is null ? null : detailFactory(value, ClearSelection);
        if (!ReferenceEquals(previous, SelectedDetail))
        {
            (previous as IDisposable)?.Dispose();
        }

        // Carry the current now-playing highlight into the freshly created detail.
        (SelectedDetail as INowPlayingHighlightTarget)?.SetNowPlaying(nowPlaying);
        OnPropertyChanged(nameof(CanHandleBack));
        OnPropertyChanged(nameof(DetailKey));
    }

    // A restored key can arrive before the list has finished loading, so re-apply it whenever the
    // items are (re)built.
    private void OnBrowseItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (pendingDetailKey is not null)
        {
            ApplyPendingDetail();
        }
    }

    private void OnBrowsePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanHandleBack))
        {
            OnPropertyChanged(nameof(CanHandleBack));
        }

        if (e.PropertyName == nameof(GridItemsPageViewModelBase.CanLoadMore))
        {
            OnPropertyChanged(nameof(CanLoadMore));
        }
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MultiSelectionModel.IsActive) && browse.Selection.IsActive)
        {
            SelectedItem = null;
        }
    }

    private void ApplyPendingDetail()
    {
        if (pendingDetailKey is null)
        {
            SelectedItem = null;
            return;
        }

        if (browse.Items.FirstOrDefault(item => DescribeTile(item) == pendingDetailKey ||
            DescribeTile(item, includeGroupingKey: false) == pendingDetailKey) is not { } match)
        {
            return;
        }

        pendingDetailKey = null;
        SelectedItem = match;
    }

    // Opaque identity of a tile, only ever compared for equality. The inner escaping keeps titles
    // containing the separator from colliding.
    private static string? DescribeTile(LibraryTileModel tile, bool includeGroupingKey = true) => tile.Identifier is { } id
        ? $"{id.Type}|{Uri.EscapeDataString(id.ArtistName ?? string.Empty)}|{Uri.EscapeDataString(id.AlbumName ?? string.Empty)}|{id.MediaFileEntityId}" +
            (includeGroupingKey && id.AlbumGroupingKey is not null ? $"|{Uri.EscapeDataString(id.AlbumGroupingKey)}" : string.Empty)
        : null;

    partial void OnSelectedDetailChanged(PageViewModelBase? oldValue, PageViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDetailPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDetailPropertyChanged;
        }

        UpdateDetailBackButton();
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CanHandleBack))
        {
            OnPropertyChanged(nameof(CanHandleBack));
        }
    }

    partial void OnIsCompactChanged(bool value)
    {
        UpdateDetailBackButton();
        OnPropertyChanged(nameof(CanHandleBack));
    }

    // Keep the inline detail's back button in step with the shell layout: whenever the shell shows a
    // back button of its own, the inline back button stays hidden.
    protected override void OnHasShellBackButtonChanged(bool value) => UpdateDetailBackButton();

    // The inline detail shows its own back button only when squeezed into a single pane while the
    // shell has no back button (for example while the queue is docked). The narrow and Apple layouts
    // and Android hide it, deferring to the shell's global back button / platform back gesture.
    private void UpdateDetailBackButton()
    {
        if (SelectedDetail is IInlineDetailPage inline)
        {
            inline.ShowBackButton = !PlatformServices.HostProvidesBackNavigation && IsCompact && !HasShellBackButton;
        }
    }

    private void ClearSelection() => SelectedItem = null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        browse.Items.CollectionChanged -= OnBrowseItemsChanged;
        browse.PropertyChanged -= OnBrowsePropertyChanged;
        browse.Selection.PropertyChanged -= OnSelectionChanged;
        if (SelectedDetail is { } detail)
        {
            detail.PropertyChanged -= OnDetailPropertyChanged;
        }
        (SelectedDetail as IDisposable)?.Dispose();
        (browse as IDisposable)?.Dispose();
    }
}
