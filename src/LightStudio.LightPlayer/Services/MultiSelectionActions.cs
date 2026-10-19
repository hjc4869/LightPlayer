using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Strings;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public static class MultiSelectionActions
{
    public static void AddVideoActions(
        MultiSelectionModel selection,
        IVideoSelectionCallbacks callbacks,
        IVideoPlaybackStateStore playbackStates,
        Func<VideoFolderReference, Task<IReadOnlyList<Sourced<DbVideoFile>>>>? resolveFolder = null)
    {
        selection.AddAction(nameof(Resources.Common_Play), items =>
        {
            var videos = items.Cast<VideoTileModel>().Select(item => item.SourcedVideo!).ToArray();
            callbacks.OpenVideo(videos[0], videos, false);
            return Task.CompletedTask;
        }, OnlyVideos);

        if (callbacks.AddVideosToPlaylistAsync is { } addToPlaylist)
        {
            selection.AddAction(nameof(Resources.Common_AddToPlaylist),
                items => addToPlaylist(items.Cast<VideoTileModel>().Select(item => item.SourcedVideo!).ToArray()),
                OnlyVideos);
        }

        selection.AddAction(nameof(Resources.Video_MarkAsWatched),
            items => ApplyPlaybackStateAsync(items, callbacks.MarkWatched),
            items => CanResolveVideos(items) && items.Cast<VideoTileModel>().Any(item => item.CanMarkAsWatched));
        selection.AddAction(nameof(Resources.Video_ClearPlaybackProgress),
            items => ApplyPlaybackStateAsync(items, callbacks.ClearPlaybackProgress),
            items => CanResolveVideos(items) && items.Cast<VideoTileModel>().Any(item => item.CanClearPlaybackProgress));
        selection.AddAction(nameof(Resources.Video_ClearSubtitleCache), items =>
        {
            foreach (var item in items.Cast<VideoTileModel>())
            {
                item.ClearSubtitleCacheCommand!.Execute(null);
            }

            return Task.CompletedTask;
        }, items => items.All(item => item is VideoTileModel { ClearSubtitleCacheCommand: not null }) &&
                    items.Cast<VideoTileModel>().Any(item => item.CanClearSubtitleCache));

        bool CanResolveVideos(IReadOnlyList<SelectionItemModel> items) => items.All(item =>
            item is VideoTileModel { SourcedVideo: not null } ||
            resolveFolder is not null && item is VideoTileModel { Folder: { IsRecent: false } });

        async Task ApplyPlaybackStateAsync(
            IReadOnlyList<SelectionItemModel> items,
            Action<IReadOnlyList<Sourced<DbVideoFile>>> apply)
        {
            var videos = new List<Sourced<DbVideoFile>>();
            foreach (var item in items.Cast<VideoTileModel>())
            {
                if (item.SourcedVideo is { } video)
                {
                    videos.Add(video);
                }
                else if (item.Folder is { } folder && resolveFolder is not null)
                {
                    videos.AddRange(await resolveFolder(folder));
                }
            }

            apply(videos.DistinctBy(video => new MediaReference(video.SourceId, video.Value.Path)).ToArray());
            foreach (var item in selection.Items.OfType<VideoTileModel>())
            {
                if (item.SourcedVideo is { } video)
                {
                    item.ApplyPlaybackState(playbackStates.Get(new MediaReference(video.SourceId, video.Value.Path)));
                }
            }
        }

        static bool OnlyVideos(IReadOnlyList<SelectionItemModel> items) =>
            items.All(item => item is VideoTileModel { SourcedVideo: not null });
    }

    public static void AddMusicActions(
        MultiSelectionModel selection,
        LibraryCommands commands,
        Func<IReadOnlyList<SelectionItemModel>, Task<IReadOnlyList<TrackRowModel>>>? resolve = null)
    {
        resolve ??= items => Task.FromResult<IReadOnlyList<TrackRowModel>>(items.Cast<TrackRowModel>().ToArray());
        Add(nameof(Resources.Common_Play), commands.PlayTracks);
        Add(nameof(Resources.Common_Shuffle), commands.ShuffleTracks);
        Add(nameof(Resources.Common_PlayNext), commands.PlayTracksNext);
        Add(nameof(Resources.Common_AddToQueue), commands.AddTracksToQueue);
        if (commands.AddTracksToPlaylistAsync is { } addToPlaylist)
        {
            selection.AddAction(nameof(Resources.Common_AddToPlaylist), async items =>
                await addToPlaylist(await resolve(items)));
        }
        else
        {
            Add(nameof(Resources.Common_AddToPlaylist), commands.AddTracksToPlaylist);
        }

        void Add(string key, Action<IReadOnlyList<TrackRowModel>>? action)
        {
            if (action is not null)
            {
                selection.AddAction(key, async items => action(await resolve(items)));
            }
        }
    }
}