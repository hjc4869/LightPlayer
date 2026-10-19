using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class PhotoSearchPageViewModel : PageViewModelBase, IIncrementalLoadingViewModel,
    IScrollPositionPageViewModel, IDisposable
{
    private readonly PhotoSearchServices services;
    private readonly CancellationTokenSource lifetime = new();
    private PhotoSearchSession session;
    private Task? loadMoreTask;
    private bool hasMore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isBusy;

    [ObservableProperty]
    private string errorText = "";

    public PhotoSearchPageViewModel(string query, PhotoSearchServices services, Action? onBack = null)
    {
        Query = query;
        this.services = services;
        session = new PhotoSearchSession(services.Sources, query);
        TitleKey = nameof(Resources.Search_PhotosHeader);
        BackCommand = new RelayCommand(() => onBack?.Invoke(), () => onBack is not null);
    }

    public string Query { get; }
    public string HeaderText => string.Format(Resources.Search_ResultsForFormat, Query);
    public IRelayCommand BackCommand { get; }
    public bool ShowBackButton => BackCommand.CanExecute(null) && !PlatformServices.HostProvidesBackNavigation && !HasShellBackButton;
    public ObservableCollection<VideoTileModel> Items { get; } = [];
    public bool CanLoadMore => hasMore && !IsBusy && !lifetime.IsCancellationRequested;
    public int LoadedItemCount => Items.Count;
    public int TotalItemCount => Items.Count;
    public bool ShowEmpty => !IsBusy && Items.Count == 0;
    public double ScrollOffset { get; set; }

    protected override void OnHasShellBackButtonChanged(bool value) => OnPropertyChanged(nameof(ShowBackButton));

    public Task LoadAsync() => loadMoreTask ?? LoadMoreAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        Reset();
        await LoadMoreAsync();
    }

    public Task LoadMoreAsync() => loadMoreTask is { IsCompleted: false }
        ? loadMoreTask
        : loadMoreTask = LoadMoreCoreAsync();

    private async Task LoadMoreCoreAsync()
    {
        if (!CanLoadMore)
            return;
        IsBusy = true;
        try
        {
            var batch = await session.ReadAsync(60, lifetime.Token);
            if (batch.RestartRequired)
            {
                Reset();
                batch = await session.ReadAsync(60, lifetime.Token);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            foreach (var item in batch.Items)
                Items.Add(PhotoSearchTileFactory.Create(item, services, () => Items));
            ErrorText = batch.ErrorText;
            hasMore = batch.HasMore && !batch.RestartRequired;
            OnPropertyChanged(nameof(LoadedItemCount));
            OnPropertyChanged(nameof(TotalItemCount));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("PhotoSearch", "Could not load photo search results.", exception);
            ErrorText = exception.Message;
            hasMore = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Reset()
    {
        foreach (var item in Items)
            item.ReleasePreview();
        Items.Clear();
        ScrollOffset = 0;
        session = new PhotoSearchSession(services.Sources, Query);
        hasMore = true;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var item in Items)
            item.ReleasePreview();
    }
}

internal static class PhotoSearchTileFactory
{
    internal static VideoTileModel Create(Sourced<PhotoSearchMatch> match, PhotoSearchServices services,
        Func<IEnumerable<VideoTileModel>> shown)
    {
        var photo = new PhotoFileReference(match.SourceId, match.Value.Path, match.Value.FileName, match.Value.SizeInBytes);
        var tile = VideoTileModel.ForPhoto(photo);
        tile.OpenCommand = new RelayCommand(() =>
        {
            services.History?.RecordPhoto(photo.Source, photo.Path);
            services.Callbacks.OpenPhoto?.Invoke(photo, shown().Where(item => item.Photo is not null).Select(item => item.Photo!).ToArray());
        });
        if (services.Callbacks.ShowPhotoProperties is { } properties)
            tile.PropertiesCommand = new RelayCommand(() => properties(photo));
        if (photo.IsLocal && services.Callbacks.OpenPhotoContainingFolder is { } folder)
            tile.OpenFolderCommand = new RelayCommand(() => folder(photo));
        tile.PreviewLoader = async (model, token) =>
        {
            var bitmap = photo.IsLocal
                ? await services.Photos.GetThumbnailAsync(photo.Path, token).ConfigureAwait(false)
                : await services.Previews.GetPreviewAsync(photo.Media, token).ConfigureAwait(false);
            if (bitmap is not null)
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
        };
        return tile;
    }
}