using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LocalServices.Photos;

public sealed class PhotoIndexService : IPhotoIndexService, IDisposable, IAsyncDisposable
{
    private const string SystemPhotoRoot = "system-library://photos";
    private readonly IMultiMediaPlatformIO platform;
    private readonly ILibraryLocationService locations;
    private readonly INativeMediaLibraryProvider? nativeLibrary;
    private readonly IAppSettingsStore? settingsStore;
    private readonly string dataDirectory;
    private readonly string settingsPath;
    private readonly IInferenceProvider provider;
    private readonly IInferenceModelDownloads downloads;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim inference = new(1, 1);
    private readonly SemaphoreSlim searches = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, SearchSnapshot> snapshots = new(StringComparer.Ordinal);
    private PhotoIndexSettings settings;
    private PhotoIndexStatus status = new();
    private PhotoIndexDatabase? database;
    private IEmbeddingInferenceSession? encoder;
    private IEmbeddingInferenceSession? textEncoder;
    private CancellationTokenSource? indexingCancellation;
    private Task indexingTask = Task.CompletedTask;
    private bool initialized;
    private bool disposed;
    private Task? disposalTask;
    private long catalogGeneration;

    public PhotoIndexService(IMultiMediaPlatformIO platform, ILibraryLocationService locations, IInferenceProvider provider,
        string? dataDirectory = null, string? nativeLibraryDirectory = null,
        INativeMediaLibraryProvider? nativeLibrary = null, IAppSettingsStore? settingsStore = null)
    {
        this.platform = platform;
        this.locations = locations;
        this.nativeLibrary = nativeLibrary;
        this.settingsStore = settingsStore;
        this.provider = provider;
        SqliteDatabase.InitializeVectors ??= PhotoVectorExtension.Load;
        this.dataDirectory = dataDirectory ?? AppPaths.DataDirectory;
        settingsPath = Path.Combine(this.dataDirectory, "photo-settings.json");
        if (nativeLibraryDirectory is not null)
            PhotoVectorExtension.NativeLibraryDirectory = nativeLibraryDirectory;
        settings = LoadSettings();
        downloads = provider.CreateDownloads(Path.Combine(this.dataDirectory, "Models", "Photos"),
            (files, repository, token) => ImportModelCoreAsync(files, repository, selectModel: false, token));
        status = status with
        {
            Options = settings.Options,
            ModelId = settings.ModelId,
            ModelName = settings.ModelName,
            Models = InstalledModels(),
            Downloads = downloads.Status,
        };
        downloads.Changed += OnDownloadsChanged;
        if (nativeLibrary is not null)
        {
            nativeLibrary.CatalogChanged += OnLibraryChanged;
            if (settingsStore is not null)
                settingsStore.NativeLibraryChanged += OnLibraryChanged;
        }
    }

    public IPhotoSearchService GetSearchService(LibrarySourceId sourceId) => new SourceSearchService(this, sourceId);

    private bool UseSystemPhotos => nativeLibrary?.SupportsPhotosAndVideos == true &&
        settingsStore?.NativeLibrary.UseSystemMediaLibrary != false;

    private async void OnLibraryChanged(object? sender, EventArgs eventArgs)
    {
        Interlocked.Increment(ref catalogGeneration);
        try
        {
            await StopIndexingAsync().ConfigureAwait(false);
            if (!disposed && Status.Options.Enabled)
                await IndexAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("PhotoIndex", "Could not update the photo index after the library changed.", exception);
        }
    }

    public event EventHandler? Changed;
    public PhotoIndexStatus Status => Volatile.Read(ref status);

    public InferenceModelCatalog GetModelCatalog(CultureInfo culture) => provider.GetModelCatalog(Status.Options.DeviceId, culture);

    public async Task DownloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!GetModelCatalog(CultureInfo.InvariantCulture).Models.Any(model => model.Id == modelId))
            throw new ArgumentException("Select a model supported by the current device.", nameof(modelId));
        if (Status.Models.Any(model => model.CatalogId == modelId) &&
            Status.Downloads.All(download => download.ModelId != modelId || download.State == InferenceModelDownloadState.NotStarted))
            return;
        await downloads.StartAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    public void PauseModelDownload(string modelId) => downloads.Pause(modelId);

    public Task CancelModelDownloadAsync(string modelId) => downloads.CancelAsync(modelId);

    private void OnDownloadsChanged(object? sender, EventArgs eventArgs) => Update(current => current with { Downloads = downloads.Status });

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
                return;
            try
            {
                await platform.PrepareLibraryDatabaseAsync().ConfigureAwait(false);
                await using var probe = SqliteDatabase.CreateConnection("Data Source=:memory:;Pooling=False", vectors: true);
                await using var command = probe.CreateCommand();
                command.CommandText = "SELECT sqlite_version();";
                var version = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (Version.Parse(version) < new Version(3, 38))
                    throw new NotSupportedException("Photo indexing requires SQLite 3.38 or newer.");
                var devices = await provider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                if (!devices.Any(device => device.Id == settings.Options.DeviceId))
                    settings = settings with { Options = settings.Options with { DeviceId = provider.DefaultDeviceId } };
                var modelAvailable = settings.ModelId is not null &&
                    settings.ModelFiles.All(name => File.Exists(Path.Combine(ModelDirectory(settings.ModelId), name))) &&
                    provider.IsModelSupported(settings.ModelFileName, settings.Options.DeviceId);
                if (!modelAvailable && settings.Options.Enabled)
                    settings = settings with { Options = settings.Options with { Enabled = false } };
                var count = 0;
                if (modelAvailable && (OperatingSystem.IsBrowser() || settings.Options.Enabled || File.Exists(Path.Combine(platform.LocalDataPath, "photo-v1.sqlite"))))
                {
                    var indexDatabase = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
                    count = await indexDatabase.CountAsync(settings.ModelId!, cancellationToken).ConfigureAwait(false);
                }
                Update(current => current with
                {
                    Options = settings.Options,
                    ActiveDeviceId = settings.Options.DeviceId,
                    RuntimeAvailable = true,
                    ModelAvailable = modelAvailable,
                    Models = InstalledModels(),
                    Devices = devices,
                    IndexedCount = count,
                    Error = null,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLog.Warning("PhotoIndex", "Photo indexing is unavailable on this host.", exception);
                Update(current => current with { RuntimeAvailable = false, Error = exception.Message });
            }
            initialized = true;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public Task ImportModelAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken = default) =>
        ImportModelCoreAsync(files, null, selectModel: true, cancellationToken);

    public Task ImportModelAsync(string path, CancellationToken cancellationToken = default) =>
        ImportModelAsync(provider.GetImportFiles(path), cancellationToken);

    private async Task ImportModelCoreAsync(IReadOnlyList<InferenceModelFile> files, string? repositoryId, bool selectModel,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(dataDirectory, "Models", "Photos", ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!Status.RuntimeAvailable)
                throw new InvalidOperationException("Photo indexing dependencies are unavailable.");
            if (selectModel && !GetModelCatalog(CultureInfo.CurrentUICulture).CanImport)
                throw new NotSupportedException(GetModelCatalog(CultureInfo.CurrentUICulture).Description);
            var package = await provider.PrepareImportAsync(files, cancellationToken).ConfigureAwait(false);
            repositoryId ??= package.CatalogId;
            var selected = package.Files.ToDictionary(file => file.Name, StringComparer.Ordinal);
            if (selected.Count == 0 || !selected.ContainsKey(package.ModelFileName) || selected.Keys.Any(name => !IsModelFileName(name)))
                throw new ArgumentException("Model files must have plain file names.", nameof(files));
            Directory.CreateDirectory(staging);
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            foreach (var (name, file) in selected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                using var source = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                var path = Path.Combine(staging, name);
                await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var maximumBytes = Math.Min(file.MaximumBytes, 16L * 1024 * 1024 * 1024);
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        if (destination.Length + count > maximumBytes)
                            throw new InvalidDataException($"Invalid photo model file size: {name}.");
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    if (destination.Length == 0)
                        throw new InvalidDataException($"Invalid photo model file size: {name}.");
                }
                fingerprint.AppendData(Encoding.UTF8.GetBytes(name));
                fingerprint.AppendData(await provider.HashModelFileAsync(path, cancellationToken).ConfigureAwait(false));
            }
            var modelId = Convert.ToHexString(fingerprint.GetHashAndReset());
            var validated = await provider.ValidateModelAsync(staging, package.ModelFileName, cancellationToken).ConfigureAwait(false);
            if (validated.EmbeddingDimensions is < 1 or > 8192)
                throw new InvalidDataException("Invalid photo model embedding dimensions.");
            repositoryId ??= settings.Models.FirstOrDefault(existing => existing.Id == modelId)?.CatalogId;
            var definition = provider.GetModelCatalog(provider.DefaultDeviceId, CultureInfo.InvariantCulture).Models
                .FirstOrDefault(candidate => candidate.Id == repositoryId);
            var modelName = package.Name ?? (definition is null ? validated.Name : $"{definition.Name} ({package.ModelFileName})");
            var installed = new StoredEmbeddingModel(modelId, modelName, package.ModelFileName,
                selected.Keys.Order(StringComparer.Ordinal).ToArray(), validated.EmbeddingDimensions, repositoryId);
            var installedDirectory = ModelDirectory(modelId);
            if (!Directory.Exists(installedDirectory))
                Directory.Move(staging, installedDirectory);
            var replacement = settings with
            {
                Models = settings.Models.Where(existing => existing.Id != modelId).Append(installed).ToArray(),
            };
            if (provider.IsModelSupported(installed.FileName, settings.Options.DeviceId) && (selectModel || !Status.ModelAvailable))
            {
                await ApplyModelAsync(WithModel(replacement, installed), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SaveSettings(replacement);
                settings = replacement;
                Update(current => current with { Models = InstalledModels() });
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
            finally
            {
                lifecycle.Release();
            }
        }
    }

    public async Task SelectModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var model = settings.Models.SingleOrDefault(candidate => candidate.Id == modelId && ModelExists(candidate))
                ?? throw new ArgumentException("Select an installed photo model.", nameof(modelId));
            if (!provider.IsModelSupported(model.FileName, settings.Options.DeviceId))
                throw new NotSupportedException(GetModelCatalog(CultureInfo.CurrentUICulture).Description);
            if (settings.ModelId == modelId)
                return;
            await ApplyModelAsync(WithModel(settings, model), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!settings.Models.Any(model => model.Id == modelId))
                throw new ArgumentException("Select an installed photo model.", nameof(modelId));
            if (settings.ModelId == modelId)
            {
                var next = settings.Models.FirstOrDefault(model => model.Id != modelId && ModelExists(model) &&
                    provider.IsModelSupported(model.FileName, settings.Options.DeviceId));
                await ApplyModelAsync(WithModel(settings, next), cancellationToken).ConfigureAwait(false);
            }
            var directory = ModelDirectory(modelId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            var replacement = settings with { Models = settings.Models.Where(model => model.Id != modelId).ToArray() };
            SaveSettings(replacement);
            settings = replacement;
            Update(current => current with { Models = InstalledModels() });
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private static PhotoIndexSettings WithModel(PhotoIndexSettings source, StoredEmbeddingModel? model) => source with
    {
        ModelId = model?.Id,
        ModelName = model?.Name,
        ModelFileName = model?.FileName ?? string.Empty,
        ModelFiles = model?.Files ?? [],
        EmbeddingDimensions = model?.EmbeddingDimensions ?? 0,
        Options = model is null ? source.Options with { Enabled = false } : source.Options,
    };

    private async Task ApplyModelAsync(PhotoIndexSettings replacement, CancellationToken cancellationToken)
    {
        await StopIndexingAsync().ConfigureAwait(false);
        await searches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inference.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var indexDatabase = replacement.ModelId is null ? null : await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
                SaveSettings(replacement);
                try
                {
                    if (replacement.ModelId is not null)
                        await indexDatabase!.SetModelAsync(replacement.ModelId, replacement.EmbeddingDimensions, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    SaveSettings(settings);
                    throw;
                }
                await DisposeEncodersAsync().ConfigureAwait(false);
                settings = replacement;
                snapshots.Clear();
                var count = settings.ModelId is null ? 0 : await indexDatabase!.CountAsync(settings.ModelId, cancellationToken).ConfigureAwait(false);
                Update(current => current with
                {
                    Options = settings.Options,
                    ModelId = settings.ModelId,
                    ModelName = settings.ModelName,
                    ModelAvailable = settings.ModelId is not null,
                    Models = InstalledModels(),
                    ActiveDeviceId = settings.Options.DeviceId,
                    IndexedCount = count,
                    ProcessedCount = 0,
                    DiscoveredCount = 0,
                    FailedCount = 0,
                    Error = null,
                });
            }
            finally
            {
                inference.Release();
            }
        }
        finally
        {
            searches.Release();
        }
    }

    private bool ModelExists(StoredEmbeddingModel model) => model.Files.All(name => File.Exists(Path.Combine(ModelDirectory(model.Id), name)));

    private InferenceInstalledModel[] InstalledModels() => settings.Models.Where(ModelExists)
        .Select(model => new InferenceInstalledModel(model.Id, model.Name, model.CatalogId,
            provider.IsModelSupported(model.FileName, settings.Options.DeviceId))).ToArray();

    public async Task ConfigureAsync(PhotoIndexOptions options, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelAvailable = Status.RuntimeAvailable && settings.ModelId is not null &&
                settings.ModelFiles.All(name => File.Exists(Path.Combine(ModelDirectory(settings.ModelId), name))) &&
                provider.IsModelSupported(settings.ModelFileName, options.DeviceId);
            if (options.Enabled && !modelAvailable)
            {
                if (options.DeviceId == settings.Options.DeviceId)
                    throw new InvalidOperationException("Select a compatible photo model before enabling photo indexing.");
                options = options with { Enabled = false };
            }
            if (!Status.Devices.Any(device => device.Id == options.DeviceId) && options.DeviceId != settings.Options.DeviceId)
                throw new ArgumentException("Select an available photo indexing device.", nameof(options));
            await StopIndexingAsync().ConfigureAwait(false);
            await searches.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await inference.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var replacement = settings with { Options = options };
                    SaveSettings(replacement);
                    settings = replacement;
                    await DisposeEncodersAsync().ConfigureAwait(false);
                    snapshots.Clear();
                    Update(current => current with
                    {
                        Options = options,
                        ActiveDeviceId = options.DeviceId,
                        ModelAvailable = modelAvailable,
                        Models = InstalledModels(),
                        Error = null,
                    });
                }
                finally
                {
                    inference.Release();
                }
            }
            finally
            {
                searches.Release();
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task IndexAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        Task work;
        TaskCompletionSource? completion = null;
        CancellationToken indexingToken = default;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!Status.IsAvailable || disposed)
                    return;
                if (!indexingTask.IsCompleted)
                    work = indexingTask;
                else
                {
                    indexingCancellation?.Dispose();
                    indexingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    indexingToken = indexingCancellation.Token;
                    status = status with { IsIndexing = true, ProcessedCount = 0, DiscoveredCount = 0, FailedCount = 0, Error = null };
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    work = indexingTask = completion.Task;
                }
            }
        }
        finally
        {
            lifecycle.Release();
        }
        if (completion is not null)
            _ = CompleteIndexAsync(completion, indexingToken);
        Changed?.Invoke(this, EventArgs.Empty);
        await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteIndexAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            await IndexCoreAsync(cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    public void CancelIndexing()
    {
        lock (sync)
            indexingCancellation?.Cancel();
    }

    private async Task StopIndexingAsync()
    {
        Task work;
        lock (sync)
        {
            indexingCancellation?.Cancel();
            work = indexingTask;
        }
        await work.ConfigureAwait(false);
    }

    private async Task IndexCoreAsync(CancellationToken cancellationToken)
    {
        var modelId = settings.ModelId!;
        var processed = 0;
        var discovered = 0;
        var failed = 0;
        var progress = Stopwatch.StartNew();
        try
        {
            var indexDatabase = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var roots = locations.GetVideoFolderPaths();
            var systemPhotos = UseSystemPhotos
                ? await nativeLibrary!.GetPhotosAsync(cancellationToken).ConfigureAwait(false) : null;
            var retainedRoots = settingsStore?.VideoFolderPaths ?? roots;
            await indexDatabase.RemoveOtherRootsAsync(nativeLibrary?.SupportsPhotosAndVideos == true
                ? retainedRoots.Append(SystemPhotoRoot).ToArray() : retainedRoots, cancellationToken).ConfigureAwait(false);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var run = Guid.NewGuid().ToString("N");
            var constrained = OperatingSystem.IsAndroid() || GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 8L * 1024 * 1024 * 1024;
            var workers = OperatingSystem.IsBrowser() ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, constrained ? 2 : 8);
            var batchSize = constrained ? 2 : settings.Options.DeviceId == provider.DefaultDeviceId ? 4 : 8;
            var completedRoots = new List<string>();
            var pending = new List<PreparedPhoto>(batchSize);
            using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = pipelineCancellation.Token;
            var prepared = Channel.CreateBounded<PreparedPhoto>(new BoundedChannelOptions(batchSize)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            AppLog.Information("PhotoIndex", $"Indexing with {workers} photo preparation workers and image batches of {batchSize}.");
            var preparation = OperatingSystem.IsBrowser() ? PrepareAsync() : Task.Run(PrepareAsync);
            async Task PrepareAsync()
            {
                try
                {
                    if (OperatingSystem.IsBrowser())
                    {
                        await foreach (var source in EnumeratePhotosAsync(token).ConfigureAwait(false))
                            await PreparePhotoAsync(source, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await Parallel.ForEachAsync(EnumeratePhotosAsync(token), new ParallelOptions
                        {
                            MaxDegreeOfParallelism = workers,
                            CancellationToken = token,
                        }, PreparePhotoAsync).ConfigureAwait(false);
                    }
                    prepared.Writer.TryComplete();
                }
                catch (Exception exception)
                {
                    prepared.Writer.TryComplete(exception);
                    throw;
                }
            }

            async ValueTask PreparePhotoAsync((IFile File, string Root) source, CancellationToken preparationToken)
            {
                var file = source.File;
                var metadata = new PhotoMetadata
                {
                    Path = file.Path,
                    Root = source.Root,
                    FileName = file.Name,
                    SizeInBytes = file.SizeInBytes,
                    LastModifiedUtc = file.LastModifiedUtc,
                    CreatedUtc = file.CreatedUtc,
                    Format = Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant(),
                };
                PreparedPhoto result;
                try
                {
                    if (await indexDatabase.MarkUnchangedAsync(file.Path, source.Root, file.SizeInBytes, file.LastModifiedUtc, modelId, run, preparationToken).ConfigureAwait(false))
                    {
                        result = new PreparedPhoto(metadata, null, null);
                    }
                    else
                    {
                        if (!OperatingSystem.IsBrowser())
                        {
                            using var stream = await platform.OpenMediaFileAsync(file.Path, preparationToken).ConfigureAwait(false);
                            metadata = PhotoMetadataReader.Read(stream, metadata);
                        }
                        else
                        {
                            await using var stream = await platform.OpenMediaFileAsync(file.Path, preparationToken).ConfigureAwait(false);
                            metadata = await PhotoMetadataReader.ReadAsync(stream, metadata, preparationToken).ConfigureAwait(false);
                            if (await platform.ProbeImageAsync(file.Path, preparationToken).ConfigureAwait(false) is { } imageInfo)
                                metadata = metadata with { Width = imageInfo.Width, Height = imageInfo.Height };
                        }
                        if (Path.IsPathFullyQualified(file.Path))
                            metadata = metadata with { CreatedUtc = File.GetCreationTimeUtc(file.Path) };
                        var image = await PhotoFolderService.DecodeIndexImageAsync(platform, file.Path, preparationToken).ConfigureAwait(false)
                            ?? throw new InvalidDataException("The photo could not be decoded.");
                        if (metadata.Width <= 0 || metadata.Height <= 0)
                            metadata = metadata with { Width = image.Width, Height = image.Height };
                        result = new PreparedPhoto(metadata, image, null);
                    }
                }
                catch (OperationCanceledException) when (preparationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result = new PreparedPhoto(metadata, null, exception);
                }
                await prepared.Writer.WriteAsync(result, preparationToken).ConfigureAwait(false);
            }
            try
            {
                await foreach (var photo in prepared.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (photo.Image is not null)
                    {
                        pending.Add(photo);
                        if (pending.Count == batchSize)
                            await FlushBatchAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        if (photo.Error is not null)
                            await RecordFailureAsync(photo.Metadata, photo.Error).ConfigureAwait(false);
                        processed++;
                        await ReportProgressAsync().ConfigureAwait(false);
                    }
                }
                await FlushBatchAsync().ConfigureAwait(false);
                await preparation.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                foreach (var root in completedRoots)
                    await indexDatabase.RemoveMissingAsync(root, run, token).ConfigureAwait(false);
            }
            finally
            {
                pipelineCancellation.Cancel();
                try
                {
                    await preparation.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
            }

            async IAsyncEnumerable<(IFile File, string Root)> EnumeratePhotosAsync([EnumeratorCancellation] CancellationToken enumerationToken)
            {
                if (systemPhotos is not null)
                {
                    foreach (var photo in systemPhotos)
                    {
                        enumerationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref discovered);
                        yield return (new SystemPhotoFile(photo), SystemPhotoRoot);
                    }
                    completedRoots.Add(SystemPhotoRoot);
                }

                foreach (var root in roots)
                {
                    var complete = true;
                    var folders = new Stack<string>();
                    folders.Push(root);
                    while (folders.TryPop(out var folder))
                    {
                        enumerationToken.ThrowIfCancellationRequested();
                        if (!visited.Add(folder))
                            continue;
                        IFile[] files;
                        try
                        {
                            if (!await platform.FolderExistsAsync(folder, enumerationToken).ConfigureAwait(false))
                                throw new DirectoryNotFoundException(folder);
                            files = await platform.ListFilesFullNameInPathAsync(folder).ConfigureAwait(false);
                            foreach (var child in await platform.ListFoldersFullNameInPathAsync(folder).ConfigureAwait(false))
                            {
                                if (Path.IsPathFullyQualified(child) && new DirectoryInfo(child).LinkTarget is not null)
                                    continue;
                                folders.Push(child);
                            }
                        }
                        catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
                        {
                            AppLog.Warning("PhotoIndex", $"Skipping inaccessible photo folder {folder}.", exception);
                            continue;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            complete = false;
                            Interlocked.Increment(ref failed);
                            AppLog.Warning("PhotoIndex", $"Could not enumerate photo folder {folder}.", exception);
                            continue;
                        }
                        foreach (var file in files.Where(file => MediaContainerFormats.IsImage(file.Name)))
                        {
                            Interlocked.Increment(ref discovered);
                            yield return (file, root);
                        }
                    }
                    if (complete)
                        completedRoots.Add(root);
                }
            }

            async Task FlushBatchAsync()
            {
                if (pending.Count == 0)
                    return;
                float[][]? embeddings = null;
                try
                {
                    embeddings = await EncodeAsync(current => current.EncodeImagesAsync(
                        pending.Select(photo => photo.Image!).ToArray(), token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AppLog.Warning("PhotoIndex", "Photo batch inference failed; retrying the photos individually.", exception);
                }
                for (var index = 0; index < pending.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var photo = pending[index];
                    var metadata = photo.Metadata;
                    try
                    {
                        var vector = embeddings is not null ? embeddings[index] :
                            await EncodeAsync(current => current.EncodeImageAsync(photo.Image!, token), token).ConfigureAwait(false);
                        var currentFile = await platform.GetMediaFilePropertiesAsync(metadata.Path, token).ConfigureAwait(false);
                        if (currentFile is null || currentFile.Length != metadata.SizeInBytes || currentFile.LastModifiedUtc != metadata.LastModifiedUtc)
                            throw new IOException("The photo changed during indexing; it will be retried on the next scan.");
                        await indexDatabase.UpsertAsync(metadata, vector, modelId, run, null, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        await RecordFailureAsync(metadata, exception).ConfigureAwait(false);
                    }
                    processed++;
                    await ReportProgressAsync().ConfigureAwait(false);
                }
                pending.Clear();
            }

            async Task RecordFailureAsync(PhotoMetadata metadata, Exception exception)
            {
                if (FileAccessErrors.IsAccessDenied(exception))
                {
                    AppLog.Warning("PhotoIndex", $"Skipping inaccessible photo {metadata.Path}.", exception);
                    return;
                }
                Interlocked.Increment(ref failed);
                AppLog.Warning("PhotoIndex", $"Could not index photo {metadata.Path}.", exception);
                await indexDatabase.UpsertAsync(metadata, null, modelId, run, exception.ToString(), token).ConfigureAwait(false);
            }

            async Task ReportProgressAsync()
            {
                if (progress.ElapsedMilliseconds >= 250)
                {
                    var count = await indexDatabase.CountAsync(modelId, token).ConfigureAwait(false);
                    Update(current => current with
                    {
                        ProcessedCount = processed,
                        DiscoveredCount = Volatile.Read(ref discovered),
                        FailedCount = Volatile.Read(ref failed),
                        IndexedCount = count,
                    });
                    progress.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("PhotoIndex", "Photo indexing stopped after an error.", exception);
            Update(current => current with { Error = exception.Message });
        }
        finally
        {
            await searches.WaitAsync().ConfigureAwait(false);
            try
            {
                snapshots.Clear();
            }
            finally
            {
                searches.Release();
            }
            var count = Status.IndexedCount;
            try
            {
                count = database is null ? 0 : await database.CountAsync(modelId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Warning("PhotoIndex", "Could not read the final photo index count.", exception);
            }
            Update(current => current with
            {
                IsIndexing = false,
                ProcessedCount = processed,
                DiscoveredCount = discovered,
                FailedCount = failed,
                IndexedCount = count,
            });
        }
    }

    private async Task<TResult> EncodeAsync<TResult>(Func<IEmbeddingInferenceSession, ValueTask<TResult>> encode, CancellationToken cancellationToken)
    {
        await inference.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunInferenceAsync(async () =>
            {
                if (encoder is null)
                {
                    encoder = await provider.CreateSessionAsync(ModelDirectory(settings.ModelId!), settings.ModelFileName,
                        settings.Options.DeviceId, cancellationToken).ConfigureAwait(false);
                    if (encoder.EmbeddingDimensions != settings.EmbeddingDimensions)
                    {
                        encoder.Dispose();
                        encoder = null;
                        throw new InvalidDataException("The photo model embedding dimensions changed; import the model again.");
                    }
                }
                try
                {
                    return await encode(encoder).ConfigureAwait(false);
                }
                finally
                {
                    if (Status.ActiveDeviceId != encoder.ActiveDeviceId)
                        Update(current => current with { ActiveDeviceId = encoder.ActiveDeviceId });
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inference.Release();
        }
    }

    private async Task<float[]> EncodeTextAsync(string query, CancellationToken cancellationToken)
    {
        var vector = await RunInferenceAsync(async () =>
        {
            textEncoder ??= await provider.CreateIndependentTextSessionAsync(ModelDirectory(settings.ModelId!), settings.ModelFileName,
                cancellationToken).ConfigureAwait(false);
            if (textEncoder is null)
                return null;
            if (textEncoder.EmbeddingDimensions != settings.EmbeddingDimensions)
            {
                textEncoder.Dispose();
                textEncoder = null;
                throw new InvalidDataException("The photo model embedding dimensions changed; import the model again.");
            }
            return await textEncoder.EncodeTextAsync(query, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return vector ?? await EncodeAsync(current => current.EncodeTextAsync(query, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<PhotoSearchPage> SearchAsync(string query, int pageSize = 20, string? cursor = null,
        CancellationToken cancellationToken = default) => SearchCoreAsync(query, pageSize, cursor, null, cancellationToken);

    private static Task<TResult> RunInferenceAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken) =>
        OperatingSystem.IsBrowser() ? operation() : Task.Run(operation, cancellationToken);

    private async Task<PhotoSearchPage> SearchCoreAsync(string query, int pageSize, string? cursor,
        LibrarySourceId? sourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (pageSize is < 1 or > 100 || query.Length > 2048)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Photo searches accept 1-100 results per page and up to 2048 query characters.");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!Status.IsAvailable)
            return new PhotoSearchPage([], null, false);
        query = query.Trim();
        if (query.Length == 0)
            return new PhotoSearchPage([], null, true);
        await searches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Status.IsAvailable)
                return new PhotoSearchPage([], null, false);
            var modelId = settings.ModelId!;
            var generation = Volatile.Read(ref catalogGeneration);
            SearchSnapshot snapshot;
            var offset = 0;
            string key;
            if (cursor is not null)
            {
                var separator = cursor.IndexOf('.');
                if (separator != 32 || !Guid.TryParseExact(cursor[..separator], "N", out _) ||
                    !int.TryParse(cursor.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset is < 1 or > 100000)
                    throw new ArgumentException("Invalid photo search cursor.", nameof(cursor));
                key = cursor[..separator];
                if (!snapshots.TryGetValue(key, out snapshot!) || snapshot.Query != query || snapshot.ModelId != modelId ||
                    snapshot.SourceId != sourceId || snapshot.Generation != generation ||
                    DateTime.UtcNow - snapshot.CreatedUtc > TimeSpan.FromMinutes(10))
                    return new PhotoSearchPage([], null, true, RestartRequired: true);
            }
            else
            {
                var vector = await EncodeTextAsync(query, cancellationToken).ConfigureAwait(false);
                var indexDatabase = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
                var roots = sourceId is null || sourceId == LibrarySourceId.Local
                    ? locations.GetVideoFolderPaths().ToList() : [];
                HashSet<string>? systemPaths = null;
                if (UseSystemPhotos && (sourceId is null || sourceId == LibrarySourceId.System))
                {
                    roots.Add(SystemPhotoRoot);
                    systemPaths = (await nativeLibrary!.GetPhotosAsync(cancellationToken).ConfigureAwait(false))
                        .Select(photo => photo.Path).ToHashSet(StringComparer.Ordinal);
                }
                var ranked = await indexDatabase.RankAsync(vector, modelId, roots,
                    (path, root) => root != SystemPhotoRoot || systemPaths?.Contains(path) == true, cancellationToken).ConfigureAwait(false);
                if (settings.ModelId != modelId || !Status.IsAvailable || generation != Volatile.Read(ref catalogGeneration))
                    return new PhotoSearchPage([], null, Status.IsAvailable, RestartRequired: true);
                key = Guid.NewGuid().ToString("N");
                snapshot = new SearchSnapshot(query, modelId, ranked, DateTime.UtcNow, sourceId, generation);
                foreach (var expired in snapshots.Where(pair => DateTime.UtcNow - pair.Value.CreatedUtc > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray())
                    snapshots.Remove(expired);
                while (snapshots.Count >= 4)
                    snapshots.Remove(snapshots.MinBy(pair => pair.Value.CreatedUtc).Key);
                snapshots[key] = snapshot;
            }
            var items = await database!.ReadPageAsync(snapshot.Ranked, offset, pageSize, modelId, cancellationToken).ConfigureAwait(false);
            var next = offset + pageSize;
            return new PhotoSearchPage(items, next < snapshot.Ranked.Count ? key + "." + next.ToString(CultureInfo.InvariantCulture) : null, true);
        }
        finally
        {
            searches.Release();
        }
    }

    private string ModelDirectory(string modelId) => Path.Combine(dataDirectory, "Models", "Photos", modelId);

    private static bool IsModelFileName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0 && name == Path.GetFileName(name);

    private async Task<PhotoIndexDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        await platform.PrepareLibraryDatabaseAsync().ConfigureAwait(false);
        PhotoIndexDatabase current;
        lock (sync)
        {
            current = database ??= new PhotoIndexDatabase(platform.LocalDataPath);
        }
        await current.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (settings.ModelId is not null)
            await current.SetModelAsync(settings.ModelId, settings.EmbeddingDimensions, cancellationToken).ConfigureAwait(false);
        return current;
    }

    private PhotoIndexSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath))
                return new PhotoIndexSettings { Options = new PhotoIndexOptions(DeviceId: provider.DefaultDeviceId) };
            var saved = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), PhotoIndexJsonContext.Default.PhotoIndexSettings)
                ?? new PhotoIndexSettings { Options = new PhotoIndexOptions(DeviceId: provider.DefaultDeviceId) };
            if (saved.Options is null || string.IsNullOrWhiteSpace(saved.Options.DeviceId))
                throw new InvalidDataException("Invalid stored photo indexing options.");
            if (saved.ModelId is not null && (saved.ModelId.Length != 64 || saved.ModelId.Any(character => !Uri.IsHexDigit(character))))
                throw new InvalidDataException("Invalid stored photo model identity.");
            if (saved.ModelId is not null && (saved.ModelFiles is not { Length: > 0 } ||
                saved.ModelFiles.Any(name => !IsModelFileName(name)) || !IsModelFileName(saved.ModelFileName) ||
                !saved.ModelFiles.Contains(saved.ModelFileName, StringComparer.Ordinal) || saved.EmbeddingDimensions is < 1 or > 8192))
                throw new InvalidDataException("Invalid stored photo model configuration.");
            if (saved.Models is null)
                saved = saved with { Models = [] };
            if (saved.Models.Any(model => model is null ||
                model.Id is not { Length: 64 } || model.Id.Any(character => !Uri.IsHexDigit(character)) ||
                string.IsNullOrWhiteSpace(model.Name) || !IsModelFileName(model.FileName) || model.Files is not { Length: > 0 } ||
                model.Files.Any(name => !IsModelFileName(name)) || !model.Files.Contains(model.FileName, StringComparer.Ordinal) ||
                model.EmbeddingDimensions is < 1 or > 8192) || saved.Models.Select(model => model.Id).Distinct().Count() != saved.Models.Length)
                throw new InvalidDataException("Invalid stored photo model library.");
            if (saved.ModelId is not null && !saved.Models.Any(model => model.Id == saved.ModelId))
                saved = saved with
                {
                    Models = [.. saved.Models, new StoredEmbeddingModel(saved.ModelId, saved.ModelName ?? saved.ModelFileName,
                    saved.ModelFileName, saved.ModelFiles, saved.EmbeddingDimensions)]
                };
            return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppLog.Warning("PhotoIndex", "Could not load photo indexing settings.", exception);
            return new PhotoIndexSettings { Options = new PhotoIndexOptions(DeviceId: provider.DefaultDeviceId) };
        }
    }

    private void SaveSettings(PhotoIndexSettings replacement)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(replacement, PhotoIndexJsonContext.Default.PhotoIndexSettings));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void Update(Func<PhotoIndexStatus, PhotoIndexStatus> change)
    {
        lock (sync)
            Volatile.Write(ref status, change(status));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task DisposeEncodersAsync()
    {
        var previousEncoder = encoder;
        var previousTextEncoder = textEncoder;
        encoder = null;
        textEncoder = null;
        try
        {
            if (previousEncoder is IAsyncDisposable asynchronous)
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            else
                previousEncoder?.Dispose();
        }
        finally
        {
            if (previousTextEncoder is IAsyncDisposable asynchronous)
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            else
                previousTextEncoder?.Dispose();
        }
    }

    public void Dispose() => _ = ObserveDisposalAsync();

    private async Task ObserveDisposalAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { AppLog.Warning("PhotoIndex", "Could not dispose photo indexing services.", exception); }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposed = true;
            return new ValueTask(disposalTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (nativeLibrary is not null)
        {
            nativeLibrary.CatalogChanged -= OnLibraryChanged;
            if (settingsStore is not null)
                settingsStore.NativeLibraryChanged -= OnLibraryChanged;
        }
        downloads.Changed -= OnDownloadsChanged;
        CancelIndexing();
        if (downloads is IAsyncDisposable asynchronous)
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        else
            downloads.Dispose();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        await indexingTask.ConfigureAwait(false);
        await searches.WaitAsync().ConfigureAwait(false);
        await inference.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeEncodersAsync().ConfigureAwait(false);
            if (database is not null)
                await database.DisposeAsync().ConfigureAwait(false);
            indexingCancellation?.Dispose();
        }
        finally
        {
            searches.Release();
            inference.Release();
            lifecycle.Release();
        }
    }

    private sealed record SearchSnapshot(string Query, string ModelId, List<(long Id, double Score)> Ranked,
        DateTime CreatedUtc, LibrarySourceId? SourceId, long Generation);

    private sealed class SourceSearchService(PhotoIndexService owner, LibrarySourceId sourceId) : IPhotoSearchService
    {
        public Task<PhotoSearchPage> SearchAsync(string query, int pageSize = 20, string? cursor = null,
            CancellationToken cancellationToken = default) => owner.SearchCoreAsync(query, pageSize, cursor, sourceId, cancellationToken);
    }

    private sealed class SystemPhotoFile(DbImageFile photo) : IFile
    {
        public string Name => photo.FileName;
        public string Path => photo.Path;
        public DateTime LastModifiedUtc => photo.FileLastModifiedDate.UtcDateTime;
        public long SizeInBytes => photo.FileSize;
        public DateTime? CreatedUtc => photo.FileCreatedDate?.UtcDateTime;
    }

    private sealed record PreparedPhoto(PhotoMetadata Metadata, StillImageThumbnail? Image, Exception? Error);
}

internal sealed record PhotoIndexSettings
{
    public PhotoIndexOptions Options { get; init; } = new();
    public string? ModelId { get; init; }
    public string? ModelName { get; init; }
    public string ModelFileName { get; init; } = string.Empty;
    public string[] ModelFiles { get; init; } = [];
    public int EmbeddingDimensions { get; init; }
    public StoredEmbeddingModel[] Models { get; init; } = [];
}

internal sealed record StoredEmbeddingModel(string Id, string Name, string FileName, string[] Files, int EmbeddingDimensions,
    [property: JsonPropertyName("RepositoryId")] string? CatalogId = null);

[JsonSerializable(typeof(PhotoIndexSettings))]
internal partial class PhotoIndexJsonContext : JsonSerializerContext;