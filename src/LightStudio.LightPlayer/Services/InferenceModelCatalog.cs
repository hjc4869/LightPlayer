using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

public sealed record InferenceDevice(string Id, string Name);

public sealed record InferenceInstalledModel(string Id, string Name, string? CatalogId = null, bool IsCompatible = true);

public sealed record InferenceModelFile(string Name, Func<CancellationToken, Task<Stream>> OpenReadAsync,
    long MaximumBytes = 16L * 1024 * 1024 * 1024);

public sealed record InferenceModelDefinition(string Id, string Name, string Description, Uri? DownloadUri = null);

public sealed record InferenceModelCatalog(IReadOnlyList<InferenceModelDefinition> Models, string? Description = null, bool CanImport = true);

public enum InferenceModelDownloadState
{
    NotStarted,
    Downloading,
    Paused,
    Verifying,
    Installing,
    Canceling,
    Failed,
}

public sealed record InferenceModelDownload(string ModelId, InferenceModelDownloadState State = InferenceModelDownloadState.NotStarted,
    long ReceivedBytes = 0, long TotalBytes = 0, string? Error = null);