using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.Inference;

internal sealed class LiteRtModelDownloads : IInferenceModelDownloads
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object sync = new();
    private readonly string directory;
    private readonly Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install;
    private InferenceModelDownload status = new(LiteRtEmbeddingModels.Id);
    private CancellationTokenSource? cancellation;
    private Task work = Task.CompletedTask;
    private bool disposed;

    internal LiteRtModelDownloads(string modelDirectory, Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install)
    {
        this.install = install;
        directory = Path.Combine(modelDirectory, ".downloads", "siglip2-litert-" + string.Join("-", LiteRtEmbeddingModels.Files.Select(file => file.Sha256[..8])));
        if (!Directory.Exists(directory))
            return;
        try
        {
            status = status with
            {
                State = InferenceModelDownloadState.Paused,
                ReceivedBytes = LiteRtEmbeddingModels.Files.Sum(ExistingLength),
                TotalBytes = LiteRtEmbeddingModels.Files.Sum(file => file.Size),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("InferenceModels", "Could not restore a partial LiteRT model download.", exception);
            status = status with { State = InferenceModelDownloadState.Failed, Error = exception.Message };
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InferenceModelDownload> Status
    {
        get { lock (sync) return [status]; }
    }

    public Task StartAsync(string modelId, CancellationToken cancellationToken)
    {
        RequireModel(modelId);
        Task pending;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!work.IsCompleted || status.State == InferenceModelDownloadState.Canceling)
                return work;
            cancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cancellation.Token;
            status = status with { State = InferenceModelDownloadState.Downloading, Error = null };
            pending = work = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return pending;
    }

    public void Pause(string modelId)
    {
        RequireModel(modelId);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status.State is InferenceModelDownloadState.Downloading or InferenceModelDownloadState.Verifying)
                cancellation?.Cancel();
        }
    }

    public async Task CancelAsync(string modelId)
    {
        RequireModel(modelId);
        Task pending;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status.State is InferenceModelDownloadState.Installing or InferenceModelDownloadState.Canceling)
                return;
            status = status with { State = InferenceModelDownloadState.Canceling };
            cancellation?.Cancel();
            pending = work;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await pending.ConfigureAwait(false);
        try
        {
            await Task.Run(DeleteDownload).ConfigureAwait(false);
            SetStatus(new InferenceModelDownload(modelId));
        }
        catch (Exception exception)
        {
            AppLog.Error("InferenceModels", "Could not cancel a LiteRT model download.", exception);
            SetStatus(new InferenceModelDownload(modelId, InferenceModelDownloadState.Failed, Error: exception.Message));
            throw;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var total = LiteRtEmbeddingModels.Files.Sum(file => file.Size);
            long completed = 0;
            foreach (var file in LiteRtEmbeddingModels.Files)
            {
                await DownloadFileAsync(file, received => SetStatus(new InferenceModelDownload(LiteRtEmbeddingModels.Id,
                    InferenceModelDownloadState.Downloading, completed + received, total)), cancellationToken).ConfigureAwait(false);
                completed += file.Size;
            }
            SetStatus(new InferenceModelDownload(LiteRtEmbeddingModels.Id, InferenceModelDownloadState.Verifying, total, total));
            foreach (var file in LiteRtEmbeddingModels.Files)
            {
                if (!await file.VerifyAsync(PartialPath(file), cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(PartialPath(file));
                    throw new InvalidDataException($"The integrity check failed for {file.Name}. Resume to download that file again.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus(new InferenceModelDownload(LiteRtEmbeddingModels.Id, InferenceModelDownloadState.Installing, total, total));
            await install(LiteRtEmbeddingModels.Files.Select(file => new InferenceModelFile(file.Name,
                _ => Task.FromResult<Stream>(File.OpenRead(PartialPath(file))), file.Size)).ToArray(), LiteRtEmbeddingModels.Id,
                cancellationToken).ConfigureAwait(false);
            try
            {
                DeleteDownload();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("InferenceModels", "Could not remove completed LiteRT download files.", exception);
            }
            SetStatus(new InferenceModelDownload(LiteRtEmbeddingModels.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (sync)
                status = status with { State = status.State == InferenceModelDownloadState.Canceling ? status.State : InferenceModelDownloadState.Paused };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Error("InferenceModels", "LiteRT model download failed.", exception);
            SetStatus(new InferenceModelDownload(LiteRtEmbeddingModels.Id, InferenceModelDownloadState.Failed, Error: exception.Message));
        }
    }

    private async Task DownloadFileAsync(LiteRtModelFile file, Action<long> progress, CancellationToken cancellationToken)
    {
        var offset = ExistingLength(file);
        progress(offset);
        if (offset == file.Size)
            return;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
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
        await using var destination = new FileStream(PartialPath(file), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 131072, true);
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

    private string PartialPath(LiteRtModelFile file) => Path.Combine(directory, file.Name + ".partial");

    private long ExistingLength(LiteRtModelFile file)
    {
        var info = new FileInfo(PartialPath(file));
        return info.Exists && info.Length <= file.Size ? info.Length : 0;
    }

    private static void RequireModel(string modelId)
    {
        if (modelId != LiteRtEmbeddingModels.Id)
            throw new ArgumentException("Select a downloadable LiteRT embedding model.", nameof(modelId));
    }

    private void DeleteDownload()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private void SetStatus(InferenceModelDownload replacement)
    {
        lock (sync)
        {
            if (status.State == InferenceModelDownloadState.Canceling && !work.IsCompleted)
                return;
            status = replacement;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Task pending;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            cancellation?.Cancel();
            pending = work;
        }
        pending.GetAwaiter().GetResult();
        cancellation?.Dispose();
        client.Dispose();
    }
}