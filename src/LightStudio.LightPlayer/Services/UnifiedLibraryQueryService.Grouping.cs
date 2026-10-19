using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public sealed partial class UnifiedLibraryQueryService
{
    public Task<GroupedLibraryQuery<Sourced<DbMediaFile>>> GetGroupedTracksAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default,
        LibrarySourceId? sourceId = null, string? folderPath = null) =>
        GroupedLibraryQueries.CreateAsync<DbMediaFile, Sourced<DbMediaFile>>(registry, LibraryMediaCategory.Music,
            LibraryItemKind.Song, settings, services => services.LibraryQuery as ILibraryBrowseIndexQuery<DbMediaFile>,
            services => LibraryBrowseIndexes.Tracks(services.LibraryQuery),
            items => new Sourced<DbMediaFile>(items[0].SourceId, items[0].Value),
            visibilitySettings().PreferHigherPriorityDuplicates, cancellationToken, sourceId, folderPath);

    public Task<GroupedLibraryQuery<Sourced<MediaLibraryAlbum>>> GetGroupedAlbumsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) =>
        GroupedLibraryQueries.CreateAsync<MediaLibraryAlbum, Sourced<MediaLibraryAlbum>>(registry, LibraryMediaCategory.Music,
            LibraryItemKind.Album, settings, services => services.LibraryQuery as ILibraryBrowseIndexQuery<MediaLibraryAlbum>,
            services => LibraryBrowseIndexes.Albums(services.LibraryQuery),
            items => new Sourced<MediaLibraryAlbum>(items[0].SourceId, items[0].Value),
            visibilitySettings().PreferHigherPriorityDuplicates, cancellationToken);

    public Task<GroupedLibraryQuery<UnifiedArtistBrowseItem>> GetGroupedArtistsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) =>
        GroupedLibraryQueries.CreateAsync<MediaLibraryArtistBrowseItem, UnifiedArtistBrowseItem>(registry, LibraryMediaCategory.Music,
            LibraryItemKind.Artist, settings, services => services.LibraryQuery as ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>,
            services => LibraryBrowseIndexes.Artists(services.LibraryQuery), MergeArtist, false, cancellationToken);
}

public sealed partial class UnifiedVideoLibraryQueryService
{
    public Task<GroupedLibraryQuery<Sourced<IndexedMediaFile>>> GetGroupedMediaAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) =>
        GroupedLibraryQueries.CreateAsync<IndexedMediaFile, Sourced<IndexedMediaFile>>(registry, LibraryMediaCategory.Video,
            LibraryItemKind.Video, settings, services => services.VideoLibraryQuery as IMediaLibraryQueryService,
            services => LibraryBrowseIndexes.Media(services.VideoLibraryQuery),
            items => new Sourced<IndexedMediaFile>(items[0].SourceId, items[0].Value), false, cancellationToken);

    public Task<GroupedLibraryQuery<Sourced<DbVideoFile>>> GetGroupedVideosAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) =>
        GroupedLibraryQueries.CreateAsync<DbVideoFile, Sourced<DbVideoFile>>(registry, LibraryMediaCategory.Video,
            LibraryItemKind.Video, settings, services => services.VideoLibraryQuery as ILibraryBrowseIndexQuery<DbVideoFile>,
            services => LibraryBrowseIndexes.Videos(services.VideoLibraryQuery),
            items => new Sourced<DbVideoFile>(items[0].SourceId, items[0].Value), false, cancellationToken);
}
