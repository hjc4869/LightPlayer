using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

public interface IPhotoSearchService
{
    Task<PhotoSearchPage> SearchAsync(string query, int pageSize = 20, string? cursor = null,
        CancellationToken cancellationToken = default);
}

public interface IPhotoIndexService : IPhotoSearchService
{
    IPhotoSearchService GetSearchService(LibrarySourceId sourceId);

    event EventHandler? Changed;
    PhotoIndexStatus Status { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    InferenceModelCatalog GetModelCatalog(CultureInfo culture);
    Task ImportModelAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken = default);
    Task ImportModelAsync(string path, CancellationToken cancellationToken = default);
    Task SelectModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task DeleteModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task DownloadModelAsync(string modelId, CancellationToken cancellationToken = default);
    void PauseModelDownload(string modelId);
    Task CancelModelDownloadAsync(string modelId);
    Task ConfigureAsync(PhotoIndexOptions options, CancellationToken cancellationToken = default);
    Task IndexAsync(CancellationToken cancellationToken = default);
    void CancelIndexing();
}

public sealed record PhotoIndexOptions(bool Enabled = false, string DeviceId = "cpu", bool AllowRemoteSearch = false);

public sealed record PhotoIndexStatus
{
    public bool RuntimeAvailable { get; init; }
    public bool ModelAvailable { get; init; }
    public PhotoIndexOptions Options { get; init; } = new();
    public IReadOnlyList<InferenceDevice> Devices { get; init; } = [];
    public string ActiveDeviceId { get; init; } = "cpu";
    public string? ModelId { get; init; }
    public string? ModelName { get; init; }
    public IReadOnlyList<InferenceInstalledModel> Models { get; init; } = [];
    public IReadOnlyList<InferenceModelDownload> Downloads { get; init; } = [];
    public bool IsIndexing { get; init; }
    public int IndexedCount { get; init; }
    public int ProcessedCount { get; init; }
    public int DiscoveredCount { get; init; }
    public int FailedCount { get; init; }
    public string? Error { get; init; }
    public bool CanEnable => RuntimeAvailable && ModelAvailable;
    public bool IsAvailable => CanEnable && Options.Enabled;
}