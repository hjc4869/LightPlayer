using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LocalServices;

public sealed class AudioSeparationService : IAudioSeparationService
{
    private readonly IAudioSeparationInference provider;
    private readonly IInferenceModelDownloads downloads;
    private readonly string modelDirectory;
    private readonly string settingsPath;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object sync = new();
    private readonly ConditionalWeakTable<IAudioSeparationSession, SessionGeneration> sessions = new();
    private int generation;
    private AudioSeparationSettings settings;
    private AudioSeparationStatus status = new();
    private AudioSeparationTrack track;
    private bool initialized;
    private bool disposed;

    public AudioSeparationService(IAudioSeparationInference provider, string? dataDirectory = null)
    {
        this.provider = provider;
        dataDirectory ??= AppPaths.DataDirectory;
        settingsPath = Path.Combine(dataDirectory, "audio-separation.json");
        modelDirectory = Path.Combine(dataDirectory, "Models", "AudioSeparation");
        settings = LoadSettings();
        downloads = provider.CreateDownloads(modelDirectory, (files, catalogId, token) =>
            ImportAsync(files.Single(), catalogId, false, token));
        downloads.Changed += OnDownloadsChanged;
        PublishSettings();
    }

    public event EventHandler? Changed;
    public event EventHandler? PlaybackChanged;
    public AudioSeparationStatus Status => Volatile.Read(ref status);

    public AudioSeparationTrack Track
    {
        get { lock (sync) return track; }
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            lock (sync)
            {
                if (track == value)
                    return;
                track = value;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public InferenceModelCatalog GetModelCatalog(CultureInfo culture) => provider.GetModelCatalog(Status.Options.DeviceId, culture);

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
                var devices = await provider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                if (!devices.Any(device => device.Id == settings.Options.DeviceId))
                    settings = settings with { Options = settings.Options with { DeviceId = "cpu" } };
                Update(current => current with { RuntimeAvailable = true, Devices = devices, Error = null });
                PublishSettings();
                initialized = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task ConfigureAsync(AudioSeparationOptions options, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!Status.Devices.Any(device => device.Id == options.DeviceId))
                throw new ArgumentException("Select an available inference device.", nameof(options));
            if (options.Enabled && !Status.ModelAvailable)
                throw new InvalidOperationException("Select an installed separation model before enabling separation.");
            SaveSettings(settings with { Options = options });
            Update(current => current with { RuntimeAvailable = true, Error = null });
            PublishSettings();
        }
        finally
        {
            lifecycle.Release();
        }
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ImportModelAsync(InferenceModelFile file, CancellationToken cancellationToken = default) =>
        ImportAsync(file, null, true, cancellationToken);

    private async Task ImportAsync(InferenceModelFile file, string? catalogId, bool select, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(modelDirectory, ".import-" + Guid.NewGuid().ToString("N"));
        var playbackChanged = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsFileName(file.Name))
                throw new ArgumentException("Select a single ONNX model file.", nameof(file));
            Directory.CreateDirectory(staging);
            var path = Path.Combine(staging, file.Name);
            using (var source = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (destination.Length + count > Math.Min(file.MaximumBytes, 512L * 1024 * 1024))
                        throw new InvalidDataException("The separation model file exceeds the size limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
            }
            await provider.ValidateModelAsync(path, cancellationToken).ConfigureAwait(false);
            string id;
            using (var source = File.OpenRead(path))
                id = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            catalogId ??= settings.Models.FirstOrDefault(model => model.Id == id)?.CatalogId;
            var definition = GetModelCatalog(CultureInfo.InvariantCulture).Models.FirstOrDefault(model => model.Id == catalogId);
            var installed = new AudioSeparationModel(id, definition?.Name ?? Path.GetFileNameWithoutExtension(file.Name), file.Name, catalogId);
            var directory = Path.Combine(modelDirectory, id);
            Directory.CreateDirectory(directory);
            File.Move(path, Path.Combine(directory, file.Name), true);
            var modelId = select || !Status.ModelAvailable ? id : settings.ModelId;
            playbackChanged = modelId != settings.ModelId;
            SaveSettings(settings with
            {
                Models = settings.Models.Where(model => model.Id != id).Append(installed).ToArray(),
                ModelId = modelId,
            });
            Update(current => current with { RuntimeAvailable = true, Error = null });
            PublishSettings();
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
        if (playbackChanged)
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SelectModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!settings.Models.Any(model => model.Id == modelId && ModelExists(model)))
                throw new ArgumentException("Select an installed separation model.", nameof(modelId));
            SaveSettings(settings with { ModelId = modelId });
            Update(current => current with { RuntimeAvailable = true, Error = null });
            PublishSettings();
        }
        finally
        {
            lifecycle.Release();
        }
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var playbackChanged = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var selected = settings.Models.Single(model => model.Id == modelId);
            var remaining = settings.Models.Where(model => model.Id != modelId).ToArray();
            playbackChanged = settings.ModelId == modelId;
            var active = playbackChanged ? remaining.FirstOrDefault(ModelExists)?.Id : settings.ModelId;
            SaveSettings(settings with
            {
                Models = remaining, ModelId = active,
                Options = active is null ? settings.Options with { Enabled = false } : settings.Options,
            });
            PublishSettings();
            var directory = Path.Combine(modelDirectory, selected.Id);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        finally
        {
            lifecycle.Release();
            if (playbackChanged)
                PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task DownloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!GetModelCatalog(CultureInfo.InvariantCulture).Models.Any(model => model.Id == modelId))
            throw new ArgumentException("Select an available separation model.", nameof(modelId));
        if (Status.Models.Any(model => model.CatalogId == modelId) &&
            Status.Downloads.All(download => download.ModelId != modelId || download.State == InferenceModelDownloadState.NotStarted))
            return;
        await downloads.StartAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    public void PauseModelDownload(string modelId) => downloads.Pause(modelId);
    public Task CancelModelDownloadAsync(string modelId) => downloads.CancelAsync(modelId);

    public IAudioSeparationSession? CreateSession(CancellationToken cancellationToken) =>
        CreateSessionAsync(cancellationToken).GetAwaiter().GetResult();

    public async Task<IAudioSeparationSession?> CreateSessionAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var selectedTrack = Track;
            if (!Status.IsAvailable || selectedTrack == AudioSeparationTrack.Original)
                return null;
            var model = settings.Models.Single(model => model.Id == settings.ModelId);
            var session = await provider.CreateSessionAsync(ModelPath(model), settings.Options.DeviceId,
                selectedTrack == AudioSeparationTrack.Second, cancellationToken).ConfigureAwait(false);
            sessions.Add(session, new SessionGeneration(generation));
            ReportDevice(session.ActiveDeviceId, session);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            return null;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public void ReportDevice(string deviceId, IAudioSeparationSession? session = null)
    {
        if (Status.ActiveDeviceId != deviceId)
            Update(current => current with { ActiveDeviceId = deviceId }, session);
    }

    public void ReportFailure(Exception exception, IAudioSeparationSession? session = null)
    {
        AppLog.Error("AudioSeparation", "Audio separation is unavailable; original audio remains available.", exception);
        Update(current => current with { RuntimeAvailable = false, Error = exception.Message }, session, releaseCachedSession: true);
    }

    private void OnDownloadsChanged(object? sender, EventArgs eventArgs) => Update(current => current with { Downloads = downloads.Status });
    private string ModelPath(AudioSeparationModel model) => Path.Combine(modelDirectory, model.Id, model.FileName);
    private bool ModelExists(AudioSeparationModel model) => File.Exists(ModelPath(model));

    private void PublishSettings() => Update(current => current with
    {
        Options = settings.Options, ModelId = settings.ModelId, ActiveDeviceId = settings.Options.DeviceId,
        ModelAvailable = settings.Models.Any(model => model.Id == settings.ModelId && ModelExists(model)),
        Models = settings.Models.Where(ModelExists).Select(model => new InferenceInstalledModel(model.Id, model.Name, model.CatalogId)).ToArray(),
        Downloads = downloads.Status,
    });

    private void Update(Func<AudioSeparationStatus, AudioSeparationStatus> change, IAudioSeparationSession? session = null,
        bool releaseCachedSession = false)
    {
        lock (sync)
        {
            if (session is not null && (!sessions.TryGetValue(session, out var owner) || owner.Value != generation))
                return;
            if (releaseCachedSession)
                provider.ReleaseCachedSession();
            Volatile.Write(ref status, change(status));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AudioSeparationSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath))
                return new();
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), AudioSeparationJsonContext.Default.AudioSeparationSettings)
                ?? throw new InvalidDataException("Invalid separation settings.");
            if (loaded.Options is null || loaded.Models is null || loaded.Models.Any(model => model is null ||
                model.Id is not { Length: 64 } || !model.Id.All(Uri.IsHexDigit) || !IsFileName(model.FileName)) ||
                loaded.Models.Select(model => model.Id).Distinct().Count() != loaded.Models.Length)
                throw new InvalidDataException("Invalid separation model settings.");
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppLog.Warning("AudioSeparation", "Could not load audio separation settings.", exception);
            return new();
        }
    }

    private static bool IsFileName(string name) => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\', ':']) < 0 &&
        name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private void SaveSettings(AudioSeparationSettings replacement)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(replacement, AudioSeparationJsonContext.Default.AudioSeparationSettings));
        File.Move(temporary, settingsPath, true);
        lock (sync)
        {
            if (settings.Options != replacement.Options || settings.ModelId != replacement.ModelId)
            {
                generation++;
                provider.ReleaseCachedSession();
            }
            settings = replacement;
        }
    }

    public void Dispose()
    {
        lifecycle.Wait();
        try
        {
            if (disposed)
                return;
            disposed = true;
            downloads.Changed -= OnDownloadsChanged;
            provider.ReleaseCachedSession();
        }
        finally
        {
            lifecycle.Release();
        }
        downloads.Dispose();
    }

    private sealed record SessionGeneration(int Value);
}

internal sealed record AudioSeparationSettings
{
    public AudioSeparationOptions Options { get; init; } = new();
    public string? ModelId { get; init; }
    public AudioSeparationModel[] Models { get; init; } = [];
}

internal sealed record AudioSeparationModel(string Id, string Name, string FileName, string? CatalogId);

[JsonSerializable(typeof(AudioSeparationSettings))]
internal partial class AudioSeparationJsonContext : JsonSerializerContext;