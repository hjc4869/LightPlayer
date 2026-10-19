using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class InferenceModelSettingsViewModel : ObservableObject
{
    private readonly IInferenceModelSettingsHost owner;
    private InferenceModelDefinition? definition;
    private readonly InferenceInstalledModel? imported;
    private IReadOnlyList<InferenceInstalledModel> installed = [];
    private InferenceModelDownload? download;

    internal InferenceModelSettingsViewModel(IInferenceModelSettingsHost owner, InferenceModelDefinition definition)
    {
        this.owner = owner;
        this.definition = definition;
    }

    internal InferenceModelSettingsViewModel(IInferenceModelSettingsHost owner, InferenceInstalledModel imported)
    {
        this.owner = owner;
        this.imported = imported;
    }

    internal string? ImportedId => imported?.Id;
    internal string? CatalogId => definition?.Id;
    public string Name => definition?.Name ?? imported!.Name;
    public string Description => definition?.Description ?? Resources.Inference_Imported;
    public Uri? DownloadUri => definition?.DownloadUri;
    public bool HasDownloadLink => DownloadUri is { IsAbsoluteUri: true, Scheme: "https" or "http" };

    private InferenceModelDownloadState State => download?.State ?? InferenceModelDownloadState.NotStarted;
    public bool IsInstalled => installed.Count > 0;
    public bool HasTransfer => State != InferenceModelDownloadState.NotStarted;
    public bool ShowDelete => IsInstalled && !HasTransfer;
    public bool ShowDownload => !IsInstalled && !HasTransfer && definition is not null && owner.CanDownload(definition.Id);
    public bool ShowResume => HasTransfer && State is InferenceModelDownloadState.Paused or InferenceModelDownloadState.Failed;
    public bool ShowPause => HasTransfer && !ShowResume;
    public bool HasProgress => HasTransfer && download?.TotalBytes > 0;
    public double Progress => download is { TotalBytes: > 0 } ? Math.Clamp(100d * download.ReceivedBytes / download.TotalBytes, 0, 100) : 0;
    public string? Error => download?.Error;
    public string StatusText => State switch
    {
        InferenceModelDownloadState.Downloading => Resources.Inference_Downloading,
        InferenceModelDownloadState.Paused => Resources.Inference_Paused,
        InferenceModelDownloadState.Verifying => Resources.Inference_Verifying,
        InferenceModelDownloadState.Installing => Resources.Inference_Installing,
        InferenceModelDownloadState.Canceling => Resources.Inference_Canceling,
        InferenceModelDownloadState.Failed => Resources.Inference_DownloadFailed,
        _ => string.Empty,
    };
    public string ProgressText => download is { TotalBytes: > 0 }
        ? $"{FormatSize(download.ReceivedBytes)} / {FormatSize(download.TotalBytes)}" : string.Empty;

    private bool CanDownload => definition is not null && owner.CanDownload(definition.Id) && !owner.IsChanging && (ShowDownload || ShowResume);
    private bool CanPause => State is InferenceModelDownloadState.Downloading or InferenceModelDownloadState.Verifying;
    private bool CanCancel => HasTransfer && State is not (InferenceModelDownloadState.Installing or InferenceModelDownloadState.Canceling);
    private bool CanDelete => ShowDelete && !owner.IsChanging;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => AppErrorReporter.GuardAsync(() => owner.DownloadModelAsync(definition!.Id));

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => owner.PauseModelDownload(definition!.Id);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => AppErrorReporter.GuardAsync(() => owner.CancelModelDownloadAsync(definition!.Id));

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => owner.DeleteModelsAsync(installed);

    internal void UpdateDefinition(InferenceModelDefinition value) => definition = value;

    internal void Refresh()
    {
        installed = owner.AllInstalledModels.Where(model => definition is null ? model.Id == imported!.Id : model.CatalogId == definition.Id).ToArray();
        download = owner.Downloads.FirstOrDefault(candidate => candidate.ModelId == definition?.Id);
        OnPropertyChanged(string.Empty);
        DownloadCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GiB"
        : (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";
}

internal interface IInferenceModelSettingsHost
{
    bool IsChanging { get; }
    IReadOnlyList<InferenceInstalledModel> AllInstalledModels { get; }
    IReadOnlyList<InferenceModelDownload> Downloads { get; }
    bool CanDownload(string modelId);
    Task DownloadModelAsync(string modelId);
    void PauseModelDownload(string modelId);
    Task CancelModelDownloadAsync(string modelId);
    Task DeleteModelsAsync(IReadOnlyList<InferenceInstalledModel> models);
}