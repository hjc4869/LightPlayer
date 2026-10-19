using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.Inference;

internal sealed class OnnxModelDownloads : IInferenceModelDownloads, IAsyncDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object sync = new();
    private readonly Dictionary<string, DownloadJob> jobs = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install;
    private readonly Func<string, ModelDownloadFile, CancellationToken, Task<bool>> verifyFile;
    private bool disposed;
    private Task? disposalTask;

    internal OnnxModelDownloads(string modelDirectory, Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install,
        IReadOnlyList<OnnxModelPackage>? definitions = null,
        Func<string, ModelDownloadFile, CancellationToken, Task<bool>>? verifyFile = null)
    {
        this.install = install;
        this.verifyFile = verifyFile ?? VerifyAsync;
        definitions ??= OnnxEmbeddingModels.All.Select(model => new OnnxModelPackage(model.Id, model.RepositoryId, model.ModelPath,
            OnnxEmbeddingModels.CompanionNames(model.ImageProcessorType), true)).ToArray();
        foreach (var definition in definitions)
        {
            var cacheName = definition.RepositoryId.Split('/')[1];
            if (definition.Id != definition.RepositoryId)
                cacheName += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.Id)))[..16];
            var job = new DownloadJob(definition, Path.Combine(modelDirectory, ".downloads", cacheName));
            jobs.Add(definition.Id, job);
            if (!Directory.Exists(job.Directory))
                continue;
            try
            {
                var manifest = ReadManifest(job);
                job.Status = job.Status with
                {
                    State = InferenceModelDownloadState.Paused,
                    ReceivedBytes = manifest?.Files.Sum(file => ExistingLength(job, file)) ?? 0,
                    TotalBytes = manifest?.Files.Sum(file => file.Size) ?? 0,
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                AppLog.Warning("InferenceModels", "Could not restore a partial model download.", exception);
                job.Status = job.Status with { State = InferenceModelDownloadState.Failed, Error = exception.Message };
            }
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InferenceModelDownload> Status
    {
        get { lock (sync) return jobs.Values.Select(job => job.Status).ToArray(); }
    }

    public Task StartAsync(string modelId, CancellationToken cancellationToken)
    {
        Task work;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var job = jobs[modelId];
            if (!job.Work.IsCompleted || job.Status.State == InferenceModelDownloadState.Canceling)
                return job.Work;
            job.Cancellation?.Dispose();
            job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            job.Status = job.Status with { State = InferenceModelDownloadState.Downloading, Error = null };
            work = job.Work = Task.Run(() => RunAsync(job, job.Cancellation.Token), CancellationToken.None);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return work;
    }

    public void Pause(string modelId)
    {
        lock (sync)
        {
            var job = jobs[modelId];
            if (job.Status.State is InferenceModelDownloadState.Downloading or InferenceModelDownloadState.Verifying)
                job.Cancellation?.Cancel();
        }
    }

    public async Task CancelAsync(string modelId)
    {
        DownloadJob job;
        Task work;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            job = jobs[modelId];
            if (job.Status.State is InferenceModelDownloadState.Installing or InferenceModelDownloadState.Canceling)
                return;
            job.Status = job.Status with { State = InferenceModelDownloadState.Canceling };
            job.Cancellation?.Cancel();
            work = job.Work;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await work.ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(job.Directory))
                    Directory.Delete(job.Directory, recursive: true);
            }).ConfigureAwait(false);
            SetStatus(job, new InferenceModelDownload(modelId));
        }
        catch (Exception exception)
        {
            AppLog.Error("InferenceModels", "Could not cancel a model download.", exception);
            SetStatus(job, job.Status with { State = InferenceModelDownloadState.Failed, Error = exception.Message });
            throw;
        }
    }

    private async Task RunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(job.Directory);
            var manifest = ReadManifest(job) ?? await FetchManifestAsync(job.Definition, "main", cancellationToken).ConfigureAwait(false);
            var manifestPath = Path.Combine(job.Directory, "download.json");
            var temporary = manifestPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, ModelDownloadJsonContext.Default.ModelDownloadManifest),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, manifestPath, overwrite: true);
            var total = manifest.Files.Sum(file => file.Size);
            long completed = 0;
            foreach (var file in manifest.Files)
            {
                await DownloadFileAsync(job, manifest.Revision, file, received => SetStatus(job,
                    new InferenceModelDownload(job.Definition.Id, InferenceModelDownloadState.Downloading, completed + received, total)),
                    cancellationToken).ConfigureAwait(false);
                completed += file.Size;
            }
            SetStatus(job, new InferenceModelDownload(job.Definition.Id, InferenceModelDownloadState.Verifying, total, total));
            var verified = await FetchManifestAsync(job.Definition, manifest.Revision, cancellationToken).ConfigureAwait(false);
            if (verified.Revision != manifest.Revision || !verified.Files.SequenceEqual(manifest.Files))
                throw new InvalidDataException("The model repository metadata changed. Cancel this download and try again.");
            foreach (var file in verified.Files)
            {
                if (!await verifyFile(PartialPath(job, file), file, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(PartialPath(job, file));
                    throw new InvalidDataException($"The integrity check failed for {file.Path}. Resume to download that file again.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus(job, new InferenceModelDownload(job.Definition.Id, InferenceModelDownloadState.Installing, total, total));
            await install(verified.Files.Select(file => new InferenceModelFile(Path.GetFileName(file.Path),
                _ => Task.FromResult<Stream>(File.OpenRead(PartialPath(job, file))))).ToArray(), job.Definition.Id,
                cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Delete(job.Directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("InferenceModels", "Could not remove completed download files.", exception);
            }
            SetStatus(job, new InferenceModelDownload(job.Definition.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(job, job.Status with { State = InferenceModelDownloadState.Paused });
        }
        catch (Exception exception)
        {
            AppLog.Error("InferenceModels", $"Model download failed: {job.Definition.Id}.", exception);
            SetStatus(job, job.Status with { State = InferenceModelDownloadState.Failed, Error = exception.Message });
        }
    }

    private async Task DownloadFileAsync(DownloadJob job, string revision, ModelDownloadFile file, Action<long> progress,
        CancellationToken cancellationToken)
    {
        var path = PartialPath(job, file);
        var offset = ExistingLength(job, file);
        progress(offset);
        if (offset == file.Size)
            return;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://huggingface.co/{job.Definition.RepositoryId}/resolve/{revision}/{file.Path}");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != offset || range.To != file.Size - 1 || range.Length != file.Size)
                throw new InvalidDataException("The model server returned an invalid byte range.");
        }
        else if (response.StatusCode == HttpStatusCode.OK)
            offset = 0;
        else
            throw new InvalidDataException("The model server returned an unexpected response.");
        if (response.Content.Headers.ContentLength is { } length && length != file.Size - offset)
            throw new InvalidDataException("The model server returned an unexpected file size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 131072, true);
        destination.SetLength(offset);
        destination.Position = offset;
        var buffer = new byte[131072];
        var clock = Stopwatch.StartNew();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromMinutes(2));
            var count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0)
                break;
            if (offset + count > file.Size)
                throw new InvalidDataException("The model download exceeds its expected size.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            offset += count;
            if (clock.ElapsedMilliseconds >= 150)
            {
                progress(offset);
                clock.Restart();
            }
        }
        if (offset != file.Size)
            throw new EndOfStreamException("The model download was interrupted. Resume to continue.");
        progress(offset);
    }

    private async Task<ModelDownloadManifest> FetchManifestAsync(OnnxModelPackage definition, string revision,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"https://huggingface.co/api/models/{definition.RepositoryId}/revision/{revision}?blobs=true",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var files = new List<ModelDownloadFile>();
        foreach (var entry in json.RootElement.GetProperty("siblings").EnumerateArray())
        {
            var path = entry.GetProperty("rfilename").GetString()!;
            if (!IsRequiredPath(definition, path))
                continue;
            var isLfs = entry.TryGetProperty("lfs", out var lfs);
            files.Add(new ModelDownloadFile(path, entry.GetProperty("size").GetInt64(),
                (isLfs ? lfs.GetProperty("sha256") : entry.GetProperty("blobId")).GetString()!.ToLowerInvariant(), isLfs));
        }
        var manifest = new ModelDownloadManifest(definition.RepositoryId, json.RootElement.GetProperty("sha").GetString()!,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        ValidateManifest(definition, manifest);
        return manifest;
    }

    private static bool IsRequiredPath(OnnxModelPackage definition, string path) =>
        definition.CompanionNames.Contains(path, StringComparer.Ordinal) ||
        path == definition.ModelPath || definition.AllowExternalData && path.LastIndexOf('/') == definition.ModelPath.LastIndexOf('/') &&
        OnnxEmbeddingModels.IsExternalDataFile(definition.ModelPath, path);

    private static void ValidateManifest(OnnxModelPackage definition, ModelDownloadManifest manifest)
    {
        if (manifest.RepositoryId != definition.RepositoryId || !IsHash(manifest.Revision, 40) || manifest.Files is not { Length: > 0 and <= 16 } ||
            manifest.Files.Any(file => file is null || !IsRequiredPath(definition, file.Path) ||
                file.Size <= 0 || file.Size > (file.Path == definition.ModelPath || OnnxEmbeddingModels.IsExternalDataFile(definition.ModelPath, file.Path)
                    ? 16L * 1024 * 1024 * 1024 : 64L * 1024 * 1024) ||
                !IsHash(file.Hash, file.IsLfs ? 64 : 40)) || manifest.Files.Select(file => file.Path).Distinct().Count() != manifest.Files.Length ||
            manifest.Files.Select(file => Path.GetFileName(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            !manifest.Files.Any(file => file.Path == definition.ModelPath) ||
            definition.CompanionNames.Any(name => !manifest.Files.Any(file => file.Path == name)))
            throw new InvalidDataException("Invalid inference model download metadata.");
    }

    private static bool IsHash(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);

    private static ModelDownloadManifest? ReadManifest(DownloadJob job)
    {
        var path = Path.Combine(job.Directory, "download.json");
        if (!File.Exists(path))
            return null;
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), ModelDownloadJsonContext.Default.ModelDownloadManifest)
            ?? throw new InvalidDataException("Invalid inference model download metadata.");
        ValidateManifest(job.Definition, manifest);
        return manifest;
    }

    private static string PartialPath(DownloadJob job, ModelDownloadFile file) => Path.Combine(job.Directory, Path.GetFileName(file.Path) + ".partial");

    private static long ExistingLength(DownloadJob job, ModelDownloadFile file)
    {
        var info = new FileInfo(PartialPath(job, file));
        return info.Exists && info.Length <= file.Size ? info.Length : 0;
    }

    private static async Task<bool> VerifyAsync(string path, ModelDownloadFile file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != file.Size)
            return false;
        using var hash = IncrementalHash.CreateHash(file.IsLfs ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        if (!file.IsLfs)
            hash.AppendData(Encoding.ASCII.GetBytes("blob " + file.Size.ToString(CultureInfo.InvariantCulture) + "\0"));
        var buffer = new byte[131072];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            hash.AppendData(buffer.AsSpan(0, count));
        return Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(DownloadJob job, InferenceModelDownload replacement)
    {
        lock (sync)
        {
            if (job.Status.State == InferenceModelDownloadState.Canceling && !job.Work.IsCompleted)
                return;
            job.Status = replacement;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsBrowser())
            _ = ObserveDisposalAsync();
        else
            DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ObserveDisposalAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { AppLog.Warning("InferenceModels", "Could not dispose model downloads.", exception); }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposalTask is not null)
                return new ValueTask(disposalTask);
            disposed = true;
            foreach (var job in jobs.Values)
                job.Cancellation?.Cancel();
            return new ValueTask(disposalTask = DisposeCoreAsync(jobs.Values.Select(job => job.Work).ToArray()));
        }
    }

    private async Task DisposeCoreAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        finally
        {
            foreach (var job in jobs.Values)
                job.Cancellation?.Dispose();
            client.Dispose();
        }
    }

    private sealed class DownloadJob(OnnxModelPackage definition, string directory)
    {
        internal OnnxModelPackage Definition { get; } = definition;
        internal string Directory { get; } = directory;
        internal InferenceModelDownload Status { get; set; } = new(definition.Id);
        internal CancellationTokenSource? Cancellation { get; set; }
        internal Task Work { get; set; } = Task.CompletedTask;
    }
}

internal sealed record OnnxModelPackage(string Id, string RepositoryId, string ModelPath, IReadOnlyList<string> CompanionNames,
    bool AllowExternalData = false);

internal sealed record ModelDownloadFile(string Path, long Size, string Hash, bool IsLfs);
internal sealed record ModelDownloadManifest(string RepositoryId, string Revision, ModelDownloadFile[] Files);

[JsonSerializable(typeof(ModelDownloadManifest))]
internal partial class ModelDownloadJsonContext : JsonSerializerContext;