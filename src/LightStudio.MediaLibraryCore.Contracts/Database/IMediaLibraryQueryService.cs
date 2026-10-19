#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.MediaLibraryCore.Database;

public sealed record IndexedMediaFile(DbVideoFile? Video = null, DbImageFile? Image = null)
{
    [JsonIgnore]
    public string Path => Image?.Path ?? Video?.Path ?? string.Empty;

    [JsonIgnore]
    public string DisplayName => Image?.FileName ?? Video?.DisplayName ?? string.Empty;
}

public interface IMediaLibraryQueryService : ILibraryBrowseIndexQuery<IndexedMediaFile>
{
    Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(
        PageRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DbImageFile>> GetImagesInFolderAsync(
        string folderPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DbImageFile>> SearchImagesAsync(
        string keyword, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbImageFile>>([]);
}
