using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Services;

public interface IPageSearchSource
{
    IEnumerable<PageSearchEntry> GetEntries();

    bool IsComplete { get; }

    bool IsLoading { get; }

    bool CanLoadMore { get; }

    Task<bool> LoadMoreAsync();

    event EventHandler? Changed;
}

public sealed class PageSearchSource(
    Func<IEnumerable<PageSearchEntry>> getEntries,
    IReadOnlyList<INotifyCollectionChanged> collections,
    INotifyPropertyChanged? state = null,
    Func<bool>? isLoading = null,
    Func<bool>? hasPendingItems = null,
    IIncrementalLoadingViewModel? pagination = null,
    Func<IEnumerable<INotifyPropertyChanged>>? itemState = null) : IPageSearchSource
{
    private EventHandler? changed;
    private HashSet<INotifyPropertyChanged> observedItems = [];

    public IEnumerable<PageSearchEntry> GetEntries() => getEntries();

    public bool IsComplete => !IsLoading && !CanLoadMore && hasPendingItems?.Invoke() != true;

    public bool IsLoading => isLoading?.Invoke() == true;

    public bool CanLoadMore => pagination?.CanLoadMore == true;

    public event EventHandler? Changed
    {
        add
        {
            if (changed is null)
            {
                foreach (var collection in collections)
                {
                    collection.CollectionChanged += OnCollectionChanged;
                }

                if (state is not null)
                {
                    state.PropertyChanged += OnStateChanged;
                }

                ObserveItems();
            }

            changed += value;
        }
        remove
        {
            changed -= value;
            if (changed is null)
            {
                foreach (var collection in collections)
                {
                    collection.CollectionChanged -= OnCollectionChanged;
                }

                if (state is not null)
                {
                    state.PropertyChanged -= OnStateChanged;
                }

                foreach (var item in observedItems)
                {
                    item.PropertyChanged -= OnItemChanged;
                }

                observedItems.Clear();
            }
        }
    }

    public async Task<bool> LoadMoreAsync()
    {
        if (pagination is null || !pagination.CanLoadMore)
        {
            return false;
        }

        var count = pagination.LoadedItemCount;
        await pagination.LoadMoreAsync();
        return pagination.LoadedItemCount > count;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        ObserveItems();
        changed?.Invoke(this, args);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        changed?.Invoke(this, EventArgs.Empty);

    private void ObserveItems()
    {
        if (itemState is null)
        {
            return;
        }

        var replacement = itemState().ToHashSet();
        foreach (var previous in observedItems.Except(replacement))
        {
            previous.PropertyChanged -= OnItemChanged;
        }

        foreach (var item in replacement.Except(observedItems))
        {
            item.PropertyChanged += OnItemChanged;
        }

        observedItems = replacement;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or "Title" or "Subtitle" or "Header" or "Meta" or "DisplaySubtitle")
        {
            changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public static IEnumerable<PageSearchEntry> Entries<T>(
        IEnumerable<T> items,
        IReadOnlyList<object>? ancestors = null) where T : class =>
        items.Select(item => new PageSearchEntry(item, Text(item), ancestors));

    public static string Text(object item) => item switch
    {
        LibraryTileModel tile => string.Join('\n', tile.Title, tile.Subtitle),
        TrackRowModel track => string.Join('\n', track.Title,
            track.HideArtist ? string.Empty : track.Artist,
            track.HideAlbum ? string.Empty : track.Album,
            track.Year, track.Genre, track.Duration),
        VideoTileModel video => string.Join('\n', video.Title, video.DisplaySubtitle),
        PlaylistModel playlist => string.Join('\n', playlist.Title, playlist.Subtitle),
        SpotlightSlideModel slide => string.Join('\n', slide.Header, slide.Title, slide.Subtitle, slide.Meta),
        _ => string.Empty,
    };
}