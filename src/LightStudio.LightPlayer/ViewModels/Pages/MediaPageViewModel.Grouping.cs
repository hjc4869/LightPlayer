using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class MediaPageViewModel
{
    [ObservableProperty]
    private GroupedItemsModel<VideoTileModel>? grouping;

    public bool GroupingEnabled => viewSettings.GroupingEnabled;
    public override bool CanHandleBack => Grouping?.IsPickerOpen == true || base.CanHandleBack;

    public override bool TryHandleBack()
    {
        if (Grouping?.IsPickerOpen == true)
        {
            Grouping.IsPickerOpen = false;
            return true;
        }
        return base.TryHandleBack();
    }

    private async Task LoadGroupedFlatAsync(int version, CancellationToken cancellationToken)
    {
        var query = (await videoLibraryQuery.GetGroupedMediaAsync(viewSettings, cancellationToken)).Select(CreateMediaTile);
        if (version != pageVersion)
            return;
        AddressBar.Breadcrumb = [];
        AddressBar.CanGoUp = false;
        currentListing = VideoFolderListing.Empty;
        currentFolderVideos = [];
        currentFolderPhotos = [];
        ReplaceItems([]);
        var groupedQuery = CreateRecentTile() is { } recent
            ? query.PrependGroup(Resources.Shell_GroupFolders, [recent])
            : query;
        Grouping = new GroupedItemsModel<VideoTileModel>(groupedQuery, cancellationToken, Release);
        TotalItemCount = Grouping.TotalItemCount;
        ApplyFailures(query.SourceFailures);
        HasLoadMoreError = query.Groups.Count == 0 && HasPartialFailures;
        IsEmpty = TotalItemCount == 0;
        await Grouping.LoadMoreAsync();
    }

    private async Task GroupFolderItemsAsync(int version, CancellationToken cancellationToken)
    {
        var folders = Items.Where(item => item.IsFolder).ToArray();
        var snapshot = Items.Where(item => !item.IsFolder).ToArray();
        var settings = viewSettings;
        var (query, ordered) = await Task.Run(() =>
        {
            var result = GroupedLibraryQueries.FromItems(snapshot, DescribeGroupedMedia, settings, Kind, out var orderedItems);
            return (result, orderedItems);
        }, cancellationToken);
        if (version != pageVersion)
            return;
        query = query.PrependGroup(Resources.Shell_GroupFolders, folders);
        ordered = folders.Concat(ordered).ToArray();
        Grouping = new GroupedItemsModel<VideoTileModel>(query, cancellationToken, Release, ordered);
        Items.ReplaceAll(ordered);
    }

    private static LibraryBrowseIndexEntry DescribeGroupedMedia(VideoTileModel item)
    {
        var created = item.Photo?.CreatedUtc ?? item.Video?.FileCreatedDate;
        if (created is null && (item.Photo?.IsLocal == true || item.SourcedVideo?.SourceId == LibrarySourceId.Local))
            created = LibraryBrowseIndexes.ReadCreationTime(item.Photo?.Path ?? item.Video?.Path);
        return new LibraryBrowseIndexEntry(item.Title,
            DateAdded: (item.Photo?.DateTaken ?? item.Photo?.LastModifiedUtc ?? item.Video?.FileLastModifiedDate ?? default).UtcTicks,
            Duration: item.Video?.Duration.Ticks ?? 0,
            FileType: Path.GetExtension(item.Photo?.FileName ?? item.Video?.FileName),
            FileSize: item.Photo?.SizeInBytes ?? item.Video?.FileSize ?? 0,
            CreatedUtc: created, IsFolder: item.IsFolder, DateTaken: item.Photo?.DateTaken,
            Country: item.Photo?.Country, State: item.Photo?.State, City: item.Photo?.City);
    }

    partial void OnGroupingChanged(GroupedItemsModel<VideoTileModel>? oldValue, GroupedItemsModel<VideoTileModel>? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.StateChanged -= OnGroupedStateChanged;
            oldValue.PropertyChanged -= OnGroupingPropertyChanged;
            oldValue.Dispose();
        }
        if (newValue is not null)
        {
            newValue.StateChanged += OnGroupedStateChanged;
            newValue.PropertyChanged += OnGroupingPropertyChanged;
        }
        OnPropertyChanged(nameof(CanHandleBack));
    }

    private void OnGroupedStateChanged(object? sender, EventArgs args)
    {
        if (Grouping is not { } current || !ReferenceEquals(sender, current) || disposed)
            return;
        if (Items.Count != current.LoadedItemCount)
        {
            Items.ReplaceAll(current.LoadedItems);
            OnPropertyChanged(nameof(LoadedItemCount));
        }
        IsLoadingMore = current.IsLoading && !IsLoading;
        HasMoreItems = current.HasMoreItems;
        HasLoadMoreError = current.HasError || current.Groups.All(group => group.IsFolder) && HasPartialFailures;
    }

    private void OnGroupingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(GroupedItemsModel.IsPickerOpen))
            OnPropertyChanged(nameof(CanHandleBack));
    }

    private void OnGroupingLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (GroupingEnabled && pageCancellation is not null && !disposed)
            _ = LoadAsync();
    }
}
