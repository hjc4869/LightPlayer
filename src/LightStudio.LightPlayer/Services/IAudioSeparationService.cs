using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

public interface IAudioSeparationService : IDisposable
{
    event EventHandler? Changed;
    event EventHandler? PlaybackChanged;
    AudioSeparationStatus Status { get; }
    AudioSeparationTrack Track { get; set; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    InferenceModelCatalog GetModelCatalog(CultureInfo culture);
    Task ConfigureAsync(AudioSeparationOptions options, CancellationToken cancellationToken = default);
    Task ImportModelAsync(InferenceModelFile file, CancellationToken cancellationToken = default);
    Task SelectModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task DeleteModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task DownloadModelAsync(string modelId, CancellationToken cancellationToken = default);
    void PauseModelDownload(string modelId);
    Task CancelModelDownloadAsync(string modelId);
    IAudioSeparationSession? CreateSession(CancellationToken cancellationToken);
    Task<IAudioSeparationSession?> CreateSessionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CreateSession(cancellationToken));
    void ReportDevice(string deviceId, IAudioSeparationSession? session = null);
    void ReportFailure(Exception exception, IAudioSeparationSession? session = null);
}

public sealed record AudioSeparationOptions(bool Enabled = false, string DeviceId = "cpu");

public sealed record AudioSeparationStatus
{
    public bool RuntimeAvailable { get; init; }
    public bool ModelAvailable { get; init; }
    public AudioSeparationOptions Options { get; init; } = new();
    public string? ModelId { get; init; }
    public IReadOnlyList<InferenceInstalledModel> Models { get; init; } = [];
    public IReadOnlyList<InferenceDevice> Devices { get; init; } = [];
    public IReadOnlyList<InferenceModelDownload> Downloads { get; init; } = [];
    public string ActiveDeviceId { get; init; } = "cpu";
    public string? Error { get; init; }
    public bool CanEnable => RuntimeAvailable && ModelAvailable;
    public bool IsAvailable => CanEnable && Options.Enabled;
}